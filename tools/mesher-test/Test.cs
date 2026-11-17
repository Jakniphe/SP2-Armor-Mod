using System;
using System.Collections.Generic;
using ArmorFragments;
using UnityEngine;

namespace Assets.Scripts.Craft.Parts { internal sealed class Stub { } }

namespace ArmorFragments
{
    // Stand-ins for what the mesher only names.
    internal sealed class VehicleMaps { public int AtlasSize = 1024; }
    internal sealed class PlateVisual { }
    internal sealed class PlateInfo { public Mesh Mesh; public PlateGeometry Geometry; public Matrix4x4 ToPart; }
}

internal static class Test
{
    private static int failures;

    private static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "  OK   " : "  FAIL ") + what);
        if (!ok) failures++;
    }

    private static PlateGeometry Geometry()
    {
        return new PlateGeometry
        {
            Origin = Vector3.zero, T1 = Vector3.right, T2 = Vector3.up, N = Vector3.forward,
            SizeU = 1.6f, SizeV = 0.7f, Depth = 0.02f,
        };
    }

    // A tapered plate. With 'seam' the two triangles of each big face do NOT share their diagonal's vertices (as in a mesh whose UVs
    // are split there), so welding has to find the seam.
    private static PlateMeshSource Source(bool seam)
    {
        float h = 0.01f;
        Vector3[] c = { new Vector3(0, 0, 0), new Vector3(1.6f, 0, 0), new Vector3(1.4f, 0.7f, 0), new Vector3(0.2f, 0.7f, 0) };
        List<Vector3> vs = new List<Vector3>(), ns = new List<Vector3>();
        List<int> tris = new List<int>();
        List<sbyte> face = new List<sbyte>();

        // front face, triangles (0,2,1) and (0,3,2)
        for (int i = 0; i < 4; i++) { vs.Add(c[i] + new Vector3(0, 0, h)); ns.Add(Vector3.forward); }
        if (!seam)
        {
            tris.AddRange(new[] { 0, 2, 1, 0, 3, 2 });
        }
        else
        {
            // the second triangle gets its own copies of vertices 0 and 2
            vs.Add(vs[0]); ns.Add(Vector3.forward); vs.Add(vs[2]); ns.Add(Vector3.forward);   // 4 = copy of 0, 5 = copy of 2
            tris.AddRange(new[] { 0, 2, 1, 4, 3, 5 });
        }
        face.Add(0); face.Add(0);

        int backStart = vs.Count;
        for (int i = 0; i < 4; i++) { vs.Add(c[i] + new Vector3(0, 0, -h)); ns.Add(Vector3.back); }
        tris.AddRange(new[] { backStart, backStart + 1, backStart + 2, backStart, backStart + 2, backStart + 3 });
        face.Add(1); face.Add(1);

        int sideStart = vs.Count;
        vs.Add(new Vector3(0, 0, h)); ns.Add(Vector3.down); vs.Add(new Vector3(1.6f, 0, h)); ns.Add(Vector3.down); vs.Add(new Vector3(0, 0, -h)); ns.Add(Vector3.down);
        tris.AddRange(new[] { sideStart, sideStart + 1, sideStart + 2 });
        face.Add(-1);

        PlateMeshSource s = new PlateMeshSource
        {
            Vertices = vs.ToArray(), Normals = ns.ToArray(), Uv0 = new Vector2[vs.Count], Uv1 = new Vector4[vs.Count], Uv1Dimension = 4,
            Triangles = tris.ToArray(), PartPositions = vs.ToArray(), PartNormals = ns.ToArray(), Face = face.ToArray(),
            FaceTriangles = 4, FromPart = Matrix4x4.identity,
        };
        for (int i = 0; i < s.Uv1.Length; i++) s.Uv1[i] = new Vector4(i, 0, 7, 0);
        return s;
    }

    // Front face: a dent 0.02 deep (a Gaussian) at (0.7, 0.35) and a through hole of radius 0.04 at its middle.
    private static PlateCpu Damage()
    {
        PlateCpu cpu = new PlateCpu { Width = 400, Height = 200, PxU = (400 - 2 * PlateCpu.Border) / 1.6f, PxV = (200 - 2 * PlateCpu.Border) / 0.7f };
        PlateCpu.Face front = cpu.Get(0);
        for (int y = 0; y < cpu.Height; y++)
        {
            for (int x = 0; x < cpu.Width; x++)
            {
                float u = (x + 0.5f - PlateCpu.Border) / cpu.PxU, v = (y + 0.5f - PlateCpu.Border) / cpu.PxV;
                float r = Mathf.Sqrt((u - 0.7f) * (u - 0.7f) + (v - 0.35f) * (v - 0.35f));
                front.Height[y * cpu.Width + x] = -0.008f * Mathf.Exp(-(r * r) / (0.06f * 0.06f));
                {
                    float value = Mathf.Clamp(128f + (r - 0.04f) * cpu.PxU * 128f, 0f, 255f);
                    if (value < 255f) front.Mask[y * cpu.Width + x] = (byte)value;
                    // a thin crack running out of the hole to the right: 1 pixel wide, 1.5 mm deep
                    if (v > 0.35f - 0.5f / cpu.PxV && v < 0.35f + 0.5f / cpu.PxV && u > 0.75f && u < 0.9f) front.Height[y * cpu.Width + x] -= 0.0015f;
                }
            }
        }
        return cpu;
    }

    private static float DistanceToOutline(float x, float y)
    {
        Vector2[] poly = { new Vector2(0, 0), new Vector2(1.6f, 0), new Vector2(1.4f, 0.7f), new Vector2(0.2f, 0.7f) };
        float best = float.MaxValue;
        for (int i = 0; i < 4; i++)
        {
            Vector2 a = poly[i], b = poly[(i + 1) % 4], p = new Vector2(x, y);
            Vector2 ab = b - a;
            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
            best = Mathf.Min(best, (p - (a + ab * t)).magnitude);
        }
        return best;
    }

    private static long PositionKey(Vector3 p)
    {
        return ((long)Mathf.RoundToInt(p.x * 10000f) * 73856093L) ^ ((long)Mathf.RoundToInt(p.y * 10000f) * 19349663L) ^ ((long)Mathf.RoundToInt(p.z * 10000f) * 83492791L);
    }

    private static void Run(string name, bool seam, float cell, int budget, float fine = 0f)
    {
        Console.WriteLine("\n== " + name + " ==");
        PlateGeometry g = Geometry();
        PlateMeshSource s = Source(seam);
        PlateCpu cpu = Damage();
        VehicleMaps owner = new VehicleMaps();
        int sideTriangle = s.Triangles.Length / 3 - 1;

        MeshData m = PlateMesher.Build(cpu, g, s, owner, cell, fine, budget);
        Console.WriteLine("big-face triangles " + m.Before + " -> " + m.After + ", vertices " + m.Vertices.Length + ", triangles " + m.Triangles.Length / 3 +
                          ", moved " + m.Moved.Length + ", max push " + (m.MaxHeight * 1000f).ToString("F1") + " mm, built in " + m.BuildMs.ToString("F1") + " ms");

        bool finite = true;
        foreach (Vector3 p in m.Vertices) finite &= !float.IsNaN(p.x + p.y + p.z);
        foreach (Vector3 p in m.Normals) finite &= !float.IsNaN(p.x + p.y + p.z) && Mathf.Abs(p.magnitude - 1f) < 0.01f;
        Check(finite, "positions finite, normals unit length");
        Check(m.Vertices.Length == m.Normals.Length && m.Uv0.Length == m.Vertices.Length && m.BaseVertices.Length == m.Vertices.Length && m.Uv1.Length == m.Vertices.Length, "arrays agree in length");
        Check(m.Vertices.Length <= budget + 8, "vertex budget respected (" + m.Vertices.Length + " of " + budget + ")");
        Check(m.After > m.Before, "the damaged area was refined (" + m.Before + " -> " + m.After + ")");
        Check(Mathf.Abs(m.MaxHeight - 0.008f) < 0.0015f || budget < 2000, "max push about 8 mm (" + (m.MaxHeight * 1000f).ToString("F1") + ")");

        // Refined only around the damage: vertices far from it are the originals (the plate's left end has no new vertex).
        int farNew = 0;
        for (int i = s.Vertices.Length; i < m.Vertices.Length; i++)
            if (Mathf.Abs(m.BaseVertices[i].x - 0.7f) > 0.5f) farNew++;
        Check(farNew < 30 || budget < 2000, "no new vertices far from the damage (" + farNew + ")");

        // The side triangle passes through, indices unchanged.
        bool side = false;
        int s0 = s.Triangles[3 * sideTriangle], s1 = s.Triangles[3 * sideTriangle + 1], s2 = s.Triangles[3 * sideTriangle + 2];
        for (int t = 0; t + 2 < m.Triangles.Length; t += 3)
            if (m.Triangles[t] == s0 && m.Triangles[t + 1] == s1 && m.Triangles[t + 2] == s2) side = true;
        Check(side, "the side triangle is kept as it was");

        // The dent moved vertices, the base stays flat, and blending is a straight line between them.
        bool flat = true;
        for (int i = 0; i < m.BaseVertices.Length; i++)
            if (m.BaseNormals[i].z > 0.5f && Mathf.Abs(m.BaseVertices[i].z - 0.01f) > 1e-5f) flat = false;
        Check(flat, "the base (flat) front vertices all lie on the face");
        bool pushed = m.Moved.Length > 50;
        float lowest = 1f;
        foreach (int i in m.Moved) if (m.Vertices[i].z < lowest) lowest = m.Vertices[i].z;
        Check(pushed && lowest < 0.01f - 0.006f, "dent pushed in: " + m.Moved.Length + " vertices moved, lowest z " + lowest.ToString("F4") + " (rest 0.0100)");

        // The front face triangles: gather them, count edges by position (so a duplicated seam vertex counts as the same point).
        List<int> front = new List<int>();
        for (int t = 0; t + 2 < m.Triangles.Length; t += 3)
        {
            if (m.Normals[m.Triangles[t]].z > 0.5f && m.Normals[m.Triangles[t + 1]].z > 0.5f && m.Normals[m.Triangles[t + 2]].z > 0.5f) front.Add(t);
        }

        // Holes
        int insideHole = 0;
        foreach (int t in front)
        {
            Vector3 a = m.BaseVertices[m.Triangles[t]], b = m.BaseVertices[m.Triangles[t + 1]], c = m.BaseVertices[m.Triangles[t + 2]];
            Vector3 mid = (a + b + c) / 3f;
            if (Mathf.Sqrt((mid.x - 0.7f) * (mid.x - 0.7f) + (mid.y - 0.35f) * (mid.y - 0.35f)) < 0.035f) insideHole++;
        }
        Check(insideHole == 0, "no triangle over the hole (" + insideHole + " found)");
        float keptMin = 9f; int nearEdge = 0; double keptArea = 0, worst = 0;
        foreach (int t in front)
        {
            Vector3 a = m.BaseVertices[m.Triangles[t]], b = m.BaseVertices[m.Triangles[t + 1]], c = m.BaseVertices[m.Triangles[t + 2]];
            Vector3 mid = (a + b + c) / 3f;
            float rr = Mathf.Sqrt((mid.x - 0.7f) * (mid.x - 0.7f) + (mid.y - 0.35f) * (mid.y - 0.35f));
            if (rr < keptMin) keptMin = rr;
            if (rr > 0.03f && rr < 0.055f) nearEdge++;
            if (rr < 0.06f) keptArea += Vector3.Cross(b - a, c - a).magnitude * 0.5;
        }
        double expectedArea = Math.PI * (0.06 * 0.06 - 0.04 * 0.04);
        Console.WriteLine("  hole area: the metal left inside r = 60 mm is " + (keptArea * 1e4).ToString("F1") + " cm2 for " + (expectedArea * 1e4).ToString("F1") + " cm2 expected (" + (100 * (keptArea - expectedArea) / expectedArea).ToString("+0.0;-0.0") + " %: how much the hole's size is off, from its edge being a staircase of triangles)");
        Console.WriteLine("  hole edge: the hole is r = 0.0400 m; the nearest kept triangle's middle is at " + keptMin.ToString("F4") + " (error " + ((keptMin - 0.04f) * 1000f).ToString("F1") + " mm); triangles between r 30 and 55 mm: " + nearEdge);

        // Winding: where the face is flat the triangles go the way the originals do.
        Vector3 o0 = s.Vertices[s.Triangles[0]], o1 = s.Vertices[s.Triangles[1]], o2 = s.Vertices[s.Triangles[2]];
        float sign = Mathf.Sign(Vector3.Dot(Vector3.Cross(o1 - o0, o2 - o0), Vector3.forward));
        int wrong = 0;
        foreach (int t in front)
        {
            Vector3 a = m.BaseVertices[m.Triangles[t]], b = m.BaseVertices[m.Triangles[t + 1]], c = m.BaseVertices[m.Triangles[t + 2]];
            if (Mathf.Sign(Vector3.Dot(Vector3.Cross(b - a, c - a), Vector3.forward)) != sign) wrong++;
        }
        Check(wrong == 0, "front triangles keep the original winding (" + wrong + " wrong of " + front.Count + ")");

        // CONFORMITY: an edge used by one triangle only must be on the plate's outline or on the hole's rim; anywhere else it is a
        // crack (a T-junction). Edges are counted by the positions of their ends.
        Dictionary<string, int> count = new Dictionary<string, int>();
        Dictionary<string, Vector3[]> ends = new Dictionary<string, Vector3[]>();
        foreach (int t in front)
        {
            for (int k = 0; k < 3; k++)
            {
                Vector3 p = m.BaseVertices[m.Triangles[t + k]], q = m.BaseVertices[m.Triangles[t + (k + 1) % 3]];
                long a = PositionKey(p), b = PositionKey(q);
                string key = a < b ? a + ":" + b : b + ":" + a;
                int n;
                count.TryGetValue(key, out n);
                count[key] = n + 1;
                ends[key] = new[] { p, q };
            }
        }
        int loose = 0, interiorLoose = 0, overUsed = 0; double rimDev = 0, rimMean = 0; int rimEdges = 0;
        foreach (KeyValuePair<string, int> pair in count)
        {
            if (pair.Value > 2) overUsed++;
            if (pair.Value != 1) continue;
            loose++;
            Vector3 mid = (ends[pair.Key][0] + ends[pair.Key][1]) * 0.5f;
            float midR = Mathf.Sqrt((mid.x - 0.7f) * (mid.x - 0.7f) + (mid.y - 0.35f) * (mid.y - 0.35f));
            if (midR > 0.03f && midR < 0.05f)
                foreach (Vector3 e in ends[pair.Key])
                {
                    double dev = Math.Abs(Math.Sqrt((e.x - 0.7) * (e.x - 0.7) + (e.y - 0.35) * (e.y - 0.35)) - 0.04);
                    rimDev = Math.Max(rimDev, dev); rimMean += dev; rimEdges++;
                }
            float onOutline = DistanceToOutline(mid.x, mid.y);
            float rim = Mathf.Abs(Mathf.Sqrt((mid.x - 0.7f) * (mid.x - 0.7f) + (mid.y - 0.35f) * (mid.y - 0.35f)) - 0.04f);
            if (onOutline > 0.0005f && rim > Mathf.Max(cell * 1.5f, 0.012f)) interiorLoose++;
        }
        Console.WriteLine("  hole rim: the corners of the hole's edge are off the true circle by " + (rimMean / Math.Max(rimEdges, 1) * 1000).ToString("F2") + " mm on average, " + (rimDev * 1000).ToString("F2") + " mm at most (" + rimEdges / 2 + " edge pieces)");
        Check(interiorLoose == 0, "no cracks: every edge used once is on the outline or the hole's rim (" + loose + " such edges, " + interiorLoose + " in the middle of the plate)");
        Check(overUsed == 0, "no edge is used by more than two triangles (" + overUsed + ")");

        bool uv1 = true;
        for (int i = s.Vertices.Length; i < m.Vertices.Length; i++) uv1 &= m.Uv1[i].z == 7f;
        Check(uv1, "UV1 carried over to the new vertices");
    }


    // The atlases' mip levels: a handed-over rectangle is aligned, and its smaller levels are the 2 x 2 averages of the bigger ones.
    private static void MipTest()
    {
        Console.WriteLine("\n== MIP LEVELS ==");
        PlateCpu cpu = new PlateCpu { Width = 96, Height = 64, PxU = 200f, PxV = 200f };
        cpu.TileX[0] = 32; cpu.TileY[0] = 16; cpu.TileX[1] = 160; cpu.TileY[1] = 16;
        PlateStamps.Apply(cpu, new PlateImpact { Kind = DamageKind.Blocked, Front = true, U = 0.15f, V = 0.1f, Caliber = 0.05f, Strength = 0.6f, Thickness = 0.02f, Seed = 3 });
        List<Delivery> output = new List<Delivery>();
        PlateStamps.Flush(cpu, null, null, 0f, output, true, true);
        Check(output.Count >= 3, "normals, bump and damage view were handed over (" + output.Count + ")");
        bool aligned = true, mips = true, unit = true, average = true;
        foreach (Delivery d in output)
        {
            aligned &= d.X % 16 == 0 && d.Y % 16 == 0 && d.W % 16 == 0 && d.H % 16 == 0;
            if (d.Mips == null || d.Mips.Length != 4) { mips = false; continue; }
            int w = d.W, h = d.H;
            for (int level = 0; level < 4; level++)
            {
                w >>= 1; h >>= 1;
                mips &= d.Mips[level].Length == w * h * 4;
            }
            if (d.Kind == AtlasKind.Normal)
            {
                byte[] m = d.Mips[0];
                for (int i = 0; i < m.Length; i += 4)
                {
                    float x = m[i] / 255f * 2f - 1f, y = m[i + 1] / 255f * 2f - 1f, z = m[i + 2] / 255f * 2f - 1f;
                    float len = (float)Math.Sqrt(x * x + y * y + z * z);
                    unit &= Math.Abs(len - 1f) < 0.03f;
                }
            }
            else if (d.Kind == AtlasKind.Height)
            {
                int hw = d.W / 2;
                int a = (4 * d.W + 6) * 4 + 1, b = a + 4, c = a + d.W * 4, e = c + 4;
                int expected = (d.Bytes[a] + d.Bytes[b] + d.Bytes[c] + d.Bytes[e] + 2) >> 2;
                average &= Math.Abs(d.Mips[0][(2 * hw + 3) * 4 + 1] - expected) <= 1;
            }
        }
        Check(aligned, "the rectangles are multiples of 16 in position and size");
        Check(mips, "four smaller levels, each half the size");
        Check(unit, "the averaged normals are unit length again");
        Check(average, "a smaller level's pixel is the average of the 2 x 2 above it");

        // The depth limit: a dent cannot be deeper than 60% of the plate, nor a bulge more than 50% of it.
        PlateCpu deep = new PlateCpu { Width = 32, Height = 32, PxU = 200f, PxV = 200f };
        for (int i = 0; i < 5; i++)
            PlateStamps.Apply(deep, new PlateImpact { Kind = DamageKind.Blocked, Front = true, U = 0.08f, V = 0.08f, Caliber = 0.04f, Strength = 1f, Thickness = 0.01f, Seed = i });
        float lowest = 0f;
        foreach (float v in deep.Faces[0].Height) lowest = Math.Min(lowest, v);
        Check(lowest >= -0.0061f, "five shots on one spot dent it no deeper than 60% of the plate (" + (lowest * 1000f).ToString("F2") + " mm of 10)");
    }

    // The sides of a thick block as plates: every corner of the block falls inside the side's rectangle, the axes are perpendicular and right-handed.
    private static void SideTest()
    {
        Console.WriteLine("\n== SIDES OF A THICK BLOCK ==");
        PlateGeometry main = new PlateGeometry
        {
            T1 = new Vector3(1, 0, 0), T2 = new Vector3(0, 1, 0), N = new Vector3(0, 0, 1),
            SizeU = 1.0f, SizeV = 0.6f, Depth = 0.3f, Origin = new Vector3(-0.5f, -0.3f, 0f),
        };
        bool inside = true, frame = true;
        for (int axis = 1; axis <= 2; axis++)
        {
            PlateGeometry side = PlateGeometry.Side(main, axis);
            frame &= Math.Abs(Vector3.Dot(side.T1, side.T2)) < 1e-5f && Math.Abs(Vector3.Dot(side.T1, side.N)) < 1e-5f &&
                     Vector3.Dot(Vector3.Cross(side.T1, side.T2), side.N) > 0.99f && side.Axis == axis;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = new Vector3((i & 1) == 0 ? -0.5f : 0.5f, (i & 2) == 0 ? -0.3f : 0.3f, (i & 4) == 0 ? -0.15f : 0.15f);
                Vector2 plane = side.ToPlane(corner);
                inside &= plane.x > -1e-4f && plane.x < side.SizeU + 1e-4f && plane.y > -1e-4f && plane.y < side.SizeV + 1e-4f;
            }
            // the origin is on the block's middle plane across the side's normal
            Vector3 blockCenter = Vector3.zero;
            Vector3 along = blockCenter - side.Origin;
            frame &= Math.Abs(Vector3.Dot(along, side.N)) < 1e-4f;   // the origin is on the middle of the thickness across the side's normal
        }
        Check(inside, "all eight corners of the block fall inside each side's rectangle");
        Check(frame, "the side's axes are perpendicular, right-handed, and its origin sits on the middle of its thickness");
    }

    private static int Main()
    {
        Run("shared diagonal, 8 mm", false, 0.008f, 120000);
        Run("split seam (duplicated vertices on the diagonal), 8 mm", true, 0.008f, 120000);
        Run("small vertex budget", false, 0.008f, 800);
        Run("FINE 3 mm around the hole and the crack", false, 0.008f, 120000, 0.003f);
        Run("FINE 3 mm, split seam", true, 0.008f, 120000, 0.003f);
        Run("FINE 3 mm, small budget", false, 0.008f, 3000, 0.003f);
        MipTest();
        SideTest();
        Console.WriteLine(failures == 0 ? "\nALL CHECKS PASSED" : "\n" + failures + " CHECK(S) FAILED");
        return failures == 0 ? 0 : 1;
    }
}
