using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Assets.Scripts.Craft;
using Assets.Scripts.Craft.Parts;
using Assets.Scripts.Flight;
using Assets.Scripts.Flight.Cameras;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

namespace ArmorFragments
{
    // PHASE 1 OF THE DAMAGE MAPS: A PROBE. It answers, in the game and in the log, the questions the design depends on
    // (it changes nothing about shots or damage):
    //
    //  1. DENT: can a normal map be added to a part WITHOUT replacing the game's paint? The part keeps its own material
    //     (a clone the theme keeps up to date, so the paint is the game's), and only its normal map and a UV0 are added.
    //     The "sum" is the shader's own: paint + normal map.
    //  2. HOLE: can a part show a see-through hole? The game's part shader has no alpha test, so this tries the URP Lit
    //     shader with alpha clipping (its base color is taken from the part's paint, which is only an approximation).
    //  3. UPDATE: can a small tile that a worker thread has computed be put into a bigger texture without a hitch? The
    //     tile goes into the atlas with Graphics.CopyTexture, one per frame, and every step is timed in the log.
    //  4. INVENTORY: what the part's meshes and materials really are (UV0, tangents, merged or not, shaders and their
    //     keywords), which decides how the final version has to be built.
    //
    // Use: turn on "Keep armor plates out of the mesh merge" in the panel, start a flight (the game merges the meshes of
    // a group when the flight starts: a plate that was merged cannot be changed), open the panel's "Damage probe" section,
    // click a button and, within 3 seconds, put the mouse over the part. Everything is written to the log as [PROBE].
    internal static class DamageProbe
    {
        private const string Tag = "[PROBE] ";
        private const int AtlasSize = 512;   // the atlas: four tiles
        private const int TileSize = 256;    // ... the damage lives in the lower left one (the rest is flat)
        private const float Countdown = 3f;

        public enum Kind { Dent, HoleLit }

        // What the panel shows.
        public static string Message = "No probe yet";

        private sealed class Applied
        {
            public PartMaterialScript.RendererMaterialMap Map;
            public Mesh Mesh;
            public Material[] OriginalMaterials;
            public bool OriginalExclude;
            public List<Vector2> OriginalUv0;
            public List<Vector4> OriginalTangents;
            public Material Material;
            public bool FromTheme;
        }

        private static readonly List<Applied> Active = new List<Applied>();
        private static ThemeScript activeTheme;
        private static Texture2D normalAtlas;
        private static Texture2D alphaAtlas;
        private static Texture2D staging;
        private static bool shadersDumped;

        // A click waits a moment so the mouse can be put on the part.
        private static bool pending;
        private static Kind pendingKind;
        private static float pendingAt;

        // The worker thread's finished tiles, waiting for the main thread.
        private sealed class Frame
        {
            public int Step;
            public byte[] Pixels;
            public double ComputeMs;
        }

        private static readonly ConcurrentQueue<Frame> Ready = new ConcurrentQueue<Frame>();
        private static int generation;   // a new run (or a reset) makes the older worker stop

        // ---- Panel ---------------------------------------------------------------------------------

        public static void Request(Kind kind)
        {
            pending = true;
            pendingKind = kind;
            pendingAt = Time.unscaledTime + Countdown;
            Message = "Put the mouse over the part: " + Countdown.ToString("F0") + " s";
        }

        public static void Tick()
        {
            if (pending)
            {
                float left = pendingAt - Time.unscaledTime;
                if (left > 0f)
                {
                    Message = "Put the mouse over the part: " + Mathf.CeilToInt(left) + " s";
                }
                else
                {
                    pending = false;
                    try
                    {
                        Run(pendingKind);
                    }
                    catch (Exception e)
                    {
                        Message = "Probe failed: " + e.Message;
                        ArmorFragmentsPlugin.Log.LogError(Tag + "failed: " + e);
                    }
                }
            }

            DeliverTile();
        }

        // ---- The probe -----------------------------------------------------------------------------

