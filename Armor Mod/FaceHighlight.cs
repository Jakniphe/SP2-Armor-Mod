using System;
using System.Collections.Generic;
using System.Text;
using Assets.Scripts.Craft.Parts;
using Assets.Scripts.Design;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace ArmorFragments
{
    // Designer only: on the selected block with per-face armor, the face being edited is tinted orange and the
    // other armored faces blue (open faces are left as they are). The tint is the block's own mesh, triangle by
    // triangle, sorted into faces by ArmorFaces.Classify: the same faces the shells and fragments meet.
    // The tint is drawn each frame with Graphics.DrawMesh at the block's frame; it is not an object in the scene,
    // so nothing the game does to the block's renderers (paint, outlines, picking) reaches it or is changed by it.
    internal static class FaceHighlight
    {
        private const string OverlayName = "ArmorFaceHighlight";
        private const float RefreshInterval = 0.2f;
        private const float Lift = 0.002f;   // meters off the surface, so the tint is not hidden in it

        private static Mesh overlayMesh;
        private static Transform overlayFrame;
        private static int overlayLayer;
        private static string shownSignature;
        private static float nextRefresh;
        private static bool warned;
        private static Material pickedMaterial, armoredMaterial;
        private static bool materialsTried;

        public static void Tick()
        {
            try
            {
                if (Designer.Instance == null)
                {
                    Clear();
                    return;
                }
                if (Time.unscaledTime >= nextRefresh)
                {
                    nextRefresh = Time.unscaledTime + RefreshInterval;
                    Refresh();
                }
                Draw();
            }
            catch (Exception e)
            {
                if (!warned) ArmorFragmentsPlugin.Log.LogWarning("Armor face highlight failed: " + e);
                warned = true;
                Clear();
            }
        }

        private static void Refresh()
        {
            PartScript part = UI.DesignerSection.PerFacePart;
            if (part == null || !Materials())
            {
                Clear();
                return;
            }

            List<KeyValuePair<Mesh, Transform>> filters = Sources(part);
            string signature = Signature(part, filters);
            if (signature == shownSignature && overlayMesh != null) return;

            Clear();
            Build(part, filters);
            shownSignature = signature;
        }

        // Drawn at the frame's full matrix every frame, so the tint follows the block as it is moved or resized.
        private static void Draw()
        {
            if (overlayMesh == null) return;
            if (overlayFrame == null)
            {
                Clear();
                return;
            }
            Matrix4x4 matrix = overlayFrame.localToWorldMatrix;
            Material[] materials = { pickedMaterial, armoredMaterial };
            for (int sub = 0; sub < materials.Length; sub++)
            {
                if (overlayMesh.GetIndexCount(sub) == 0) continue;
                Graphics.DrawMesh(overlayMesh, matrix, materials[sub], overlayLayer, null, sub, null,
                    ShadowCastingMode.Off, false);
            }
        }

        // The block's visible meshes (not another part's). If none can be read, its colliders,
        // which in the designer are the visible shape too.
        internal static List<KeyValuePair<Mesh, Transform>> Sources(PartScript part)
        {
            List<KeyValuePair<Mesh, Transform>> sources = new List<KeyValuePair<Mesh, Transform>>();
            foreach (MeshFilter filter in part.GetComponentsInChildren<MeshFilter>())
            {
                if (filter == null || filter.sharedMesh == null) continue;
                if (filter.GetComponentInParent<PartScript>() != part) continue;
                if (filter.GetComponent<MeshRenderer>() == null || !filter.sharedMesh.isReadable) continue;
                sources.Add(new KeyValuePair<Mesh, Transform>(filter.sharedMesh, filter.transform));
            }
            if (sources.Count > 0) return sources;

            foreach (MeshCollider collider in part.GetComponentsInChildren<MeshCollider>())
            {
                if (collider == null || collider.sharedMesh == null || !collider.sharedMesh.isReadable) continue;
                if (collider.GetComponentInParent<PartScript>() != part) continue;
                sources.Add(new KeyValuePair<Mesh, Transform>(collider.sharedMesh, collider.transform));
            }
            return sources;
        }

        // Changes when the block's name (its faces), the face being edited, or its shape changes.
        private static string Signature(PartScript part, List<KeyValuePair<Mesh, Transform>> filters)
        {
            StringBuilder text = new StringBuilder();
            text.Append(part.GetInstanceID()).Append('|').Append(part.Part.Name).Append('|').Append(UI.DesignerSection.SelectedFace);
            foreach (KeyValuePair<Mesh, Transform> filter in filters)
            {
                Mesh mesh = filter.Key;
                text.Append('|').Append(mesh.GetInstanceID()).Append(':').Append(mesh.vertexCount).Append(':').Append(mesh.bounds.ToString("F4"));
            }
            return text.ToString();
        }

        private static void Build(PartScript part, List<KeyValuePair<Mesh, Transform>> filters)
        {
            ArmorTag armor = TagCodec.Parse(part.Part.Name).Armor;
            if (armor == null || !armor.PerFace) return;

            ArmorFaces.Shape shape = ArmorFaces.ShapeOf(part);
            Transform frame = shape.Frame;
            int picked = UI.DesignerSection.SelectedFace;   // AllFaces: every armored face is the one edited
            bool all = picked == UI.DesignerSection.AllFaces;

            List<Vector3> vertices = new List<Vector3>();
            List<int> pickedTriangles = new List<int>();
            List<int> armoredTriangles = new List<int>();

            foreach (KeyValuePair<Mesh, Transform> filter in filters)
            {
                Mesh mesh = filter.Key;
                Vector3[] points = mesh.vertices;
                Transform from = filter.Value;

                for (int sub = 0; sub < mesh.subMeshCount; sub++)
                {
                    if (mesh.GetTopology(sub) != MeshTopology.Triangles) continue;
                    int[] triangles = mesh.GetTriangles(sub);
                    for (int t = 0; t + 2 < triangles.Length; t += 3)
                    {
                        Vector3 a = from.TransformPoint(points[triangles[t]]);
                        Vector3 b = from.TransformPoint(points[triangles[t + 1]]);
                        Vector3 c = from.TransformPoint(points[triangles[t + 2]]);
                        Vector3 normal = Vector3.Cross(b - a, c - a);
                        if (normal.sqrMagnitude < 1e-14f) continue;
                        normal.Normalize();

                        int face = ArmorFaces.Classify(shape, (a + b + c) / 3f, normal);
                        if (face < 0) continue;   // an inner wall of a hollow block
                        List<int> target;
                        if (all) target = armor.Faces[face] > 0f ? pickedTriangles : null;
                        else if (face == picked) target = pickedTriangles;
                        else target = armor.Faces[face] > 0f ? armoredTriangles : null;
                        if (target == null) continue;

                        Vector3 lift = normal * Lift;
                        target.Add(vertices.Count);
                        vertices.Add(frame.InverseTransformPoint(a + lift));
                        target.Add(vertices.Count);
                        vertices.Add(frame.InverseTransformPoint(b + lift));
                        target.Add(vertices.Count);
                        vertices.Add(frame.InverseTransformPoint(c + lift));
                    }
                }
            }
            if (vertices.Count == 0) return;

            overlayMesh = new Mesh { name = OverlayName };
            if (vertices.Count > 65000) overlayMesh.indexFormat = IndexFormat.UInt32;
            overlayMesh.SetVertices(vertices);
            overlayMesh.subMeshCount = 2;
            overlayMesh.SetTriangles(pickedTriangles, 0);
            overlayMesh.SetTriangles(armoredTriangles, 1);
            overlayMesh.RecalculateBounds();
            overlayFrame = frame;
            overlayLayer = part.gameObject.layer;
        }

        private static void Clear()
        {
            if (overlayMesh != null) Object.Destroy(overlayMesh);
            overlayMesh = null;
            overlayFrame = null;
            shownSignature = null;
        }

        private static bool Materials()
        {
            if (materialsTried) return pickedMaterial != null;
            materialsTried = true;

            Shader shader = RayDrawer.GetShader();
            if (shader == null) return false;
            pickedMaterial = MakeMaterial(shader, new Color(1f, 0.55f, 0.1f, 0.55f));
            armoredMaterial = MakeMaterial(shader, new Color(0.3f, 0.6f, 1f, 0.35f));
            return true;
        }

        private static Material MakeMaterial(Shader shader, Color color)
        {
            Material material = new Material(shader);
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color")) material.SetColor("_Color", color);
            material.renderQueue = 3000;
            return material;
        }
    }
}
