using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using Assets.Scripts.Craft.MeshGen;
using Assets.Scripts.Craft.Parts;
using Assets.Scripts.Craft.Parts.Modifiers;
using Assets.Scripts.Craft.Parts.Modifiers.CarverParts;
using Assets.Scripts.Design;
using HarmonyLib;
using UnityEngine;

namespace ArmorFragments
{
    // The outer surface of a per-face block, as the player sees it: its own mesh (FaceHighlight.Sources) without the
    // walls of holes carved into it by the game's carving parts. Used by the face tint, the armor weight, and the test
    // of whether a line meets real surface where it crosses the block's colliders.
    //
    // A carved hole is only in the block's visible mesh: in flight its colliders are convex and cover the hole. So on
    // a carved block, where a line crosses the colliders with no visible surface there (the mouth of a hole), the
    // face is open there. Hole walls are found by the game's paint slot in the designer (carved surfaces are painted
    // with slot 5 and up, slice cuts with 4) and, in flight, where the mesh is one piece, as surfaces that face into
    // the block's convex colliders.
    //
    // A cutter tagged [HOL t=] makes the walls of the holes it carves plates of that thickness: each wall is given
    // to the armored cutter whose box it lies in. Lines meet those walls (WallBoundaries), and they weigh.
    internal static class ArmorSurface
    {
        private const int CarvedLevel = 5;            // first paint slot of carved surfaces
        private const float InsideProbe = 0.02f;      // m out along a surface's normal: still inside = a hole wall
        private const float SurfaceTolerance = 0.05f; // m along the line between a collider and the visible surface
        private const float CutterMargin = 0.01f;     // m around a cutter's box that still counts as its wall

        private static readonly FieldInfo SubmeshLevelsField =
            AccessTools.Field(typeof(ProceduralPartMeshRenderer), "_submeshToLevel");
        private static readonly PropertyInfo CutterBoundsProperty =
            AccessTools.Property(typeof(MeshModifierBaseScript), "ManifoldLocalBounds");

        private sealed class Entry
        {
            public string Key;
            public Vector3[] Local;        // outer triangles, three points each, in the part's local space
            public Vector3[] WallLocal;    // armored hole walls, three points each, in the part's local space
            public float[] WallMm;         // each armored wall triangle's thickness
            public float WallKg;
            public Vector3 WallCenter;     // where the walls' weight sits, part's local space
        }

        private static readonly Dictionary<PartScript, Entry> Cache = new Dictionary<PartScript, Entry>();
        private static readonly List<Vector3> Scratch = new List<Vector3>();
        private static readonly List<Vector3> WallScratch = new List<Vector3>();
        private static readonly List<float> WallMmScratch = new List<float>();
        private static readonly Vector3[] NoPoints = new Vector3[0];
        private static readonly float[] NoMm = new float[0];

        // Whether carving parts cut into this block.
        public static bool IsCarved(PartScript part)
        {
            JFuselageScript fuselage = FaceHighlight.FuselageOf(part);
            return fuselage != null && fuselage.MeshModifiers != null && fuselage.MeshModifiers.Count > 0;
        }

        // The block's outer surface triangles in its local space (three points each); empty if it can't be read.
        public static Vector3[] OuterLocal(PartScript part)
        {
            Entry entry = Get(part);
            return entry != null ? entry.Local : NoPoints;
        }

        // The armored hole walls (triangles in the part's local space, three points each) and each one's thickness.
        public static Vector3[] WallsLocal(PartScript part, out float[] mm)
        {
            Entry entry = Get(part);
            mm = entry != null ? entry.WallMm : NoMm;
            return entry != null ? entry.WallLocal : NoPoints;
        }

        // Steel of the armored hole walls: kg, and where it sits in the part's local space.
        public static float WallKg(PartScript part, out Vector3 localCenter)
        {
            Entry entry = Get(part);
            localCenter = entry != null ? entry.WallCenter : Vector3.zero;
            return entry != null ? entry.WallKg : 0f;
        }