        private static void Run(Kind kind)
        {
            Reset();

            RaycastHit hit;
            PartScript part = PickPart(out hit);
            if (part == null)
            {
                Message = "No part under the mouse (use the orbit camera and aim at an armor plate)";
                ArmorFragmentsPlugin.Log.LogWarning(Tag + Message);
                return;
            }

            AircraftScript aircraft = part.Aircraft;
            ThemeScript theme = aircraft != null ? aircraft.Theme : null;
            if (theme == null)
            {
                Message = "The part has no aircraft theme";
                return;
            }

            DumpShaders(theme);
            Describe(part);

            Vector3 hitLocal = part.transform.InverseTransformPoint(hit.point);
            Vector3 normalLocal = part.transform.InverseTransformDirection(hit.normal).normalized;
            float tile = ArmorFragmentsPlugin.ProbeTileMeters.Value;

            BuildAtlases(kind == Kind.HoleLit);
            activeTheme = theme;

            int touched = 0;
            int merged = 0;
            int skipped = 0;
            foreach (PartMaterialScript.RendererMaterialMap map in part.PartMaterialScript.RendererMaps)
            {
                Renderer renderer = map.Renderer;
                Mesh mesh = map.Mesh;
                string why = null;
                if (renderer == null || mesh == null) why = "no renderer or mesh";
                else if (!map.MeshIsUnique) why = "the mesh is shared with other parts (never changed)";
                else if (!mesh.isReadable) why = "the mesh is not readable";
                else if (mesh.vertexCount == 0) why = "the mesh is empty";
                else if (!renderer.enabled) { why = "its renderer is off: the part was merged into its group's mesh"; merged++; }

                if (why != null)
                {
                    skipped++;
                    ArmorFragmentsPlugin.Log.LogInfo(Tag + "renderer skipped: " + why);
                    continue;
                }

                Applied applied = new Applied
                {
                    Map = map,
                    Mesh = mesh,
                    OriginalMaterials = map.OriginalMaterials,
                    OriginalExclude = map.ExcludeFromMeshCombine,
                    OriginalUv0 = new List<Vector2>(),
                    OriginalTangents = new List<Vector4>(),
                };
                mesh.GetUVs(0, applied.OriginalUv0);
                mesh.GetTangents(applied.OriginalTangents);

                // UV0 on the part's local frame, then the tangents the normal map needs.
                Matrix4x4 toPart = part.transform.worldToLocalMatrix * renderer.localToWorldMatrix;
                int mixed;
                int facing = WriteUv(mesh, toPart, hitLocal, normalLocal, tile, out mixed);
                mesh.RecalculateTangents();
                ArmorFragmentsPlugin.Log.LogInfo(Tag + "UV0 written: " + facing + " of " + mesh.vertexCount +
                    " vertices face the hit, " + mixed + " triangles mix facing and not facing vertices (those smear)");

                Material material;
                if (kind == Kind.HoleLit)
                {
                    material = MakeLit(theme, part);
                    applied.FromTheme = false;
                }
                else
                {
                    material = theme.RequestDefaultPartMaterialInstance();   // the game's own paint, kept up to date by the theme
                    applied.FromTheme = true;
                    if (material != null)
                    {
                        material.SetTexture("_BumpMap", normalAtlas);
                        material.SetFloat("_BumpScale", 1f);
                        material.EnableKeyword("_NORMALMAP");
                    }
                }
                if (material == null)
                {
                    mesh.SetUVs(0, applied.OriginalUv0);
                    ArmorFragmentsPlugin.Log.LogWarning(Tag + "no material could be made");
                    continue;
                }
                applied.Material = material;

                Material[] materials = new Material[map.OriginalMaterials.Length];
                for (int i = 0; i < materials.Length; i++) materials[i] = material;
                map.OriginalMaterials = materials;
                map.ExcludeFromMeshCombine = true;
                map.SetRendererMaterial(materials);

                Active.Add(applied);
                touched++;
            }

            Message = (kind == Kind.HoleLit ? "Hole (Lit)" : "Dent") + " on '" + part.Part.Name + "': " + touched +
                      " renderer(s)" +
                      (merged > 0 ? ". It was merged into its group's mesh: turn on 'Keep armor plates out of the mesh merge' and start the flight again" : "") +
                      (touched > 0 || merged > 0 ? "" : ", nothing changed");
            ArmorFragmentsPlugin.Log.LogInfo(Tag + Message);
        }

