using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Assets.Scripts.Craft;
using Assets.Scripts.Craft.Parts;
using UnityEngine;
using UnityEngine.Rendering;

namespace ArmorFragments
{
    // The damage maps of one vehicle: ONE atlas of normal maps and ONE atlas of hole masks, with a tile (two: front and
    // back) for each of its armor plates. A plate that is damaged gets its UV0 pointing at its tiles, and a material:
    //  - the game's own part material (a clone the theme keeps up to date, so the paint is the game's) with the normal
    //    atlas as its normal map: the damage is added to the paint by the shader;
    //  - once it has a hole, a URP Lit material (alpha clipping, the paint's color, the same normal atlas and the hole
    //    atlas): the game's part shader has no alpha test, so a see-through hole needs this one.
    // The damage is computed by a worker thread (DamageWorker) and shown a rectangle at a time (Graphics.CopyTexture).
    internal sealed class VehicleMaps
    {
        public readonly AircraftScript Aircraft;
        public readonly ThemeScript Theme;
        public int AtlasSize;
        public Texture2D Normal;
        public Texture2D Alpha;
        public Material Clone;       // the game's part material with the normal atlas
        public float LastUsed;
        public bool Disposed;
        public readonly Dictionary<PartScript, PlateVisual> Plates = new Dictionary<PartScript, PlateVisual>();

        private const int SentinelSize = 8;   // a flat corner of the atlas, for vertices that belong to no face (the sides)

        private VehicleMaps(AircraftScript aircraft, ThemeScript theme)
        {
            Aircraft = aircraft;
            Theme = theme;
        }

        public static string LastFailure = "";

        // Lays out the tiles of every armor plate of the aircraft and makes the atlases. Null if there is nothing to do.
        public static VehicleMaps Create(AircraftScript aircraft)
        {
            ThemeScript theme = aircraft.Theme;
            if (theme == null) { LastFailure = "no theme"; return null; }

            List<PlateInfo> infos = new List<PlateInfo>();
            foreach (PartData data in aircraft.Parts)
            {
                PartScript part = data != null ? data.PartScript : null;
                if (part == null || TagCodec.Parse(data.Name).Armor == null) continue;
                PlateInfo info = PlateDamageMaps.InfoOf(part);
                if (info != null) infos.Add(info);
            }
            if (infos.Count == 0) { LastFailure = "no armor plate with a readable mesh"; return null; }

            // The tiles: as dense as the setting says, less dense if they do not fit in 2048 x 2048.
            float density = Mathf.Max(ArmorFragmentsPlugin.DamageMapsDensity.Value, 16f);
            int size = 0;
            List<int[]> tiles = null;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                long area = 0;
                List<int[]> sizes = new List<int[]>();
                foreach (PlateInfo info in infos)
                {
                    int w = Mathf.CeilToInt(info.Geometry.SizeU * density) + 2 * PlateCpu.Border;
                    int h = Mathf.CeilToInt(info.Geometry.SizeV * density) + 2 * PlateCpu.Border;
                    sizes.Add(new[] { w, h });
                    area += 2L * w * h;
                }
                int candidate = 64;
                while (candidate < 2048 && (long)candidate * candidate < (area * 13L) / 10L + 4096) candidate *= 2;
                for (int atlas = candidate; atlas <= 2048 && tiles == null; atlas *= 2)
                {
                    List<int[]> placed = Pack(sizes, atlas);
                    if (placed != null) { size = atlas; tiles = placed; }
                }
                if (tiles != null) break;
                density *= 0.7f;
            }
            if (tiles == null) { LastFailure = "the plates do not fit in an atlas"; return null; }