        // Changes when a cutter of the block is moved or its hole wall armor changes (for caches of other classes).
        public static string CutterKey(PartScript part)
        {
            JFuselageScript fuselage = FaceHighlight.FuselageOf(part);
            if (fuselage == null || fuselage.MeshModifiers == null || fuselage.MeshModifiers.Count == 0) return "";
            StringBuilder text = new StringBuilder();
            foreach (MeshModifierBaseScript cutter in fuselage.MeshModifiers)
            {
                if (cutter == null || cutter.PartScript == null) continue;
                Transform t = cutter.transform;
                text.Append(cutter.PartScript.Part.Name).Append('@').Append(t.position.ToString("F3"))
                    .Append(t.rotation.ToString("F3")).Append(t.lossyScale.ToString("F3")).Append(';');
            }
            return text.ToString();
        }

        // Does the line along dir meet the block's visible surface at point (a point where it crosses the block's
        // colliders)? Always true on a block nothing is carved into.
        public static bool SurfaceAt(PartScript part, Vector3 point, Vector3 dir)
        {
            if (part == null || !IsCarved(part)) return true;
            Vector3[] local = OuterLocal(part);
            if (local.Length == 0) return true;   // nothing to compare with: trust the colliders

            Transform frame = part.transform;
            Vector3 origin = frame.InverseTransformPoint(point - dir * SurfaceTolerance);
            Vector3 end = frame.InverseTransformPoint(point + dir * SurfaceTolerance);
            Vector3 segment = end - origin;
            float hit;
            for (int t = 0; t + 2 < local.Length; t += 3)
                if (SegmentHits(origin, segment, local[t], local[t + 1], local[t + 2], out hit)) return true;
            return false;
        }

        // Where the line from origin along dir crosses the block's armored hole walls within maxDistance.
        public static void WallBoundaries(PartScript part, Vector3 origin, Vector3 dir, float maxDistance,
                                          List<ArmorFaces.Boundary> into)
        {
            if (part == null || !IsCarved(part)) return;
            float[] mm;
            Vector3[] walls = WallsLocal(part, out mm);
            if (walls.Length == 0) return;

            Transform frame = part.transform;
            Vector3 start = frame.InverseTransformPoint(origin);
            Vector3 segment = frame.InverseTransformPoint(origin + dir * maxDistance) - start;
            for (int t = 0; t + 2 < walls.Length; t += 3)
            {
                float along;
                if (!SegmentHits(start, segment, walls[t], walls[t + 1], walls[t + 2], out along)) continue;
                Vector3 normal = frame.TransformDirection(Vector3.Cross(walls[t + 1] - walls[t], walls[t + 2] - walls[t])).normalized;
                float distance = along * maxDistance;
                into.Add(new ArmorFaces.Boundary
                {
                    Distance = distance,
                    Point = origin + dir * distance,
                    Normal = normal,                        // out of the wall into the hole
                    Leaving = Vector3.Dot(dir, normal) > 0f,   // from the block's inside into the hole
                    Face = ArmorFaces.HoleWall,
                    Mm = mm[t / 3],
                });
            }
        }

        private static Entry Get(PartScript part)
        {
            if (part == null) return null;

            // In flight a block and its cutters stay as built: once measured, it is kept (this runs for every ray).
            Entry known;
            if (Designer.Instance == null && Cache.TryGetValue(part, out known) && known.Local.Length > 0) return known;

            List<KeyValuePair<Mesh, Transform>> sources = FaceHighlight.Sources(part);
            bool carved = IsCarved(part);
            string key = Key(sources, carved) + (carved ? CutterKey(part) : "");

            Entry entry;
            if (Cache.TryGetValue(part, out entry) && entry.Key == key) return entry;

            entry = new Entry { Key = key };
            Build(part, sources, carved, entry);
            Cache[part] = entry;
            return entry;
        }