        // Puts everything back as it was.
        public static void Reset()
        {
            generation++;
            Frame dropped;
            while (Ready.TryDequeue(out dropped)) { }

            foreach (Applied a in Active)
            {
                try
                {
                    if (a.Map != null)
                    {
                        a.Map.OriginalMaterials = a.OriginalMaterials;
                        a.Map.ExcludeFromMeshCombine = a.OriginalExclude;
                        a.Map.SetRendererMaterial(a.OriginalMaterials);
                    }
                    if (a.Mesh != null)
                    {
                        a.Mesh.SetUVs(0, a.OriginalUv0);
                        a.Mesh.SetTangents(a.OriginalTangents);
                    }
                    if (a.Material != null)
                    {
                        if (a.FromTheme && activeTheme != null) activeTheme.ReleaseDefaultPartMaterialInstance(a.Material);
                        else UnityEngine.Object.Destroy(a.Material);
                    }
                }
                catch (Exception e)
                {
                    ArmorFragmentsPlugin.Log.LogWarning(Tag + "reset failed for a renderer: " + e.Message);
                }
            }
            Active.Clear();

            if (normalAtlas != null) UnityEngine.Object.Destroy(normalAtlas);
            if (alphaAtlas != null) UnityEngine.Object.Destroy(alphaAtlas);
            if (staging != null) UnityEngine.Object.Destroy(staging);
            normalAtlas = alphaAtlas = staging = null;
            activeTheme = null;
            Message = "Reset";
        }

        // ---- Tile updates (the thread and the copy) ------------------------------------------------

        // The crater grows in 8 steps. A worker thread computes each step's tile; the main thread puts one tile per
        // frame into the atlas. All the times are logged.
        public static void Animate()
        {
            if (Active.Count == 0 || normalAtlas == null)
            {
                Message = "Put a dent or a hole on a part first";
                return;
            }

            if (staging == null)
            {
                staging = new Texture2D(TileSize, TileSize, TextureFormat.RGBA32, false, true);
                staging.wrapMode = TextureWrapMode.Clamp;
            }

            ArmorFragmentsPlugin.Log.LogInfo(Tag + "copy support: " + SystemInfo.copyTextureSupport +
                ", graphics " + SystemInfo.graphicsDeviceType + ", cores " + SystemInfo.processorCount);

            int mine = ++generation;
            ThreadPool.QueueUserWorkItem(delegate
            {
                for (int step = 1; step <= 8; step++)
                {
                    if (mine != generation) return;
                    System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
                    byte[] pixels = new byte[TileSize * TileSize * 4];
                    DrawCrater(pixels, TileSize, step / 8f);
                    Ready.Enqueue(new Frame { Step = step, Pixels = pixels, ComputeMs = watch.Elapsed.TotalMilliseconds });
                    Thread.Sleep(250);   // standing in for a simulation that takes its time
                }
            });
            Message = "Animating: 8 tiles from a worker thread";
        }

        private static void DeliverTile()
        {
            Frame frame;
            if (normalAtlas == null || staging == null || !Ready.TryDequeue(out frame)) return;

            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
            staging.SetPixelData(frame.Pixels, 0);
            staging.Apply(false, false);
            double uploadMs = watch.Elapsed.TotalMilliseconds;

            watch.Restart();
            Graphics.CopyTexture(staging, 0, 0, 0, 0, TileSize, TileSize, normalAtlas, 0, 0, 0, 0);
            double copyMs = watch.Elapsed.TotalMilliseconds;

            ArmorFragmentsPlugin.Log.LogInfo(Tag + "tile " + frame.Step + "/8: worker " + frame.ComputeMs.ToString("F2") +
                " ms, upload " + uploadMs.ToString("F2") + " ms, copy " + copyMs.ToString("F2") + " ms (main thread)");
            Message = "Tile " + frame.Step + "/8 delivered (" + (uploadMs + copyMs).ToString("F2") + " ms on the main thread)";
        }