            VehicleMaps maps = new VehicleMaps(aircraft, theme) { AtlasSize = size, LastUsed = Time.time };
            for (int i = 0; i < infos.Count; i++)
            {
                PlateInfo info = infos[i];
                int w = tiles[i][2], h = tiles[i][3];
                PlateCpu cpu = new PlateCpu
                {
                    Width = w,
                    Height = h,
                    PxU = (w - 2 * PlateCpu.Border) / info.Geometry.SizeU,
                    PxV = (h - 2 * PlateCpu.Border) / info.Geometry.SizeV,
                };
                cpu.TileX[0] = tiles[i][0]; cpu.TileY[0] = tiles[i][1];
                cpu.TileX[1] = tiles[i][4]; cpu.TileY[1] = tiles[i][5];
                maps.Plates[info.Part] = new PlateVisual { Info = info, Cpu = cpu, Owner = maps };
            }

            maps.Normal = new Texture2D(size, size, TextureFormat.RGBA32, false, true) { wrapMode = TextureWrapMode.Clamp, name = "ArmorFragments damage normals" };
            byte[] flat = new byte[size * size * 4];
            FillPattern(flat, 128, 128, 255, 255);   // flat normals
            maps.Normal.SetPixelData(flat, 0);
            maps.Normal.Apply(false, false);

            maps.Alpha = new Texture2D(size, size, TextureFormat.Alpha8, false) { wrapMode = TextureWrapMode.Clamp, name = "ArmorFragments damage holes" };
            byte[] solid = new byte[size * size];
            FillPattern(solid, 255);   // no holes
            maps.Alpha.SetPixelData(solid, 0);
            maps.Alpha.Apply(false, false);

            ArmorFragmentsPlugin.Log.LogInfo("[DMAP] atlas " + size + "x" + size + " for " + infos.Count + " armor plate(s) of '" +
                                             aircraft.name + "', " + density.ToString("F0") + " px/m");
            return maps;
        }

        // Fills an array with a repeating pattern, by copying what is already there (fast for big arrays).
        private static void FillPattern(byte[] data, params byte[] pattern)
        {
            for (int i = 0; i < pattern.Length && i < data.Length; i++) data[i] = pattern[i];
            int filled = pattern.Length;
            while (filled < data.Length)
            {
                int copy = Math.Min(filled, data.Length - filled);
                Buffer.BlockCopy(data, 0, data, filled, copy);
                filled += copy;
            }
        }

        // Shelf packing: each plate needs two tiles of its size (front, back). Returns x, y of the front and back tiles
        // with the size in front: {x1, y1, w, h, x2, y2}. Null if they do not fit.
        private static List<int[]> Pack(List<int[]> sizes, int atlas)
        {
            int[] order = new int[sizes.Count];
            for (int i = 0; i < order.Length; i++) order[i] = i;
            Array.Sort(order, (a, b) => sizes[b][1].CompareTo(sizes[a][1]));

            int[][] result = new int[sizes.Count][];
            int x = 0, y = SentinelSize, shelf = 0;
            foreach (int index in order)
            {
                int w = sizes[index][0], h = sizes[index][1];
                int[] at = new int[6];
                at[2] = w; at[3] = h;
                for (int face = 0; face < 2; face++)
                {
                    if (x + w > atlas) { y += shelf; x = 0; shelf = 0; }
                    if (y + h > atlas || w > atlas) return null;
                    at[face == 0 ? 0 : 4] = x;
                    at[face == 0 ? 1 : 5] = y;
                    x += w;
                    if (h > shelf) shelf = h;
                }
                result[index] = at;
            }
            return new List<int[]>(result);
        }

        // The plate, ready to be stamped: its UV0 and material set. Null if it cannot be shown (merged into its group's mesh...).
        public PlateVisual PlateFor(PartScript part)
        {
            PlateVisual plate;
            if (Disposed || !Plates.TryGetValue(part, out plate) || plate.Failed) return null;
            LastUsed = Time.time;
            if (!plate.Applied && !plate.TryApply()) return null;
            return plate;
        }