        private static string Key(List<KeyValuePair<Mesh, Transform>> sources, bool carved)
        {
            StringBuilder text = new StringBuilder(carved ? "c" : "-");
            foreach (KeyValuePair<Mesh, Transform> source in sources)
            {
                Mesh mesh = source.Key;
                text.Append('|').Append(mesh.GetInstanceID()).Append(':').Append(mesh.vertexCount).Append(':')
                    .Append(mesh.bounds.ToString("F4"));
            }
            return text.ToString() + '|';
        }

        private static void Build(PartScript part, List<KeyValuePair<Mesh, Transform>> sources, bool carved, Entry entry)
        {
            Scratch.Clear();
            WallScratch.Clear();
            WallMmScratch.Clear();
            Transform frame = part.transform;
            int[] levels = carved ? HullLevels(part) : null;
            List<Collider> convex = carved && levels == null ? ConvexColliders(part) : null;
            List<KeyValuePair<MeshModifierBaseScript, float>> cutters = carved ? ArmoredCutters(part) : null;

            float wallKg = 0f;
            Vector3 wallWeighted = Vector3.zero;

            foreach (KeyValuePair<Mesh, Transform> source in sources)
            {
                Mesh mesh = source.Key;
                Transform from = source.Value;
                Vector3[] points = mesh.vertices;
                bool sameMeshAsLevels = levels != null && levels.Length == mesh.subMeshCount;
                for (int sub = 0; sub < mesh.subMeshCount; sub++)
                {
                    if (mesh.GetTopology(sub) != MeshTopology.Triangles) continue;
                    bool carvedSubmesh = sameMeshAsLevels && levels[sub] >= CarvedLevel;
                    int[] triangles = mesh.GetTriangles(sub);
                    for (int t = 0; t + 2 < triangles.Length; t += 3)
                    {
                        Vector3 a = from.TransformPoint(points[triangles[t]]);
                        Vector3 b = from.TransformPoint(points[triangles[t + 1]]);
                        Vector3 c = from.TransformPoint(points[triangles[t + 2]]);
                        Vector3 cross = Vector3.Cross(b - a, c - a);
                        float twice = cross.magnitude;
                        if (twice < 1e-10f) continue;
                        Vector3 middle = (a + b + c) / 3f;

                        bool wall = carvedSubmesh ||
                                    (convex != null && convex.Count > 0 && Inside(convex, middle + cross / twice * InsideProbe));
                        if (!wall)
                        {
                            Scratch.Add(frame.InverseTransformPoint(a));
                            Scratch.Add(frame.InverseTransformPoint(b));
                            Scratch.Add(frame.InverseTransformPoint(c));
                            continue;
                        }

                        // A hole wall: a plate if it lies in the box of a cutter with hole wall armor.
                        float mm = WallMm(cutters, middle);
                        if (mm <= 0f) continue;
                        WallScratch.Add(frame.InverseTransformPoint(a));
                        WallScratch.Add(frame.InverseTransformPoint(b));
                        WallScratch.Add(frame.InverseTransformPoint(c));
                        WallMmScratch.Add(mm);
                        float kg = 0.5f * twice * mm * 0.001f * ArmorMass.SteelKgPerM3;
                        wallKg += kg;
                        wallWeighted += kg * frame.InverseTransformPoint(middle);
                    }
                }
            }

            entry.Local = Scratch.ToArray();
            entry.WallLocal = WallScratch.ToArray();
            entry.WallMm = WallMmScratch.ToArray();
            entry.WallKg = wallKg;
            entry.WallCenter = wallKg > 0f ? wallWeighted / wallKg : Vector3.zero;
        }

        // The cutters of the block that armor their holes' walls, with their thickness.
        private static List<KeyValuePair<MeshModifierBaseScript, float>> ArmoredCutters(PartScript part)
        {
            List<KeyValuePair<MeshModifierBaseScript, float>> cutters = new List<KeyValuePair<MeshModifierBaseScript, float>>();
            JFuselageScript fuselage = FaceHighlight.FuselageOf(part);
            if (fuselage == null || fuselage.MeshModifiers == null) return cutters;
            foreach (MeshModifierBaseScript cutter in fuselage.MeshModifiers)
            {
                if (cutter == null || cutter.PartScript == null || cutter.PartScript.Part == null) continue;
                HoleTag hole = TagCodec.Parse(cutter.PartScript.Part.Name).Hole;
                if (hole != null && hole.Thickness > 0f)
                    cutters.Add(new KeyValuePair<MeshModifierBaseScript, float>(cutter, hole.Thickness));
            }
            return cutters;
        }

