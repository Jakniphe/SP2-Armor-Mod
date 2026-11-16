using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace ArmorFragments
{
    // REAL GEOMETRY FOR A DAMAGED PLATE (experimental). The plate's own mesh is rewritten, in place, as two fine grids (the front and the
    // back) pushed in or out by the damage's height field, joined by four side strips. A hole is a run of grid cells that is not
    // there. The mesh keeps what the game put on it (UV1 holds the paint's material, the decal layer and the part's id), the plate's own
    // material stays on it (so its paint, its texture and its decals are not touched, and a hole needs no other material), and the
    // normal map's UV0 is written as before.
    //
    // Only a flat, rectangular slab with one submesh and the usual vertex channels can be rewritten like this; any other plate is left
    // as it is (and keeps the maps). The rewriting is done on the worker thread, from arrays: nothing here touches a Unity object
    // except PlateMeshSource.Read, which runs once on the main thread.

    // A plate's original mesh as plain arrays, with each vertex placed in the plate's frame.
    internal sealed class PlateMeshSource
    {
        private const int TableSize = 16;

        public Vector3[] Vertices;     // mesh space
        public Vector3[] Normals;
        public int[] Triangles;
        public int Uv1Dimension;       // 0 = the mesh has no UV1
        public Matrix4x4 FromPart;     // part-local space to mesh space
        public Vector4[] FrontUv1;     // UV1 of the nearest original vertex, on a TableSize x TableSize lattice of the plate (front face)
        public Vector4[] BackUv1;
        public string Why;             // null = the plate can be rewritten; else why not

        public bool Ok
        {
            get { return Why == null; }
        }

        // The vertex channels a rewritten mesh can carry (anything else would be lost).
        private static bool Supported(VertexAttribute attribute)
        {
            return attribute == VertexAttribute.Position || attribute == VertexAttribute.Normal || attribute == VertexAttribute.Tangent ||
                   attribute == VertexAttribute.TexCoord0 || attribute == VertexAttribute.TexCoord1;
        }

        public static PlateMeshSource Read(PlateInfo info)
        {
            PlateMeshSource s = new PlateMeshSource();
            try
            {
                Fill(s, info);
            }
            catch (Exception e)
            {
                s.Why = "it could not be read: " + e.Message;
            }
            return s;
        }

        private static void Fill(PlateMeshSource s, PlateInfo info)
        {
            Mesh mesh = info.Mesh;
            PlateGeometry g = info.Geometry;
            if (mesh == null || !mesh.isReadable) { s.Why = "its mesh is not readable"; return; }
            if (g.Curved) { s.Why = "it is not a slab (it is thick or curved)"; return; }
            if (g.Depth < 0.0005f) { s.Why = "it has no thickness"; return; }
            if (mesh.subMeshCount != 1) { s.Why = "its mesh has " + mesh.subMeshCount + " submeshes"; return; }

            foreach (VertexAttributeDescriptor attribute in mesh.GetVertexAttributes())
            {
                if (!Supported(attribute.attribute)) { s.Why = "its mesh has a " + attribute.attribute + " channel"; return; }
            }

            s.Vertices = mesh.vertices;
            s.Normals = mesh.normals;
            s.Triangles = mesh.GetTriangles(0);
            int count = s.Vertices.Length;
            if (s.Normals.Length != count || s.Triangles.Length < 3) { s.Why = "its mesh has no normals"; return; }
            s.FromPart = info.ToPart.inverse;

            Vector4[] uv1 = null;
            if (mesh.HasVertexAttribute(VertexAttribute.TexCoord1))
            {
                s.Uv1Dimension = mesh.GetVertexAttributeDimension(VertexAttribute.TexCoord1);
                List<Vector4> list = new List<Vector4>(count);
                mesh.GetUVs(1, list);
                if (list.Count == count) uv1 = list.ToArray();
            }

            // Each vertex in the plate's frame, and which face it is on.
            float[] u = new float[count], v = new float[count], h = new float[count];
            int[] facing = new int[count];
            float half = g.Depth * 0.5f;
            for (int i = 0; i < count; i++)
            {
                Vector3 p = info.ToPart.MultiplyPoint3x4(s.Vertices[i]) - g.Origin;
                u[i] = Vector3.Dot(p, g.T1);
                v[i] = Vector3.Dot(p, g.T2);
                h[i] = Vector3.Dot(p, g.N);
                float along = Vector3.Dot(info.ToPart.MultiplyVector(s.Normals[i]).normalized, g.N);
                facing[i] = along > 0.8f ? 1 : along < -0.8f ? -1 : 0;
            }

            // The big faces must be flat, and together must fill the plate's rectangle (a taper or rounded corners are not a rectangle).
            float tolerance = 0.2f * g.Depth + 0.0002f;
            for (int i = 0; i < count; i++)
            {
                if (facing[i] != 0 && Mathf.Abs(h[i] - facing[i] * half) > tolerance)
                {
                    s.Why = "its faces are not flat";
                    return;
                }
            }
            double front = 0.0, back = 0.0;
            for (int t = 0; t + 2 < s.Triangles.Length; t += 3)
            {
                int a = s.Triangles[t], b = s.Triangles[t + 1], c = s.Triangles[t + 2];
                if (facing[a] == 1 && facing[b] == 1 && facing[c] == 1) front += Area(u, v, a, b, c);
                else if (facing[a] == -1 && facing[b] == -1 && facing[c] == -1) back += Area(u, v, a, b, c);
            }
            double rectangle = (double)g.SizeU * g.SizeV;
            if (front < 0.9 * rectangle || front > 1.05 * rectangle || back < 0.9 * rectangle || back > 1.05 * rectangle)
            {
                s.Why = "its outline is not a rectangle (the faces cover " + (front / rectangle).ToString("P0") + " and " +
                        (back / rectangle).ToString("P0") + " of it)";
                return;
            }

            s.FrontUv1 = Table(uv1, u, v, facing, 1, g);
            s.BackUv1 = Table(uv1, u, v, facing, -1, g);
        }

        private static double Area(float[] u, float[] v, int a, int b, int c)
        {
            double cross = (double)(u[b] - u[a]) * (v[c] - v[a]) - (double)(u[c] - u[a]) * (v[b] - v[a]);
            return Math.Abs(cross) * 0.5;
        }

        // For each cell of a coarse lattice over the plate: the UV1 of the original vertex of that face nearest to the cell's middle.
        private static Vector4[] Table(Vector4[] uv1, float[] u, float[] v, int[] facing, int face, PlateGeometry g)
        {
            Vector4[] table = new Vector4[TableSize * TableSize];
            if (uv1 == null) return table;

            for (int cy = 0; cy < TableSize; cy++)
            {
                for (int cx = 0; cx < TableSize; cx++)
                {
                    float pu = (cx + 0.5f) / TableSize * g.SizeU, pv = (cy + 0.5f) / TableSize * g.SizeV;
                    float best = float.MaxValue;
                    int pick = -1;
                    for (int i = 0; i < uv1.Length; i++)
                    {
                        if (facing[i] != face) continue;
                        float d = (u[i] - pu) * (u[i] - pu) + (v[i] - pv) * (v[i] - pv);
                        if (d < best) { best = d; pick = i; }
                    }
                    if (pick < 0) pick = 0;
                    table[cy * TableSize + cx] = uv1[pick];
                }
            }
            return table;
        }

        public Vector4 Uv1At(int face, float u, float v, PlateGeometry g)
        {
            Vector4[] table = face == 0 ? FrontUv1 : BackUv1;
            int cx = Mathf.Clamp((int)(u / g.SizeU * TableSize), 0, TableSize - 1);
            int cy = Mathf.Clamp((int)(v / g.SizeV * TableSize), 0, TableSize - 1);
            return table[cy * TableSize + cx];
        }
    }

    // A rewritten plate mesh, ready to be put into the game's mesh.
    internal sealed class MeshData
    {
        public PlateVisual Plate;
        public Vector3[] Vertices;
        public Vector3[] Normals;
        public Vector2[] Uv0;
        public Vector4[] Uv1;          // null if the mesh has no UV1
        public int[] Triangles;
        public float DeliverAt;        // Time.time from which it may be shown (the shell's ray has to arrive first)
        public double BuildMs;
        public int Columns, Rows;      // the grid's cells along the plate's length and width
    }

    internal static class PlateMesher
    {
        // The plate rewritten as grids of about 'cellMeters' (a coarser one if that would pass 'maxVertices').
        public static MeshData Build(PlateCpu cpu, PlateGeometry g, PlateMeshSource s, VehicleMaps owner, float cellMeters, int maxVertices)
        {
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();

            float r = Mathf.Max(cellMeters, 0.002f);
            int nu, nv;
            while (true)
            {
                nu = Mathf.Max(1, Mathf.CeilToInt(g.SizeU / r));
                nv = Mathf.Max(1, Mathf.CeilToInt(g.SizeV / r));
                long vertices = 2L * (nu + 1) * (nv + 1) + 8L * (nu + nv);
                if (vertices <= maxVertices) break;
                r *= 1.25f;
            }
            float du = g.SizeU / nu, dv = g.SizeV / nv;
            int row = nu + 1;
            int faceVertices = row * (nv + 1);
            int wallVertices = 8 * (nu + nv);
            float half = g.Depth * 0.5f;

            Vector3[] positions = new Vector3[2 * faceVertices + wallVertices];
            Vector3[] normals = new Vector3[positions.Length];
            Vector2[] uv0 = new Vector2[positions.Length];
            Vector4[] uv1 = s.Uv1Dimension > 0 ? new Vector4[positions.Length] : null;
            List<int> triangles = new List<int>(6 * nu * nv * 2 + 12 * (nu + nv));

            float atlas = owner.AtlasSize;
            int innerW = cpu.Width - 2 * PlateCpu.Border, innerH = cpu.Height - 2 * PlateCpu.Border;
            Vector2 sentinel = owner.SentinelUv();

            for (int face = 0; face < 2; face++)
            {
                PlateCpu.Face damage = cpu.Faces[face];
                Vector3 outward = face == 0 ? g.N : -g.N;
                int baseIndex = face * faceVertices;

                float[] height = new float[faceVertices];
                for (int j = 0; j <= nv; j++)
                    for (int i = 0; i <= nu; i++)
                        height[j * row + i] = Sample(cpu, damage, i * du, j * dv);

                for (int j = 0; j <= nv; j++)
                {
                    for (int i = 0; i <= nu; i++)
                    {
                        float pu = i * du, pv = j * dv;
                        float hu = (height[j * row + Math.Min(i + 1, nu)] - height[j * row + Math.Max(i - 1, 0)]) / ((Math.Min(i + 1, nu) - Math.Max(i - 1, 0)) * du);
                        float hv = (height[Math.Min(j + 1, nv) * row + i] - height[Math.Max(j - 1, 0) * row + i]) / ((Math.Min(j + 1, nv) - Math.Max(j - 1, 0)) * dv);

                        Vector3 partPosition = g.Origin + g.T1 * pu + g.T2 * pv + outward * (half + height[j * row + i]);
                        Vector3 partNormal = (outward - g.T1 * hu - g.T2 * hv).normalized;

                        int at = baseIndex + j * row + i;
                        positions[at] = s.FromPart.MultiplyPoint3x4(partPosition);
                        normals[at] = s.FromPart.MultiplyVector(partNormal).normalized;
                        uv0[at] = new Vector2((cpu.TileX[face] + PlateCpu.Border + pu / g.SizeU * innerW) / atlas,
                                              (cpu.TileY[face] + PlateCpu.Border + pv / g.SizeV * innerH) / atlas);
                        if (uv1 != null) uv1[at] = s.Uv1At(face, pu, pv, g);
                    }
                }

                // Which way round the triangles go is worked out once, from the mesh-space positions (a mirrored part flips it).
                Vector3 outwardMesh = s.FromPart.MultiplyVector(outward);
                Vector3 pa = positions[baseIndex], pb = positions[baseIndex + 1], pc = positions[baseIndex + row];
                bool flip = Vector3.Dot(Vector3.Cross(pb - pa, pc - pa), outwardMesh) < 0f;

                for (int j = 0; j < nv; j++)
                {
                    for (int i = 0; i < nu; i++)
                    {
                        if (Masked(cpu, damage, (i + 0.5f) * du, (j + 0.5f) * dv)) continue;   // a hole: no surface here
                        int a = baseIndex + j * row + i, b = a + 1, c = a + row, d = c + 1;
                        if (!flip) { Add(triangles, a, b, c); Add(triangles, b, d, c); }
                        else { Add(triangles, a, c, b); Add(triangles, b, c, d); }
                    }
                }
            }

            // The four sides: a strip along each edge, joining the front's edge vertices to the back's.
            int wall = 2 * faceVertices;
            for (int side = 0; side < 4; side++)
            {
                bool alongU = side < 2;                 // sides 0, 1 run along the length (v = 0 and v = max); 2, 3 along the width
                bool far = (side & 1) == 1;
                int count = alongU ? nu : nv;
                Vector3 outwardPart = alongU ? (far ? g.T2 : -g.T2) : (far ? g.T1 : -g.T1);
                Vector3 outwardMesh = s.FromPart.MultiplyVector(outwardPart).normalized;

                for (int k = 0; k < count; k++)
                {
                    int f0 = EdgeIndex(alongU, far, k, nu, nv, row), f1 = EdgeIndex(alongU, far, k + 1, nu, nv, row);
                    int b0 = faceVertices + f0, b1 = faceVertices + f1;
                    int q = wall;
                    wall += 4;
                    positions[q] = positions[f0]; positions[q + 1] = positions[f1]; positions[q + 2] = positions[b0]; positions[q + 3] = positions[b1];
                    for (int m = 0; m < 4; m++)
                    {
                        normals[q + m] = outwardMesh;
                        uv0[q + m] = sentinel;
                        if (uv1 != null) uv1[q + m] = uv1[f0];
                    }

                    bool flip = Vector3.Dot(Vector3.Cross(positions[q + 1] - positions[q], positions[q + 2] - positions[q]), outwardMesh) < 0f;
                    if (!flip) { Add(triangles, q, q + 1, q + 2); Add(triangles, q + 1, q + 3, q + 2); }
                    else { Add(triangles, q, q + 2, q + 1); Add(triangles, q + 1, q + 2, q + 3); }
                }
            }

            return new MeshData
            {
                Plate = null,
                Vertices = positions, Normals = normals, Uv0 = uv0, Uv1 = uv1, Triangles = triangles.ToArray(),
                BuildMs = watch.Elapsed.TotalMilliseconds, Columns = nu, Rows = nv,
            };
        }

        private static void Add(List<int> list, int a, int b, int c)
        {
            list.Add(a); list.Add(b); list.Add(c);
        }

        // The vertex of the front grid at step k along an edge (the back grid's is the same offset by one face).
        private static int EdgeIndex(bool alongU, bool far, int k, int nu, int nv, int row)
        {
            if (alongU) return (far ? nv : 0) * row + k;
            return k * row + (far ? nu : 0);
        }

        // The height of a face at (u, v) meters of the plate, smoothly between the pixels. Zero where the face has no damage.
        private static float Sample(PlateCpu c, PlateCpu.Face f, float u, float v)
        {
            if (f == null) return 0f;
            float x = Mathf.Clamp(PlateCpu.Border + u * c.PxU, 0f, c.Width - 1.001f);
            float y = Mathf.Clamp(PlateCpu.Border + v * c.PxV, 0f, c.Height - 1.001f);
            int x0 = (int)x, y0 = (int)y;
            float fx = x - x0, fy = y - y0;
            float a = f.Height[y0 * c.Width + x0], b = f.Height[y0 * c.Width + x0 + 1];
            float d = f.Height[(y0 + 1) * c.Width + x0], e = f.Height[(y0 + 1) * c.Width + x0 + 1];
            return (a * (1f - fx) + b * fx) * (1f - fy) + (d * (1f - fx) + e * fx) * fy;
        }

        private static bool Masked(PlateCpu c, PlateCpu.Face f, float u, float v)
        {
            if (f == null) return false;
            int x = Mathf.Clamp(Mathf.RoundToInt(PlateCpu.Border + u * c.PxU), 0, c.Width - 1);
            int y = Mathf.Clamp(Mathf.RoundToInt(PlateCpu.Border + v * c.PxV), 0, c.Height - 1);
            return f.Mask[y * c.Width + x] < 128;
        }
    }
}