        // A rectangle computed by the worker goes into the atlas. Returns the milliseconds it took.
        public double Deliver(Delivery d)
        {
            if (Disposed) return 0;
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();

            int bytesPerPixel = d.Alpha ? 1 : 4;
            int bucket = 16;
            while (bucket < Mathf.Max(d.W, d.H)) bucket *= 2;
            Texture2D staging = Staging(d.Alpha, bucket);
            byte[] padded = Padded(bucket, d.Alpha, bucket * bucket * bytesPerPixel);
            for (int row = 0; row < d.H; row++)
                Buffer.BlockCopy(d.Bytes, row * d.W * bytesPerPixel, padded, row * bucket * bytesPerPixel, d.W * bytesPerPixel);
            staging.SetPixelData(padded, 0);
            staging.Apply(false, false);
            Graphics.CopyTexture(staging, 0, 0, 0, 0, d.W, d.H, d.Alpha ? Alpha : Normal, 0, 0, d.X, d.Y);

            if (d.Alpha && d.Plate != null) d.Plate.EnsureLit();
            return watch.Elapsed.TotalMilliseconds;
        }

        private static readonly Dictionary<int, Texture2D> StagingTextures = new Dictionary<int, Texture2D>();
        private static readonly Dictionary<int, byte[]> PaddedBuffers = new Dictionary<int, byte[]>();

        private static Texture2D Staging(bool alpha, int bucket)
        {
            int key = bucket * 2 + (alpha ? 1 : 0);
            Texture2D texture;
            if (StagingTextures.TryGetValue(key, out texture) && texture != null) return texture;
            texture = alpha
                ? new Texture2D(bucket, bucket, TextureFormat.Alpha8, false)
                : new Texture2D(bucket, bucket, TextureFormat.RGBA32, false, true);
            texture.name = "ArmorFragments damage staging";
            StagingTextures[key] = texture;
            return texture;
        }

        // The staging texture takes a whole texture's worth of bytes, of exactly its size (one buffer per size, reused).
        private static byte[] Padded(int bucket, bool alpha, int length)
        {
            int key = bucket * 2 + (alpha ? 1 : 0);
            byte[] buffer;
            if (!PaddedBuffers.TryGetValue(key, out buffer) || buffer.Length != length)
            {
                buffer = new byte[length];
                PaddedBuffers[key] = buffer;
            }
            return buffer;
        }

        // Puts every plate back as it was and frees the atlases. (restore = false when the aircraft is already gone.)
        public void Dispose(bool restore)
        {
            if (Disposed) return;
            Disposed = true;
            foreach (PlateVisual plate in Plates.Values) plate.Release(restore);
            Plates.Clear();
            if (Clone != null)
            {
                if (restore && Theme != null) Theme.ReleaseDefaultPartMaterialInstance(Clone);
                else UnityEngine.Object.Destroy(Clone);
                Clone = null;
            }
            if (Normal != null) UnityEngine.Object.Destroy(Normal);
            if (Alpha != null) UnityEngine.Object.Destroy(Alpha);
            Normal = null;
            Alpha = null;
        }

        // The game's part material with the normal atlas (one for the whole vehicle).
        public Material PartClone()
        {
            if (Clone != null) return Clone;
            Material material = Theme.RequestDefaultPartMaterialInstance();
            if (material == null) return null;
            material.SetTexture("_BumpMap", Normal);
            material.SetFloat("_BumpScale", ArmorFragmentsPlugin.DamageMapsNormalStrength.Value);
            material.EnableKeyword("_NORMALMAP");
            Clone = material;
            return Clone;
        }