        // The thickest armored cutter whose box (a little bigger) holds the point; 0 if none.
        private static float WallMm(List<KeyValuePair<MeshModifierBaseScript, float>> cutters, Vector3 point)
        {
            if (cutters == null || cutters.Count == 0 || CutterBoundsProperty == null) return 0f;
            float mm = 0f;
            foreach (KeyValuePair<MeshModifierBaseScript, float> cutter in cutters)
            {
                Bounds box = (Bounds)CutterBoundsProperty.GetValue(cutter.Key, null);
                box.Expand(CutterMargin * 2f);
                if (box.Contains(cutter.Key.transform.InverseTransformPoint(point))) mm = Mathf.Max(mm, cutter.Value);
            }
            return mm;
        }

        // Paint slot of each submesh of the block's hull mesh (the designer draws one submesh per slot); null when the
        // hull is one piece (in flight) or can't be read.
        private static int[] HullLevels(PartScript part)
        {
            if (Designer.Instance == null || SubmeshLevelsField == null) return null;
            ProceduralPartMeshRenderer renderer = FaceHighlight.HullRenderer(part);
            if (renderer == null) return null;
            int[] levels = SubmeshLevelsField.GetValue(renderer) as int[];
            return levels != null && levels.Length > 1 ? levels : null;
        }

        private static List<Collider> ConvexColliders(PartScript part)
        {
            List<Collider> convex = new List<Collider>();
            foreach (Collider collider in part.GetComponentsInChildren<Collider>())
            {
                if (collider == null || !collider.enabled || collider.isTrigger) continue;
                if (collider.GetComponentInParent<PartScript>() != part) continue;
                MeshCollider mesh = collider as MeshCollider;
                if (mesh != null && !mesh.convex) continue;   // ClosestPoint only works on convex shapes
                convex.Add(collider);
            }
            return convex;
        }

        private static bool Inside(List<Collider> colliders, Vector3 point)
        {
            foreach (Collider collider in colliders)
                if ((collider.ClosestPoint(point) - point).sqrMagnitude < 1e-8f) return true;
            return false;
        }

        // Moller-Trumbore, both sides of the triangle, within the segment; along = 0..1 of the segment.
        private static bool SegmentHits(Vector3 origin, Vector3 segment, Vector3 a, Vector3 b, Vector3 c, out float along)
        {
            along = 0f;
            Vector3 e1 = b - a;
            Vector3 e2 = c - a;
            Vector3 p = Vector3.Cross(segment, e2);
            float det = Vector3.Dot(e1, p);
            if (Mathf.Abs(det) < 1e-12f) return false;
            float inverse = 1f / det;
            Vector3 s = origin - a;
            float u = Vector3.Dot(s, p) * inverse;
            if (u < 0f || u > 1f) return false;
            Vector3 q = Vector3.Cross(s, e1);
            float v = Vector3.Dot(segment, q) * inverse;
            if (v < 0f || u + v > 1f) return false;
            along = Vector3.Dot(e2, q) * inverse;
            return along >= 0f && along <= 1f;
        }

        // Parts that are gone are dropped now and then (ArmorMass.Tick calls this).
        public static void Prune()
        {
            if (Cache.Count == 0) return;
            List<PartScript> gone = null;
            foreach (PartScript part in Cache.Keys)
                if (part == null)
                {
                    if (gone == null) gone = new List<PartScript>();
                    gone.Add(part);
                }
            if (gone != null)
                foreach (PartScript part in gone) Cache.Remove(part);
        }
    }
}
