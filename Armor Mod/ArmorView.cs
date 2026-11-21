using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Assets.Scripts.Craft.Parts;
using Assets.Scripts.Design;
using Assets.Scripts.Design.UI;
using HarmonyLib;
using Jundroo.Juicy.Widgets;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace ArmorFragments
{
    // Designer: the armor view. A shield button at the bottom right (beside the XML overload button) turns it on and
    // off; it starts off each time the designer opens. While it is on, every armored face of the craft is colored by
    // its thickness: the armored faces of blocks armored per face (open faces are not), the armored walls of holes
    // carved into them, and the whole surface of parts armored as a whole. The thicknesses on the craft are ranked,
    // thinnest the lightest green to thickest the darkest red, and listed with their colors at the top left. Parts
    // without armor get no color. Drawn after the game's see-through parts, so it stays on in its X-ray view. While it
    // is on it replaces the edit tint (FaceHighlight) of the part being edited.
    internal static class ArmorView
    {
        public const string ButtonId = "button-armor-view";
        private const string IconId = "button-armor-view-icon";
        private const string LayoutPath = "Xml/Design/Designer";
        private const string XrayButtonId = "button-ghost-view";
        private const string OverloadButtonId = "btn-overload-xml";
        private const float RefreshInterval = 0.5f;

        private sealed class Entry
        {
            public Mesh Mesh;               // null: the part has no armored face
            public Transform Frame;
            public Transform LayerSource;   // the object whose layer the part is drawn on
            public string Signature;
            public float[] Mm;              // the armor's thickness under each vertex of Mesh
            public Matrix4x4 LastMatrix;    // where it was when the X-ray mesh was last made
            public bool LastShown;
        }

        // The thicknesses on the craft, thinnest first (whole millimeters), and their colors: ranked from the lightest
        // green (thinnest) through yellow and orange to the darkest red (thickest), whatever the values are.
        private static readonly List<float> Legend = new List<float>();
        private static readonly List<Color> LegendColors = new List<Color>();
        private const float Alpha = 1f;   // solid: the armor view shows the armor, not the paint under it
        private const float XrayAlpha = 0.5f;   // in the game's X-ray view
        private static readonly Color[] Ramp =
        {
            new Color(0.62f, 0.96f, 0.45f),   // light green
            new Color(0.80f, 0.90f, 0.12f),   // yellow green
            new Color(0.98f, 0.78f, 0.08f),   // amber
            new Color(0.96f, 0.48f, 0.05f),   // orange
            new Color(0.85f, 0.12f, 0.05f),   // red
            new Color(0.45f, 0.00f, 0.02f),   // dark red
        };

        public static bool On { get; private set; }
        private static ButtonWidget button;
        private static Sprite shield;
        private static Material material;
        private static readonly Dictionary<PartScript, Entry> Parts = new Dictionary<PartScript, Entry>();
        private static float nextRefresh;
        private static bool warned;

        // ---- The button ----------------------------------------------------------

        // The game read its designer layout: the shield button goes in the row at the bottom right, just left of the
        // XML overload button and styled like it (or, without that button, after the X-ray button in the view panel).
        internal static void AddToLayout(string path, XElement layout)
        {
            if (path != LayoutPath || layout == null) return;
            if (layout.Descendants().Any(e => e.Name.LocalName == "Button" && (string)e.Attribute("id") == ButtonId)) return;
            const string tooltip = "Toggle Armor View\nColor every armored face of the craft by its thickness: thinnest light green, thickest dark red.";

            XElement overload = FindButton(layout, OverloadButtonId);
            if (overload != null)
            {
                XNamespace ns = overload.Name.Namespace;
                overload.AddBeforeSelf(new XElement(ns + "Button",
                    new XAttribute("class", "btn btn-default btn-icon right-button"),
                    new XAttribute("id", ButtonId),
                    new XAttribute("tooltipPosition", "Above"),
                    new XAttribute("tooltip", tooltip),
                    new XElement(ns + "Image", new XAttribute("id", IconId), new XAttribute("margin", "10"))));
                return;
            }

            XElement xray = FindButton(layout, XrayButtonId);
            if (xray == null)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Armor view: neither the designer's XML overload nor X-ray button was found; no armor view button");
                return;
            }
            XNamespace xns = xray.Name.Namespace;
            xray.AddAfterSelf(new XElement(xns + "Button",
                new XAttribute("class", "btn btn-default btn-icon right-button"),
                new XAttribute("id", ButtonId),
                new XAttribute("tooltip", tooltip),
                new XElement(xns + "Image", new XAttribute("id", IconId))));
        }

        private static XElement FindButton(XElement layout, string id)
        {
            return layout.Descendants().FirstOrDefault(e => e.Name.LocalName == "Button" && (string)e.Attribute("id") == id);
        }

        // The designer's UI was built: the button gets its shield and its click, and the view starts off.
        internal static void Attach(Widget root)
        {
            Clear();
            On = false;
            button = root != null ? root.FindWidget<ButtonWidget>(ButtonId) : null;
            if (button == null) return;
            ImageWidget icon = root.FindWidget<ImageWidget>(IconId);
            if (icon != null && icon.Image != null)
            {
                icon.Image.sprite = Shield();
                icon.Image.preserveAspect = true;
            }
            button.Clicked += (Widget w) => Toggle();
            UpdateButton();
        }

        private static void Toggle()
        {
            On = !On;
            if (!On) Clear();
            nextRefresh = 0f;
            UpdateButton();
        }

        // Off: the game's plain (grey) button. On: its blue, as its own view buttons show a view in use.
        private static void UpdateButton()
        {
            if (button == null) return;
            try
            {
                button.EnableClass("btn-primary", On);
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Armor view: couldn't restyle the button: " + e.Message);
            }
        }

        // A plain white shield (the game tints its icons): an outline, its left half filled.
        private static Sprite Shield()
        {
            if (shield != null) return shield;
            const int size = 128;
            const int samples = 4;   // per side, for smooth edges
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "ArmorViewShield",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            Color[] pixels = new Color[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int covered = 0;
                    for (int sy = 0; sy < samples; sy++)
                    {
                        for (int sx = 0; sx < samples; sx++)
                        {
                            float u = ((x + (sx + 0.5f) / samples) / size) * 2f - 1f;    // -1 left .. 1 right
                            float v = 1f - (y + (sy + 0.5f) / samples) / size;            // 0 top .. 1 bottom
                            bool outer = InShield(u, v, 0.78f, 0.08f, 0.95f);
                            bool inner = InShield(u, v, 0.62f, 0.19f, 0.80f);
                            bool fill = InShield(u, v, 0.50f, 0.27f, 0.69f) && u < -0.04f;
                            if ((outer && !inner) || fill) covered++;
                        }
                    }
                    pixels[y * size + x] = new Color(1f, 1f, 1f, covered / (float)(samples * samples));
                }
            }
            texture.SetPixels(pixels);
            texture.Apply(false, true);
            shield = Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
            shield.name = "ArmorViewShield";
            return shield;
        }

        // A shield: straight sides from the top down to just under halfway, then curving in to a point.
        private static bool InShield(float u, float v, float halfWidth, float top, float tip)
        {
            if (v < top || v > tip) return false;
            float shoulder = top + (tip - top) * 0.45f;
            float width = v <= shoulder
                ? halfWidth
                : halfWidth * Mathf.Cos((v - shoulder) / (tip - shoulder) * Mathf.PI * 0.5f);
            return Mathf.Abs(u) <= width;
        }

        // ---- The tint ------------------------------------------------------------

        public static void Tick()
        {
            try
            {
                Designer designer = Designer.Instance;
                if (!On || designer == null || Assets.Scripts.Flight.FlightSceneScript.Instance != null)
                {
                    if (Parts.Count > 0) Clear();
                    return;
                }
                if (Time.unscaledTime >= nextRefresh)
                {
                    nextRefresh = Time.unscaledTime + RefreshInterval;
                    Refresh(designer);
                }
                Draw();
            }
            catch (Exception e)
            {
                if (!warned) ArmorFragmentsPlugin.Log.LogWarning("Armor view failed: " + e);
                warned = true;
                Clear();
            }
        }

        // Each armored part's tint is made again only when its armor or shape changed; parts gone are dropped.
        private static void Refresh(Designer designer)
        {
            HashSet<PartScript> seen = new HashSet<PartScript>();
            bool changed = false;
            bool xray = FaceHighlight.XRay;
            Blockers.Clear();
            IEnumerable<PartData> parts = designer.Aircraft != null && designer.Aircraft.Aircraft != null && designer.Aircraft.Aircraft.Assembly != null
                ? designer.Aircraft.Aircraft.Assembly.Parts
                : null;
            if (parts != null)
            {
                foreach (PartData data in parts)
                {
                    PartScript part = data != null ? data.PartScript : null;
                    if (part == null) continue;
                    if (data.Name == null || !data.Name.Contains("[ARM"))
                    {
                        if (xray) AddBlocker(part);
                        continue;
                    }
                    seen.Add(part);

                    List<KeyValuePair<Mesh, Transform>> sources = FaceHighlight.Sources(part);
                    string signature = FaceHighlight.Signature(part, sources, UI.DesignerSection.AllFaces);
                    Entry entry;
                    if (Parts.TryGetValue(part, out entry) && entry.Signature == signature &&
                        (entry.Mesh == null || entry.Frame != null)) continue;

                    if (entry != null && entry.Mesh != null) Object.Destroy(entry.Mesh);
                    entry = new Entry { Signature = signature };
                    Transform frame;
                    List<float> mm = new List<float>();
                    entry.Mesh = FaceHighlight.BuildMesh(part, sources, UI.DesignerSection.AllFaces, out frame, mm);
                    entry.Mm = mm.ToArray();
                    entry.Frame = frame;
                    entry.LayerSource = sources.Count > 0 && sources[0].Value != null ? sources[0].Value : part.transform;
                    Parts[part] = entry;
                    changed = true;
                }
            }

            List<PartScript> gone = null;
            foreach (PartScript part in Parts.Keys)
                if (part == null || !seen.Contains(part)) (gone ?? (gone = new List<PartScript>())).Add(part);
            if (gone != null)
            {
                foreach (PartScript part in gone)
                {
                    Entry entry = Parts[part];
                    if (entry.Mesh != null) Object.Destroy(entry.Mesh);
                    Parts.Remove(part);
                }
                changed = true;
            }
            if (changed) Recolor();
        }

        // In the game's X-ray view the parts are drawn see-through and hide nothing: the unarmored ones are drawn
        // again into the depth only (no color), just before the tints, so armor behind a part inside the craft is
        // hidden by it instead of covering it. Armored parts are left out, so their own faces stay seen through.
        private struct Blocker
        {
            public PartScript Part;
            public Mesh Mesh;
            public Transform Transform;
        }

        private static readonly List<Blocker> Blockers = new List<Blocker>();
        private static Material depthMaterial;
        private const float BlockerPushBack = 0.005f;   // share of the distance from the camera
        private static Material nearestMaterial;          // the tints' own depth, drawn before their colors

        private static void AddBlocker(PartScript part)
        {
            foreach (MeshRenderer renderer in XRayView.GhostSources(part))
            {
                if (renderer == null) continue;
                MeshFilter filter = renderer.GetComponent<MeshFilter>();
                Mesh mesh = filter != null ? filter.sharedMesh : null;
                if (mesh == null) continue;
                Blockers.Add(new Blocker { Part = part, Mesh = mesh, Transform = renderer.transform });
            }
        }

        private static void DrawBlockers()
        {
            if (Blockers.Count == 0) return;
            if (depthMaterial == null)
            {
                depthMaterial = FaceHighlight.MakeDepthMaterial();
                if (depthMaterial == null) return;
            }
            // Drawn a little farther from the designer's camera than they are (BlockerPushBack of the distance), so
            // armor lying against a part (a rack on a wall, a plate on the hull) is never hidden by it, whatever the
            // angle; armor really behind a part still is.
            Matrix4x4 push = Matrix4x4.identity;
            Designer designer = Designer.Instance;
            Camera camera = designer != null && designer.CameraController != null ? designer.CameraController.Camera : null;
            if (camera != null)
            {
                Vector3 eye = camera.transform.position;
                push = Matrix4x4.Translate(eye) * Matrix4x4.Scale(Vector3.one * (1f + BlockerPushBack)) * Matrix4x4.Translate(-eye);
            }
            foreach (Blocker blocker in Blockers)
            {
                if (blocker.Part == null || blocker.Mesh == null || blocker.Transform == null) continue;
                if (!blocker.Part.gameObject.activeInHierarchy) continue;
                if (blocker.Part.PartMaterialScript != null && !blocker.Part.PartMaterialScript.Visible) continue;
                Matrix4x4 matrix = push * blocker.Transform.localToWorldMatrix;
                int layer = blocker.Transform.gameObject.layer;
                for (int sub = 0; sub < blocker.Mesh.subMeshCount; sub++)
                    Graphics.DrawMesh(blocker.Mesh, matrix, depthMaterial, layer, null, sub, null, ShadowCastingMode.Off, false);
            }
        }

        // The legend from every thickness on the craft, then each part's colors from it.
        private static void Recolor()
        {
            xrayDirty = true;   // parts or colors changed: the X-ray mesh is made again
            SortedSet<float> values = new SortedSet<float>();
            foreach (Entry entry in Parts.Values)
            {
                if (entry.Mesh == null || entry.Mm == null) continue;
                foreach (float mm in entry.Mm)
                    if (mm > 0f) values.Add(Mathf.Round(mm));
            }
            Legend.Clear();
            LegendColors.Clear();
            Legend.AddRange(values);
            for (int i = 0; i < Legend.Count; i++)
                LegendColors.Add(RampAt(Legend.Count == 1 ? 0.5f : i / (float)(Legend.Count - 1)));

            foreach (Entry entry in Parts.Values)
            {
                if (entry.Mesh == null || entry.Mm == null || entry.Mm.Length != entry.Mesh.vertexCount) continue;
                Color[] colors = new Color[entry.Mm.Length];
                for (int v = 0; v < colors.Length; v++) colors[v] = ColorOf(entry.Mm[v]);
                entry.Mesh.colors = colors;
            }
        }

        private static Color ColorOf(float mm)
        {
            int index = Legend.BinarySearch(Mathf.Round(mm));
            Color color = index >= 0 ? LegendColors[index] : RampAt(0f);
            // Vertex colors reach the shader as they are, while the legend's (GUI) colors are converted: in the game's
            // linear color space the face gets the linear value, so both show the same color on screen.
            if (QualitySettings.activeColorSpace == ColorSpace.Linear) color = color.linear;
            color.a = Alpha;
            return color;
        }

        // 0: thinnest (light green) .. 1: thickest (dark red).
        private static Color RampAt(float at)
        {
            float scaled = Mathf.Clamp01(at) * (Ramp.Length - 1);
            int low = Mathf.Min(Mathf.FloorToInt(scaled), Ramp.Length - 2);
            return Color.Lerp(Ramp[low], Ramp[low + 1], scaled - low);
        }

        // The legend at the top left: one row per thickness on the craft, thinnest at the top. With very many
        // thicknesses, an even pick of them (always the thinnest and the thickest).
        private const int MaxRows = 16;
        private static GUIStyle legendText;

        // The designer's UI (its menus, the flyouts that open from the left).
        internal static DesignerUIScript DesignerUi;

        // The right edge (screen pixels) of the designer menu open on the left side of the screen, sliding in or out
        // included; 0 with none.
        private static float OpenMenuRight()
        {
            DesignerUIScript ui = DesignerUi;
            if (ui == null || ui.Flyouts == null) return 0f;
            Assets.Scripts.UI.IFlyout flyout = ui.Flyouts.Selected;
            if (flyout == null || (!flyout.IsOpen && !flyout.IsClosing) || flyout.Widget == null) return 0f;
            RectTransform rect = flyout.Widget.transform as RectTransform;
            if (rect == null || !rect.gameObject.activeInHierarchy) return 0f;

            Vector3[] corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            Canvas canvas = rect.GetComponentInParent<Canvas>();
            Camera camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
            float left = float.MaxValue, right = float.MinValue;
            foreach (Vector3 corner in corners)
            {
                float screenX = RectTransformUtility.WorldToScreenPoint(camera, corner).x;
                left = Mathf.Min(left, screenX);
                right = Mathf.Max(right, screenX);
            }
            return left < Screen.width * 0.5f ? right : 0f;   // a menu on the right side leaves the legend be
        }

        internal static void OnGui()
        {
            if (!On || Legend.Count == 0 || Designer.Instance == null) return;
            if (Assets.Scripts.Flight.FlightSceneScript.Instance != null) return;
            if (Event.current.type != EventType.Repaint) return;

            float scale = Mathf.Max(1f, Screen.height / 1080f);
            float row = 22f * scale, swatch = 22f * scale, width = 78f * scale;
            float x = 12f * scale, y = 90f * scale;
            float menuRight = OpenMenuRight();   // a menu open on the left: the legend moves out beside it
            if (menuRight > 0f) x = Mathf.Max(x, menuRight + 12f * scale);
            if (legendText == null)
                legendText = new GUIStyle(GUI.skin.label);
            legendText.fontSize = Mathf.RoundToInt(14f * scale);
            legendText.normal.textColor = Color.white;

            int rows = Mathf.Min(Legend.Count, MaxRows);
            GUI.color = new Color(0.12f, 0.14f, 0.17f, 0.85f);
            GUI.DrawTexture(new Rect(x, y, width, rows * row + 4f * scale), Texture2D.whiteTexture);
            for (int r = 0; r < rows; r++)
            {
                int i = rows == Legend.Count ? r : Mathf.RoundToInt(r * (Legend.Count - 1) / (float)(rows - 1));
                float top = y + 2f * scale + r * row;
                GUI.color = LegendColors[i];
                GUI.DrawTexture(new Rect(x + 2f * scale, top, swatch, row - 1f), Texture2D.whiteTexture);
                GUI.color = Color.white;
                GUI.Label(new Rect(x + swatch + 8f * scale, top, width - swatch - 8f * scale, row),
                    Legend[i].ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " mm", legendText);
            }
            GUI.color = Color.white;
        }

        private static void Draw()
        {
            if (Parts.Count == 0) return;
            if (material == null)
            {
                material = FaceHighlight.MakeTintMaterial(Color.white);   // the colors are the mesh's own
                if (material == null) return;
            }
            // Solid, except in the game's X-ray view, where armor deeper in the craft shows through the faces in front.
            bool xray = FaceHighlight.XRay;
            Color tint = new Color(1f, 1f, 1f, xray ? XrayAlpha : 1f);
            if (material.color != tint) material.color = tint;
            if (xray)
            {
                DrawBlockers();
                DrawXRay();
                return;
            }
            if (nearestMaterial == null) nearestMaterial = FaceHighlight.MakeDepthMaterial(FaceHighlight.RenderQueue - 50);
            foreach (KeyValuePair<PartScript, Entry> pair in Parts)
            {
                PartScript part = pair.Key;
                Entry entry = pair.Value;
                if (!Shown(part, entry)) continue;
                int layer = entry.LayerSource != null ? entry.LayerSource.gameObject.layer : part.gameObject.layer;
                Matrix4x4 matrix = entry.Frame.localToWorldMatrix;
                // The outside and the inside of the armored faces. Their depth first (all of them, before any color):
                // only the tinted surface nearest the camera is then colored at each point, so what shows does not
                // depend on the order the game draws the parts in (it changes with the camera).
                for (int sub = 0; sub <= FaceHighlight.InsideSubmesh; sub += FaceHighlight.InsideSubmesh)
                {
                    if (sub >= entry.Mesh.subMeshCount || entry.Mesh.GetIndexCount(sub) == 0) continue;
                    if (nearestMaterial != null)
                        Graphics.DrawMesh(entry.Mesh, matrix, nearestMaterial, layer, null, sub, null, ShadowCastingMode.Off, false);
                    Graphics.DrawMesh(entry.Mesh, matrix, material, layer, null, sub, null, ShadowCastingMode.Off, false);
                }
            }
        }

        private static bool Shown(PartScript part, Entry entry)
        {
            if (part == null || entry.Mesh == null || entry.Frame == null) return false;
            if (!part.gameObject.activeInHierarchy) return false;
            return part.PartMaterialScript == null || part.PartMaterialScript.Visible;   // not hidden in the designer
        }

        // ---- The X-ray view ------------------------------------------------------
        // See-through colors only blend right drawn from the farthest to the nearest, and the game sorts whole parts,
        // not triangles (so two parts' colors swapped as the camera turned). In the X-ray view every armored face of
        // the craft (outsides and insides) is one mesh in world space, its triangles put in order from the farthest
        // to the nearest whenever the camera or a part moves, and drawn in one go.
        private static Mesh xrayMesh;
        private static bool xrayDirty = true;
        private static Vector3 xrayEye = new Vector3(float.NaN, 0f, 0f);
        private static int xrayLayer;
        private static readonly List<Vector3> XrayVertices = new List<Vector3>();
        private static readonly List<Color> XrayColors = new List<Color>();
        private static readonly List<int> XrayTriangles = new List<int>();   // three vertex indices per triangle
        private static readonly List<Vector3> XrayCenters = new List<Vector3>();
        private static float[] xrayKeys = new float[0];
        private static int[] xrayOrder = new int[0];
        private static int[] xrayIndices = new int[0];

        private static void DrawXRay()
        {
            bool moved = xrayDirty;
            foreach (KeyValuePair<PartScript, Entry> pair in Parts)
            {
                Entry entry = pair.Value;
                bool shown = Shown(pair.Key, entry);
                Matrix4x4 matrix = shown ? entry.Frame.localToWorldMatrix : Matrix4x4.zero;
                if (shown != entry.LastShown || matrix != entry.LastMatrix)
                {
                    moved = true;
                    entry.LastShown = shown;
                    entry.LastMatrix = matrix;
                }
            }
            if (moved) BuildXRay();
            if (xrayMesh == null || XrayCenters.Count == 0) return;

            Designer designer = Designer.Instance;
            Camera camera = designer != null && designer.CameraController != null ? designer.CameraController.Camera : null;
            Vector3 eye = camera != null ? camera.transform.position : Vector3.zero;
            if (moved || eye != xrayEye)
            {
                xrayEye = eye;
                SortXRay(eye);
            }
            Graphics.DrawMesh(xrayMesh, Matrix4x4.identity, material, xrayLayer, null, 0, null, ShadowCastingMode.Off, false);
        }

        private static void BuildXRay()
        {
            xrayDirty = false;
            XrayVertices.Clear();
            XrayColors.Clear();
            XrayTriangles.Clear();
            XrayCenters.Clear();
            bool layerSet = false;
            foreach (KeyValuePair<PartScript, Entry> pair in Parts)
            {
                Entry entry = pair.Value;
                if (!entry.LastShown) continue;
                if (!layerSet)
                {
                    xrayLayer = entry.LayerSource != null ? entry.LayerSource.gameObject.layer : pair.Key.gameObject.layer;
                    layerSet = true;
                }
                Vector3[] local = entry.Mesh.vertices;
                Color[] colors = entry.Mesh.colors;
                Matrix4x4 matrix = entry.LastMatrix;
                int first = XrayVertices.Count;
                for (int v = 0; v < local.Length; v++)
                {
                    XrayVertices.Add(matrix.MultiplyPoint3x4(local[v]));
                    XrayColors.Add(v < colors.Length ? colors[v] : Color.white);
                }
                for (int sub = 0; sub <= FaceHighlight.InsideSubmesh; sub += FaceHighlight.InsideSubmesh)
                {
                    if (sub >= entry.Mesh.subMeshCount) continue;
                    int[] triangles = entry.Mesh.GetTriangles(sub);
                    for (int t = 0; t + 2 < triangles.Length; t += 3)
                    {
                        int a = first + triangles[t], b = first + triangles[t + 1], c = first + triangles[t + 2];
                        XrayTriangles.Add(a);
                        XrayTriangles.Add(b);
                        XrayTriangles.Add(c);
                        XrayCenters.Add((XrayVertices[a] + XrayVertices[b] + XrayVertices[c]) / 3f);
                    }
                }
            }

            if (xrayMesh == null) xrayMesh = new Mesh { name = "ArmorViewXRay" };
            xrayMesh.Clear();
            xrayMesh.indexFormat = XrayVertices.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            xrayMesh.SetVertices(XrayVertices);
            xrayMesh.SetColors(XrayColors);
            int count = XrayCenters.Count;
            if (xrayKeys.Length < count) xrayKeys = new float[count];
            if (xrayOrder.Length < count) xrayOrder = new int[count];
            if (xrayIndices.Length != count * 3) xrayIndices = new int[count * 3];
            xrayMesh.SetIndices(XrayTriangles, MeshTopology.Triangles, 0, false);
            xrayMesh.RecalculateBounds();
        }

        // Farthest first.
        private static void SortXRay(Vector3 eye)
        {
            int count = XrayCenters.Count;
            for (int i = 0; i < count; i++)
            {
                xrayKeys[i] = -(XrayCenters[i] - eye).sqrMagnitude;
                xrayOrder[i] = i;
            }
            Array.Sort(xrayKeys, xrayOrder, 0, count);
            for (int i = 0; i < count; i++)
            {
                int t = xrayOrder[i] * 3;
                xrayIndices[i * 3] = XrayTriangles[t];
                xrayIndices[i * 3 + 1] = XrayTriangles[t + 1];
                xrayIndices[i * 3 + 2] = XrayTriangles[t + 2];
            }
            xrayMesh.SetIndices(xrayIndices, MeshTopology.Triangles, 0, false);
        }

        private static void Clear()
        {
            foreach (Entry entry in Parts.Values)
                if (entry.Mesh != null) Object.Destroy(entry.Mesh);
            Parts.Clear();
            if (xrayMesh != null) Object.Destroy(xrayMesh);
            xrayMesh = null;
            xrayDirty = true;
            Blockers.Clear();
            Legend.Clear();
            LegendColors.Clear();
        }

        // The plugin is being unloaded: the tints, material and shield are freed.
        internal static void Shutdown()
        {
            Clear();
            On = false;
            button = null;
            if (material != null) Object.Destroy(material);
            material = null;
            if (depthMaterial != null) Object.Destroy(depthMaterial);
            depthMaterial = null;
            if (nearestMaterial != null) Object.Destroy(nearestMaterial);
            nearestMaterial = null;
            if (shield != null)
            {
                Object.Destroy(shield.texture);
                Object.Destroy(shield);
            }
            shield = null;
        }
    }

    // The designer's UI was built from its layout (with the shield button in it).
    [HarmonyPatch(typeof(DesignerUIScript), "Initialize")]
    internal static class ArmorViewButtonPatch
    {
        private static void Postfix(DesignerUIScript __instance)
        {
            try
            {
                ArmorView.DesignerUi = __instance;
                ArmorView.Attach(__instance.RootWidget);
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Armor view: couldn't set up the button: " + e.Message);
            }
        }
    }
}