        // The atlas position of the flat sentinel (the middle of its corner), as a UV.
        public Vector2 SentinelUv()
        {
            float c = (SentinelSize * 0.5f) / AtlasSize;
            return new Vector2(c, c);
        }
    }

    // One plate of a vehicle: how it was before the maps touched it, and what it shows now.
    internal sealed class PlateVisual
    {
        public PlateInfo Info;
        public PlateCpu Cpu;
        public VehicleMaps Owner;
        public bool Applied;
        public bool Failed;
        public bool HasHole;
        public Material Lit;

        private Material[] originalMaterials;
        private bool originalExclude;
        private List<Vector2> originalUv0;
        private List<Vector4> originalTangents;
        private bool wroteMesh;

        // UV0 into the plate's tiles, tangents, and the vehicle's material.
        public bool TryApply()
        {
            PartMaterialScript.RendererMaterialMap map = Info.Map;
            Renderer renderer = map.Renderer;
            if (renderer == null || !renderer.enabled)
            {
                Failed = true;
                ArmorFragmentsPlugin.Log.LogWarning("[DMAP] plate '" + Info.Part.Part.Name +
                    "' is merged into its group's mesh: turn on 'Keep armor plates out of the mesh merge' and start the flight again");
                return false;
            }

            Material clone = Owner.PartClone();
            if (clone == null)
            {
                Failed = true;
                ArmorFragmentsPlugin.Log.LogWarning("[DMAP] no part material could be made for '" + Info.Part.Part.Name + "'");
                return false;
            }

            Mesh mesh = Info.Mesh;
            originalMaterials = map.OriginalMaterials;
            originalExclude = map.ExcludeFromMeshCombine;
            originalUv0 = new List<Vector2>();
            originalTangents = new List<Vector4>();
            mesh.GetUVs(0, originalUv0);
            mesh.GetTangents(originalTangents);

            WriteUv(mesh);
            mesh.RecalculateTangents();
            wroteMesh = true;

            SetMaterial(clone);
            map.ExcludeFromMeshCombine = true;
            Applied = true;
            return true;
        }

        private void WriteUv(Mesh mesh)
        {
            PlateGeometry geo = Info.Geometry;
            int count = mesh.vertexCount;
            List<Vector3> vertices = new List<Vector3>(count);
            List<Vector3> normals = new List<Vector3>(count);
            mesh.GetVertices(vertices);
            mesh.GetNormals(normals);
            bool hasNormals = normals.Count == count;

            Vector2 sentinel = Owner.SentinelUv();
            float atlas = Owner.AtlasSize;
            int innerW = Cpu.Width - 2 * PlateCpu.Border, innerH = Cpu.Height - 2 * PlateCpu.Border;

            List<Vector2> uvs = new List<Vector2>(count);
            for (int i = 0; i < count; i++)
            {
                Vector3 p = Info.ToPart.MultiplyPoint3x4(vertices[i]);
                float facing = hasNormals ? Vector3.Dot(Info.ToPart.MultiplyVector(normals[i]).normalized, geo.N) : 1f;
                if (Mathf.Abs(facing) < 0.5f) { uvs.Add(sentinel); continue; }

                int face = facing > 0f ? 0 : 1;
                Vector2 plane = geo.ToPlane(p);
                float u = Mathf.Clamp01(plane.x / geo.SizeU);
                float v = Mathf.Clamp01(plane.y / geo.SizeV);
                uvs.Add(new Vector2((Cpu.TileX[face] + PlateCpu.Border + u * innerW) / atlas,
                                    (Cpu.TileY[face] + PlateCpu.Border + v * innerH) / atlas));
            }
            mesh.SetUVs(0, uvs);
        }

        private void SetMaterial(Material material)
        {
            PartMaterialScript.RendererMaterialMap map = Info.Map;
            Material[] materials = new Material[map.OriginalMaterials.Length];
            for (int i = 0; i < materials.Length; i++) materials[i] = material;
            map.OriginalMaterials = materials;
            map.SetRendererMaterial(materials);
        }

        // The plate has a hole: from now on it is drawn with the Lit shader, which can cut it out.
        public void EnsureLit()
        {
            if (HasHole || !Applied || Owner.Disposed) return;
            HasHole = true;

            Shader lit = Shader.Find("Universal Render Pipeline/Lit");
            if (lit == null || !lit.isSupported)
            {
                ArmorFragmentsPlugin.Log.LogWarning("[DMAP] the URP Lit shader is not available: no see-through holes");
                return;
            }

            Color color = new Color(0.5f, 0.5f, 0.5f, 1f);
            float metallic = 0f, smoothness = 0.4f;
            try
            {
                PartMaterial paint = Owner.Theme.Theme.GetMaterial(Info.Part.Part.MaterialIds[0]);
                if (paint != null && paint.ColorData != null && paint.ColorData.Length > 0)
                {
                    color = paint.PrimaryColor;
                    color.a = 1f;
                    metallic = paint.PrimaryColorMetallic;
                    smoothness = paint.PrimaryColorSmoothness;
                }
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("[DMAP] the plate's paint could not be read: " + e.Message);
            }

            Material material = new Material(lit);
            material.name = "ArmorFragments plate (Lit)";
            material.SetTexture("_BaseMap", Owner.Alpha);
            material.SetColor("_BaseColor", color);
            material.SetFloat("_Metallic", metallic);
            material.SetFloat("_Smoothness", smoothness);
            material.SetTexture("_BumpMap", Owner.Normal);
            material.SetFloat("_BumpScale", ArmorFragmentsPlugin.DamageMapsNormalStrength.Value);
            material.EnableKeyword("_NORMALMAP");
            material.SetFloat("_AlphaClip", 1f);
            material.SetFloat("_Cutoff", 0.5f);
            material.EnableKeyword("_ALPHATEST_ON");
            material.SetFloat("_Surface", 0f);
            material.SetFloat("_Cull", ArmorFragmentsPlugin.DamageMapsHoleBothSides.Value ? 0f : 2f);
            material.SetOverrideTag("RenderType", "TransparentCutout");
            material.renderQueue = (int)RenderQueue.AlphaTest;
            Lit = material;
            SetMaterial(material);
        }

        // Back to the game's own look (restore) and free what was made.
        public void Release(bool restore)
        {
            try
            {
                if (restore && Applied)
                {
                    Info.Map.OriginalMaterials = originalMaterials;
                    Info.Map.ExcludeFromMeshCombine = originalExclude;
                    if (Info.Map.Renderer != null) Info.Map.SetRendererMaterial(originalMaterials);
                    if (wroteMesh && Info.Mesh != null)
                    {
                        Info.Mesh.SetUVs(0, originalUv0);
                        Info.Mesh.SetTangents(originalTangents);
                    }
                }
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("[DMAP] a plate could not be restored: " + e.Message);
            }
            if (Lit != null) UnityEngine.Object.Destroy(Lit);
            Lit = null;
            Applied = false;
        }
    }

    // The worker thread: stamps the impacts onto the plates' CPU maps and works out the rectangles to deliver.
    // It touches no Unity object (only plain arrays), so it cannot hitch the game; the main thread takes what it makes.
    internal static class DamageWorker
    {
        public sealed class Job
        {
            public VehicleMaps Owner;
            public PlateVisual Plate;
            public PlateImpact[] Impacts;
            public float DeliverAt;
        }

        private static readonly BlockingCollection<Job> Jobs = new BlockingCollection<Job>(new ConcurrentQueue<Job>());
        public static readonly ConcurrentQueue<Delivery> Done = new ConcurrentQueue<Delivery>();
        public static readonly ConcurrentQueue<string> Messages = new ConcurrentQueue<string>();
        private static Thread thread;
        public static volatile int LastMs10;   // the last job, in tenths of a millisecond

        public static int Pending
        {
            get { return Jobs.Count; }
        }

        public static void Enqueue(Job job)
        {
            if (thread == null)
            {
                thread = new Thread(Run) { IsBackground = true, Name = "ArmorFragments damage maps", Priority = System.Threading.ThreadPriority.BelowNormal };
                thread.Start();
            }
            Jobs.Add(job);
        }

        private static void Run()
        {
            foreach (Job job in Jobs.GetConsumingEnumerable())
            {
                try
                {
                    System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
                    foreach (PlateImpact impact in job.Impacts) PlateStamps.Apply(job.Plate.Cpu, impact);

                    List<Delivery> output = new List<Delivery>();
                    PlateStamps.Flush(job.Plate.Cpu, job.Plate, job.Owner, job.DeliverAt, output);
                    foreach (Delivery d in output) Done.Enqueue(d);
                    LastMs10 = (int)(watch.Elapsed.TotalMilliseconds * 10.0);
                }
                catch (Exception e)
                {
                    Messages.Enqueue("[DMAP] the worker failed on a stamp: " + e);
                }
            }
        }
    }
}