        // ---- Textures ------------------------------------------------------------------------------

        private static void BuildAtlases(bool withAlpha)
        {
            byte[] normals = new byte[AtlasSize * AtlasSize * 4];
            for (int i = 0; i < normals.Length; i += 4)
            {
                normals[i] = 128; normals[i + 1] = 128; normals[i + 2] = 255; normals[i + 3] = 255;   // flat
            }

            byte[] tile = new byte[TileSize * TileSize * 4];
            DrawCrater(tile, TileSize, 1f);
            for (int y = 0; y < TileSize; y++)
                Buffer.BlockCopy(tile, y * TileSize * 4, normals, y * AtlasSize * 4, TileSize * 4);

            normalAtlas = new Texture2D(AtlasSize, AtlasSize, TextureFormat.RGBA32, false, true);
            normalAtlas.wrapMode = TextureWrapMode.Clamp;
            normalAtlas.SetPixelData(normals, 0);
            normalAtlas.Apply(false, false);

            if (!withAlpha) return;

            // White everywhere, see-through (alpha 0) inside the hole of the lower left tile.
            byte[] colors = new byte[AtlasSize * AtlasSize * 4];
            for (int i = 0; i < colors.Length; i++) colors[i] = 255;
            float center = TileSize * 0.5f;
            float radius = TileSize * 0.16f;
            for (int y = 0; y < TileSize; y++)
            {
                for (int x = 0; x < TileSize; x++)
                {
                    float dx = x + 0.5f - center;
                    float dy = y + 0.5f - center;
                    float angle = Mathf.Atan2(dy, dx);
                    float edge = radius * (1f + 0.15f * Mathf.Sin(5f * angle + 1f) + 0.08f * Mathf.Sin(9f * angle + 2f));
                    if (dx * dx + dy * dy < edge * edge) colors[(y * AtlasSize + x) * 4 + 3] = 0;
                }
            }
            alphaAtlas = new Texture2D(AtlasSize, AtlasSize, TextureFormat.RGBA32, false, false);
            alphaAtlas.wrapMode = TextureWrapMode.Clamp;
            alphaAtlas.SetPixelData(colors, 0);
            alphaAtlas.Apply(false, false);
        }

        // A bowl with a raised rim, as a tangent-space normal map: flat (128,128,255) at the edges of the tile.
        // 'growth' (0..1) is how far the crater has got.
        private static void DrawCrater(byte[] rgba, int size, float growth)
        {
            float[] height = new float[size * size];
            float center = size * 0.5f;
            float radius = size * 0.34f * Mathf.Max(growth, 0.05f);
            float depth = size * 0.05f * growth;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x + 0.5f - center;
                    float dy = y + 0.5f - center;
                    float r = Mathf.Sqrt(dx * dx + dy * dy) / radius;
                    float h = 0f;
                    if (r < 1f)
                    {
                        float bowl = 1f - r * r;
                        h = -depth * bowl * bowl;
                    }
                    float rim = (r - 1.18f) / 0.14f;
                    h += 0.3f * depth * Mathf.Exp(-rim * rim);
                    height[y * size + x] = h;
                }
            }

