using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace ArmorFragments
{
    // REAL GEOMETRY FOR A DAMAGED PLATE (experimental). The plate's own mesh is rewritten, in place: the triangles of its two big faces
    // (the ones the damage maps cover) are cut into many small ones, and every new vertex is pushed in or out along its normal by the
    // damage's height field. A hole is small triangles that are not there. Nothing about the plate's shape is assumed: its outline,
    // its taper or rounded corners and a face that is not quite flat are all kept, because the new triangles are made from the old ones.
    //
    // The rest of the mesh (the sides, the ends) is left exactly as it was, and every vertex keeps what the game put on it (UV1 holds
    // the paint's material, the decal layer and the part's id). The plate keeps its own material, so its paint, its texture and its
    // decals stay, and a hole needs no other material. The normal map's UV0 is written as the maps do.
    //
    // Every triangle that is cut is cut the same way (each edge into the same number of equal pieces), and a point on an edge is worked
    // out from the edge's two ends in a fixed order, so the two triangles that share an edge make the very same points: no cracks.
    // Only PlateMeshSource.Read touches a Unity object; Build runs on the worker thread, from arrays.

    // A plate's original mesh as plain arrays, with the part-space copies the rewriting works in.
    internal sealed class PlateMeshSource
    {
        public Vector3[] Vertices;        // mesh space
        public Vector3[] Normals;
        public Vector2[] Uv0;             // as the maps left them (into the atlas)
        public Vector4[] Uv1;             // null if the mesh has none
        public int Uv1Dimension;          // 0 = none
        public int[] Triangles;
        public Vector3[] PartPositions;   // the same vertices and normals in the part's frame (where the plate's axes are)
        public Vector3[] PartNormals;
        public sbyte[] Face;              // per triangle: 0 = on the front face, 1 = on the back face, -1 = neither (a side, an end)
        public int FaceTriangles;
        public Matrix4x4 FromPart;        // part-local space to mesh space
        public string Why;                // null = the plate can be rewritten; else why not

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

            List<Vector2> uv0 = new List<Vector2>(count);
            mesh.GetUVs(0, uv0);
            if (uv0.Count != count) { s.Why = "its mesh has no UV0"; return; }
            s.Uv0 = uv0.ToArray();

            if (mesh.HasVertexAttribute(VertexAttribute.TexCoord1))
            {
                s.Uv1Dimension = mesh.GetVertexAttributeDimension(VertexAttribute.TexCoord1);
                List<Vector4> uv1 = new List<Vector4>(count);
                mesh.GetUVs(1, uv1);
                if (uv1.Count == count) s.Uv1 = uv1.ToArray();
                else s.Uv1Dimension = 0;
            }

            s.FromPart = info.ToPart.inverse;
            s.PartPositions = new Vector3[count];
            s.PartNormals = new Vector3[count];
            sbyte[] facing = new sbyte[count];
            for (int i = 0; i < count; i++)
            {
                s.PartPositions[i] = info.ToPart.MultiplyPoint3x4(s.Vertices[i]);
                s.PartNormals[i] = info.ToPart.MultiplyVector(s.Normals[i]).normalized;
                float along = Vector3.Dot(s.PartNormals[i], g.N);
                facing[i] = (sbyte)(along > 0.8f ? 1 : along < -0.8f ? -1 : 0);
            }

            int triangles = s.Triangles.Length / 3;
            s.Face = new sbyte[triangles];
            for (int t = 0; t < triangles; t++)
            {
                sbyte a = facing[s.Triangles[3 * t]], b = facing[s.Triangles[3 * t + 1]], c = facing[s.Triangles[3 * t + 2]];
                if (a == 1 && b == 1 && c == 1) { s.Face[t] = 0; s.FaceTriangles++; }
                else if (a == -1 && b == -1 && c == -1) { s.Face[t] = 1; s.FaceTriangles++; }
                else s.Face[t] = -1;
            }
            if (s.FaceTriangles == 0) s.Why = "it has no triangles on its big faces";
        }
    }

    // A rewritten plate mesh, ready to be put into the game's mesh.
    internal sealed class MeshData
    {
        public PlateVisual Plate;
        public Vector3[] Vertices;
        public Vector3[] Normals;
        public Vector2[] Uv0;
        public Vector4[] Uv1;          // null if the mesh has none
        public int[] Triangles;
        public float DeliverAt;        // Time.time from which it may be shown (the shell's ray has to arrive first)
        public double BuildMs;
        public int Segments;           // pieces each edge of a cut triangle was cut into
        public int Cut;                // triangles that were cut
    }

    internal static class PlateMesher
    {
        private const int MaxLevel = 8;

        // The plate rewritten with its big faces cut into pieces of about 'cellMeters' (coarser if that would pass 'maxVertices').
        public static MeshData Build(PlateCpu cpu, PlateGeometry g, PlateMeshSource s, VehicleMaps owner, float cellMeters, int maxVertices)
        {
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();

            float cell = Mathf.Max(cellMeters, 0.002f);
            float cutAbove = cell * 1.5f;   // a triangle already this small is left as it is
            int triangles = s.Triangles.Length / 3;

            // How many pieces each edge is cut into: the same for every triangle that is cut (a power of two), the finest that fits.
            float[] longest = new float[triangles];
            float biggest = 0f;
            for (int t = 0; t < triangles; t++)
            {
                if (s.Face[t] < 0) continue;
                Vector3 a = s.PartPositions[s.Triangles[3 * t]], b = s.PartPositions[s.Triangles[3 * t + 1]], c = s.PartPositions[s.Triangles[3 * t + 2]];
                longest[t] = Mathf.Max((b - a).magnitude, Mathf.Max((c - b).magnitude, (a - c).magnitude));
                if (longest[t] > biggest) biggest = longest[t];
            }
            int level = 0;
            while (level < MaxLevel && biggest / (1 << level) > cell) level++;
            while (level > 0 && CountVertices(s, longest, cutAbove, 1 << level) > maxVertices) level--;
            int n = 1 << level;

            List<Vector3> vertices = new List<Vector3>(s.Vertices);
            List<Vector3> normals = new List<Vector3>(s.Normals);
            List<Vector2> uv0 = new List<Vector2>(s.Uv0);
            List<Vector4> uv1 = s.Uv1 != null ? new List<Vector4>(s.Uv1) : null;
            List<int> output = new List<int>(s.Triangles.Length);

            float atlas = owner.AtlasSize;
            int innerW = cpu.Width - 2 * PlateCpu.Border, innerH = cpu.Height - 2 * PlateCpu.Border;
            int cut = 0;

            for (int t = 0; t < triangles; t++)
            {
                int a = s.Triangles[3 * t], b = s.Triangles[3 * t + 1], c = s.Triangles[3 * t + 2];
                if (s.Face[t] < 0 || n == 1 || longest[t] <= cutAbove)
                {
                    output.Add(a); output.Add(b); output.Add(c);
                    continue;
                }
                Cut(cpu, g, s, owner, s.Face[t], a, b, c, n, atlas, innerW, innerH, vertices, normals, uv0, uv1, output);
                cut++;
            }

            return new MeshData
            {
                Vertices = vertices.ToArray(), Normals = normals.ToArray(), Uv0 = uv0.ToArray(),
                Uv1 = uv1 != null ? uv1.ToArray() : null, Triangles = output.ToArray(),
                BuildMs = watch.Elapsed.TotalMilliseconds, Segments = n, Cut = cut,
            };
        }

        // The vertices the rewritten mesh would have with each cut triangle's edges in 'n' pieces.
        private static long CountVertices(PlateMeshSource s, float[] longest, float cutAbove, int n)
        {
            long total = s.Vertices.Length;
            long perTriangle = (long)(n + 1) * (n + 2) / 2;
            for (int t = 0; t < longest.Length; t++)
                if (s.Face[t] >= 0 && longest[t] > cutAbove) total += perTriangle;
            return total;
        }

        // One triangle (a, b, c) of a big face cut into n x n small ones, each new vertex pushed by the damage.
        private static void Cut(PlateCpu cpu, PlateGeometry g, PlateMeshSource s, VehicleMaps owner, int face, int a, int b, int c, int n,
                                float atlas, int innerW, int innerH, List<Vector3> vertices, List<Vector3> normals, List<Vector2> uv0,
                                List<Vector4> uv1, List<int> output)
        {
            PlateCpu.Face damage = cpu.Faces[face];
            int baseIndex = vertices.Count;
            int[] rowStart = new int[n + 2];
            for (int j = 0; j <= n; j++) rowStart[j + 1] = rowStart[j] + (n - j + 1);
            Vector2[] plane = new Vector2[rowStart[n + 1]];   // each point's place on the plate (before it is pushed), for the hole test

            for (int j = 0; j <= n; j++)
            {
                for (int i = 0; i <= n - j; i++)
                {
                    Vector3 position, normal;
                    Point(s, a, b, c, i, j, n, out position, out normal);

                    Vector3 d = position - g.Origin;
                    float u = Vector3.Dot(d, g.T1), v = Vector3.Dot(d, g.T2);
                    plane[rowStart[j] + i] = new Vector2(u, v);

                    if (damage != null)
                    {
                        float height = Sample(cpu, damage, u, v);
                        float eu = 1f / cpu.PxU, ev = 1f / cpu.PxV;
                        float hu = (Sample(cpu, damage, u + eu, v) - Sample(cpu, damage, u - eu, v)) / (2f * eu);
                        float hv = (Sample(cpu, damage, u, v + ev) - Sample(cpu, damage, u, v - ev)) / (2f * ev);
                        position += normal * height;
                        normal = (normal - g.T1 * hu - g.T2 * hv).normalized;
                    }

                    vertices.Add(s.FromPart.MultiplyPoint3x4(position));
                    normals.Add(s.FromPart.MultiplyVector(normal).normalized);
                    uv0.Add(new Vector2((cpu.TileX[face] + PlateCpu.Border + Mathf.Clamp01(u / g.SizeU) * innerW) / atlas,
                                        (cpu.TileY[face] + PlateCpu.Border + Mathf.Clamp01(v / g.SizeV) * innerH) / atlas));
                    if (uv1 != null)
                    {
                        // The paint and decal data of whichever corner this point is nearest to.
                        int k = n - i - j;
                        int owner1 = k >= i && k >= j ? a : i >= j ? b : c;
                        uv1.Add(s.Uv1[owner1]);
                    }
                }
            }

            // The small triangles go the way the big one went. A hole in the face leaves the ones over it out.
            for (int j = 0; j < n; j++)
            {
                for (int i = 0; i < n - j; i++)
                {
                    int p00 = baseIndex + rowStart[j] + i, p10 = p00 + 1, p01 = baseIndex + rowStart[j + 1] + i;
                    if (!Hole(cpu, damage, plane[rowStart[j] + i], plane[rowStart[j] + i + 1], plane[rowStart[j + 1] + i]))
                    {
                        output.Add(p00); output.Add(p10); output.Add(p01);
                    }
                    if (i + j < n - 1)
                    {
                        int p11 = baseIndex + rowStart[j + 1] + i + 1;
                        if (!Hole(cpu, damage, plane[rowStart[j] + i + 1], plane[rowStart[j + 1] + i + 1], plane[rowStart[j + 1] + i]))
                        {
                            output.Add(p10); output.Add(p11); output.Add(p01);
                        }
                    }
                }
            }
        }

        // The point at lattice position (i, j) of triangle (a, b, c) cut into n pieces a side, and its normal, in the part's frame.
        // A point on an edge is computed from the edge's two ends in index order, so the neighbor triangle gets the same one.
        private static void Point(PlateMeshSource s, int a, int b, int c, int i, int j, int n, out Vector3 position, out Vector3 normal)
        {
            int k = n - i - j;   // pieces toward a
            if (j == 0) { Edge(s, a, b, i, n, out position, out normal); return; }
            if (i == 0) { Edge(s, a, c, j, n, out position, out normal); return; }
            if (k == 0) { Edge(s, b, c, j, n, out position, out normal); return; }

            float wa = k / (float)n, wb = i / (float)n, wc = j / (float)n;
            position = s.PartPositions[a] * wa + s.PartPositions[b] * wb + s.PartPositions[c] * wc;
            normal = (s.PartNormals[a] * wa + s.PartNormals[b] * wb + s.PartNormals[c] * wc).normalized;
        }

        // The point 'steps' pieces of 'n' of the way from vertex p to vertex q.
        private static void Edge(PlateMeshSource s, int p, int q, int steps, int n, out Vector3 position, out Vector3 normal)
        {
            if (p > q) { int swap = p; p = q; q = swap; steps = n - steps; }
            if (steps <= 0) { position = s.PartPositions[p]; normal = s.PartNormals[p]; return; }
            if (steps >= n) { position = s.PartPositions[q]; normal = s.PartNormals[q]; return; }

            float t = steps / (float)n;
            position = Vector3.Lerp(s.PartPositions[p], s.PartPositions[q], t);
            normal = Vector3.Lerp(s.PartNormals[p], s.PartNormals[q], t).normalized;
        }

        // The height of a face at (u, v) meters of the plate, smoothly between the pixels. Zero where the face has no damage.
        private static float Sample(PlateCpu c, PlateCpu.Face f, float u, float v)
        {
            float x = Mathf.Clamp(PlateCpu.Border + u * c.PxU, 0f, c.Width - 1.001f);
            float y = Mathf.Clamp(PlateCpu.Border + v * c.PxV, 0f, c.Height - 1.001f);
            int x0 = (int)x, y0 = (int)y;
            float fx = x - x0, fy = y - y0;
            float a = f.Height[y0 * c.Width + x0], b = f.Height[y0 * c.Width + x0 + 1];
            float d = f.Height[(y0 + 1) * c.Width + x0], e = f.Height[(y0 + 1) * c.Width + x0 + 1];
            return (a * (1f - fx) + b * fx) * (1f - fy) + (d * (1f - fx) + e * fx) * fy;
        }

        // Is a small triangle (by its three corners on the plate) over a hole? Its middle decides.
        private static bool Hole(PlateCpu c, PlateCpu.Face f, Vector2 p0, Vector2 p1, Vector2 p2)
        {
            if (f == null) return false;
            float u = (p0.x + p1.x + p2.x) / 3f, v = (p0.y + p1.y + p2.y) / 3f;
            int x = Mathf.Clamp(Mathf.RoundToInt(PlateCpu.Border + u * c.PxU), 0, c.Width - 1);
            int y = Mathf.Clamp(Mathf.RoundToInt(PlateCpu.Border + v * c.PxV), 0, c.Height - 1);
            return f.Mask[y * c.Width + x] < 128;
        }
    }
}