            const float slopeScale = 3f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float left = height[y * size + Mathf.Max(x - 1, 0)];
                    float right = height[y * size + Mathf.Min(x + 1, size - 1)];
                    float down = height[Mathf.Max(y - 1, 0) * size + x];
                    float up = height[Mathf.Min(y + 1, size - 1) * size + x];
                    float nx = -(right - left) * 0.5f * slopeScale;
                    float ny = -(up - down) * 0.5f * slopeScale;
                    float inverse = 1f / Mathf.Sqrt(nx * nx + ny * ny + 1f);
                    int at = (y * size + x) * 4;
                    rgba[at] = (byte)Mathf.Clamp(Mathf.RoundToInt((nx * inverse * 0.5f + 0.5f) * 255f), 0, 255);
                    rgba[at + 1] = (byte)Mathf.Clamp(Mathf.RoundToInt((ny * inverse * 0.5f + 0.5f) * 255f), 0, 255);
                    rgba[at + 2] = (byte)Mathf.Clamp(Mathf.RoundToInt((inverse * 0.5f + 0.5f) * 255f), 0, 255);
                    rgba[at + 3] = 255;
                }
            }
        }

        // UV0: the plane across the hit (its normal), centered on the hit, 'tile' meters wide, squeezed into the lower
        // left quarter of the atlas. Vertices that face away get a spot in the flat quarter, so the other side of a
        // slab does not show the dent. Returns how many vertices face the hit.
        private static int WriteUv(Mesh mesh, Matrix4x4 toPart, Vector3 hit, Vector3 normal, float tile, out int mixed)
        {
            int count = mesh.vertexCount;
            List<Vector3> vertices = new List<Vector3>(count);
            List<Vector3> normals = new List<Vector3>(count);
            mesh.GetVertices(vertices);
            mesh.GetNormals(normals);
            bool hasNormals = normals.Count == count;

            Vector3 t1 = Vector3.Cross(normal, Mathf.Abs(normal.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            Vector3 t2 = Vector3.Cross(normal, t1);

            bool[] facing = new bool[count];
            int facingCount = 0;
            List<Vector2> uvs = new List<Vector2>(count);
            for (int i = 0; i < count; i++)
            {
                Vector3 p = toPart.MultiplyPoint3x4(vertices[i]);
                Vector3 n = hasNormals ? toPart.MultiplyVector(normals[i]).normalized : normal;
                facing[i] = Vector3.Dot(n, normal) > 0f;
                if (facing[i])
                {
                    Vector3 d = p - hit;
                    uvs.Add(new Vector2(Vector3.Dot(d, t1) / tile + 0.5f, Vector3.Dot(d, t2) / tile + 0.5f) * 0.5f);
                    facingCount++;
                }
                else
                {
                    uvs.Add(new Vector2(0.75f, 0.75f));
                }
            }
            mesh.SetUVs(0, uvs);

            mixed = 0;
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                int[] triangles = mesh.GetTriangles(s);
                for (int k = 0; k + 2 < triangles.Length; k += 3)
                {
                    bool a = facing[triangles[k]];
                    if (a != facing[triangles[k + 1]] || a != facing[triangles[k + 2]]) mixed++;
                }
            }
            return facingCount;
        }

        // The hole: URP Lit with alpha clipping, painted with the part's own color (an approximation of its paint).
        private static Material MakeLit(ThemeScript theme, PartScript part)
        {
            Shader lit = Shader.Find("Universal Render Pipeline/Lit");
            if (lit == null || !lit.isSupported)
            {
                ArmorFragmentsPlugin.Log.LogWarning(Tag + "the URP Lit shader is not available: no hole test");
                return null;
            }

            Color color = new Color(0.5f, 0.5f, 0.5f, 1f);
            float metallic = 0f;
            float smoothness = 0.4f;
            try
            {
                PartMaterial paint = theme.Theme.GetMaterial(part.Part.MaterialIds[0]);
                if (paint != null && paint.ColorData != null && paint.ColorData.Length > 0)
                {
                    color = paint.PrimaryColor;
                    color.a = 1f;
                    metallic = paint.PrimaryColorMetallic;
                    smoothness = paint.PrimaryColorSmoothness;
                    ArmorFragmentsPlugin.Log.LogInfo(Tag + "paint of the part: style " + paint.Style + ", color " + color +
                        ", metallic " + metallic.ToString("F2") + ", smoothness " + smoothness.ToString("F2"));
                }
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning(Tag + "the part's paint could not be read: " + e.Message);
            }

            Material material = new Material(lit);
            material.name = "ArmorFragments probe (Lit)";
            material.SetTexture("_BaseMap", alphaAtlas);
            material.SetColor("_BaseColor", color);
            material.SetFloat("_Metallic", metallic);
            material.SetFloat("_Smoothness", smoothness);
            material.SetTexture("_BumpMap", normalAtlas);
            material.SetFloat("_BumpScale", 1f);
            material.EnableKeyword("_NORMALMAP");
            material.SetFloat("_AlphaClip", 1f);
            material.SetFloat("_Cutoff", 0.5f);
            material.EnableKeyword("_ALPHATEST_ON");
            material.SetFloat("_Surface", 0f);
            material.SetFloat("_Cull", ArmorFragmentsPlugin.ProbeHoleCullOff.Value ? 0f : 2f);
            material.SetOverrideTag("RenderType", "TransparentCutout");
            material.renderQueue = (int)RenderQueue.AlphaTest;
            ArmorFragmentsPlugin.Log.LogInfo(Tag + "Lit material: alpha-test keyword on = " + material.IsKeywordEnabled("_ALPHATEST_ON") +
                ", normal-map keyword on = " + material.IsKeywordEnabled("_NORMALMAP") + ", cull " + material.GetFloat("_Cull"));
            return material;
        }

        // ---- Picking and reporting -----------------------------------------------------------------

        private static PartScript PickPart(out RaycastHit found)
        {
            found = default(RaycastHit);
            CameraManagerScript manager = CameraManagerScript.Instance;
            Camera camera = manager != null && manager.MainCamera != null ? manager.MainCamera : Camera.main;
            if (camera == null) return null;

            Vector2 screen = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            Mouse mouse = Mouse.current;
            if (mouse != null) screen = mouse.position.ReadValue();

            int mask = -1 & ~LayerMask.GetMask("WorldSpaceUI");
            RaycastHit[] hits = Physics.RaycastAll(camera.ScreenPointToRay(screen), 2000f, mask, QueryTriggerInteraction.Ignore);
            Array.Sort(hits, (x, y) => x.distance.CompareTo(y.distance));
            foreach (RaycastHit hit in hits)
            {
                PartScript part = hit.collider.GetComponentInParent<PartScript>();
                if (part == null) continue;
                found = hit;
                return part;
            }
            return null;
        }

        private static void Describe(PartScript part)
        {
            StringBuilder text = new StringBuilder();
            PartTags tags = TagCodec.Parse(part.Part.Name);
            text.Append("part '").Append(part.Part.Name).Append("' (").Append(part.Part.PartType.Name).Append("), armor tag ")
                .Append(tags.Armor != null ? "yes" : "no").Append(", colliders:");
            foreach (Collider collider in part.GetComponentsInChildren<Collider>())
                text.Append(' ').Append(collider.GetType().Name).Append(collider.isTrigger ? "(trigger)" : "");
            ArmorFragmentsPlugin.Log.LogInfo(Tag + text);

            int index = 0;
            foreach (PartMaterialScript.RendererMaterialMap map in part.PartMaterialScript.RendererMaps)
            {
                StringBuilder line = new StringBuilder(Tag + "renderer " + index++ + ": ");
                Renderer renderer = map.Renderer;
                line.Append(renderer != null ? renderer.GetType().Name : "null");
                if (renderer != null)
                    line.Append(", enabled ").Append(renderer.enabled).Append(", active ").Append(renderer.gameObject.activeInHierarchy);
                line.Append(", excludeFromMerge ").Append(map.ExcludeFromMeshCombine).Append(", uniqueMesh ").Append(map.MeshIsUnique);
                Mesh mesh = map.Mesh;
                if (mesh != null)
                {
                    line.Append(", mesh '").Append(mesh.name).Append("' ").Append(mesh.vertexCount).Append(" vertices, ")
                        .Append(mesh.subMeshCount).Append(" submeshes, readable ").Append(mesh.isReadable);
                    if (mesh.isReadable && mesh.vertexCount > 0)
                    {
                        List<Vector2> uv0 = new List<Vector2>();
                        mesh.GetUVs(0, uv0);
                        int nonZero = 0;
                        foreach (Vector2 uv in uv0) if (uv.sqrMagnitude > 0.000001f) nonZero++;
                        List<Vector4> tangents = new List<Vector4>();
                        mesh.GetTangents(tangents);
                        line.Append(", UV0 non-zero ").Append(nonZero).Append("/").Append(uv0.Count).Append(", tangents ").Append(tangents.Count)
                            .Append(", bounds ").Append(mesh.bounds.size);
                    }
                }
                if (map.OriginalMaterials != null && map.OriginalMaterials.Length > 0 && map.OriginalMaterials[0] != null)
                    line.Append(", material '").Append(map.OriginalMaterials[0].name).Append("' shader '")
                        .Append(map.OriginalMaterials[0].shader.name).Append("'");
                if (map.MaterialTypes != null)
                    line.Append(", types ").Append(string.Join(",", Array.ConvertAll(map.MaterialTypes, t => t.ToString())));
                ArmorFragmentsPlugin.Log.LogInfo(line.ToString());
            }
        }

        // The properties and keywords of the shaders that matter. Written once per run of the game.
        private static void DumpShaders(ThemeScript theme)
        {
            if (shadersDumped) return;
            shadersDumped = true;
            DumpShader("part", theme.Material != null ? theme.Material.shader : null);
            DumpShader("part transparent", theme.MaterialTransparent != null ? theme.MaterialTransparent.shader : null);
            DumpShader("URP Lit", Shader.Find("Universal Render Pipeline/Lit"));
        }

        private static void DumpShader(string label, Shader shader)
        {
            if (shader == null)
            {
                ArmorFragmentsPlugin.Log.LogInfo(Tag + "shader " + label + ": NOT FOUND");
                return;
            }

            StringBuilder properties = new StringBuilder();
            for (int i = 0; i < shader.GetPropertyCount(); i++)
                properties.Append(shader.GetPropertyName(i)).Append(':').Append(shader.GetPropertyType(i)).Append(' ');

            StringBuilder keywords = new StringBuilder();
            try
            {
                foreach (LocalKeyword keyword in shader.keywordSpace.keywords) keywords.Append(keyword.name).Append(' ');
            }
            catch (Exception e)
            {
                keywords.Append("(not readable: " + e.Message + ")");
            }

            ArmorFragmentsPlugin.Log.LogInfo(Tag + "shader " + label + " '" + shader.name + "' supported " + shader.isSupported +
                ", queue " + shader.renderQueue + ", properties: " + properties);
            ArmorFragmentsPlugin.Log.LogInfo(Tag + "shader " + label + " keywords: " + keywords);
        }
    }

    // In flight a group of parts merges the meshes of its parts into one when it initializes. A plate that is merged has
    // no renderer of its own, so its look cannot be changed afterwards. With the setting on, armor plates are marked
    // "do not merge" just before that, and keep their own renderer (a few more draw calls).
    [HarmonyPatch(typeof(PartGroupScript), "Initialize")]
    internal static class PlateMergePatch
    {
        private static void Prefix(PartGroupScript __instance)
        {
            try
            {
                if (!ArmorFragmentsPlugin.KeepPlatesApart.Value) return;
                BodyScript body = __instance.Body;
                AircraftScript aircraft = body != null ? body.Aircraft : null;
                if (aircraft == null || aircraft.LoadContext != CraftLoadContext.Flight || aircraft.RemoteAircraft) return;

                int plates = 0;
                foreach (PartScript part in __instance.Parts)
                {
                    if (part == null || part.Part == null || part.PartMaterialScript == null) continue;
                    if (TagCodec.Parse(part.Part.Name).Armor == null) continue;
                    foreach (PartMaterialScript.RendererMaterialMap map in part.PartMaterialScript.RendererMaps)
                        map.ExcludeFromMeshCombine = true;
                    plates++;
                }
                if (plates > 0)
                    ArmorFragmentsPlugin.Log.LogInfo("[PROBE] " + plates + " armor plate(s) of a group kept out of the mesh merge");
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("[PROBE] plate merge patch failed: " + e.Message);
            }
        }
    }
}
