using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using ArmorFragments.Sim;
using Assets.Scripts.Craft;
using Assets.Scripts.Craft.Parts;
using UnityEngine;
using UnityEngine.Rendering;

namespace ArmorFragments
{
    // The damage maps of one vehicle: ONE atlas of normal maps and ONE atlas of hole masks (made when the first hole
    // appears), with a tile (two: front and back) for each of its armor plates. A plate that is damaged gets its UV0 pointing at its tiles, and a material:
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

        // The hole atlas: white everywhere (the color comes from the material) with the mask in its alpha, 255 = solid.
        // It is RGBA, not Alpha8: an Alpha8 texture reads as black (0,0,0,a) on Direct3D, which painted the plate black.
        // Made when the first hole shows up, so vehicles that never get one do not pay for it.
        public void EnsureAlpha()
        {
            if (Alpha != null) return;
            Alpha = new Texture2D(AtlasSize, AtlasSize, TextureFormat.RGBA32, false, false) { wrapMode = TextureWrapMode.Clamp, name = "ArmorFragments damage holes" };
            byte[] solid = new byte[AtlasSize * AtlasSize * 4];
            FillPattern(solid, 255, 255, 255, 255);   // no holes
            Alpha.SetPixelData(solid, 0);
            Alpha.Apply(false, false);
        }

        // A rectangle computed by the worker goes into the atlas. Returns the milliseconds it took.
        public double Deliver(Delivery d)
        {
            if (Disposed) return 0;
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();

            if (d.Alpha) EnsureAlpha();
            const int bytesPerPixel = 4;
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
            // Same format as the atlas it feeds (CopyTexture needs it): normals are linear, the hole mask is not.
            texture = new Texture2D(bucket, bucket, TextureFormat.RGBA32, false, !alpha);
            texture.name = "ArmorFragments damage staging";
            StagingTextures[key] = texture;
            return texture;
        }

        // The plugin is being unloaded: the staging textures are shared by every vehicle, so they are freed here, not in Dispose.
        public static void ReleaseStaging()
        {
            foreach (Texture2D texture in StagingTextures.Values)
                if (texture != null) UnityEngine.Object.Destroy(texture);
            StagingTextures.Clear();
            PaddedBuffers.Clear();
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
            Owner.EnsureAlpha();

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

    // The worker thread: plays the impacts onto the plates' CPU maps and works out the rectangles to deliver. A shell's impact
    // is simulated (ImpactSim, on every thread that is free) when the settings say so; everything else, and any simulation that
    // is too slow or disagrees with the energy model, gets the quick analytic marks (PlateStamps). It touches no Unity object
    // (only plain arrays), so it cannot hitch the game; the main thread takes what it makes.
    internal static class DamageWorker
    {
        public sealed class Job
        {
            public VehicleMaps Owner;
            public PlateVisual Plate;
            public PlateImpact[] Impacts;
            public float DeliverAt;

            // The simulation's settings, taken when the impact happened (the worker never reads the settings).
            public int SimQuality = -1;        // 0 low, 1 medium, 2 high; -1 = no simulation (the quick marks)
            public bool AutoQuality;
            public int TimeLimitMs;
            public float ResistanceJPerM3;
            public int Threads;                // 0 = automatic
        }

        private static readonly BlockingCollection<Job> Jobs = new BlockingCollection<Job>(new ConcurrentQueue<Job>());
        public static readonly ConcurrentQueue<Delivery> Done = new ConcurrentQueue<Delivery>();
        public static readonly ConcurrentQueue<string> Messages = new ConcurrentQueue<string>();   // warnings, for the main thread to log
        public static readonly ConcurrentQueue<string> Infos = new ConcurrentQueue<string>();      // information, the same
        private static Thread thread;
        public static volatile int LastMs10;   // the last job, in tenths of a millisecond

        // The simulation's numbers, for the panel.
        private static int pendingSims;
        public static int PendingSims { get { return Volatile.Read(ref pendingSims); } }
        public static volatile int LastSimMs;
        public static volatile int LastSimQuality = -1;
        public static volatile int LastSimThreads;
        public static volatile int SimsDone;
        public static volatile int Fallbacks;
        public static volatile int TunedQuality = 2;   // the best quality the time limit lets the simulation use now

        private static SimPool pool;
        private static int poolWorkers = -1;
        private static volatile int allowedWorkers = int.MaxValue;
        private static int goodRuns;

        // Set by Shutdown (the plugin is being unloaded): no new job is taken, a simulation that is running stops at its next step.
        private static volatile bool stopping;
        private static readonly object PoolGate = new object();

        public static int Pending
        {
            get { return Jobs.Count; }
        }

        public static int WorkersNow
        {
            get { SimPool p = pool; return p == null ? 0 : Math.Min(p.Workers, p.MaxWorkers) + 1; }
        }

        // The game's frame rate dropped: the simulation lends some threads back (or takes them again).
        public static void SetAllowedWorkers(int workers)
        {
            allowedWorkers = Math.Max(0, workers);
            SimPool p = pool;
            if (p != null) p.MaxWorkers = Math.Min(p.Workers, allowedWorkers);
        }

        public static int ConfiguredWorkers(int threads)
        {
            int total = threads > 0 ? threads : Math.Max(1, Environment.ProcessorCount - 2);   // automatic: all but two cores
            return Math.Max(0, total - 1);                                                     // the worker thread itself works too
        }

        public static void Enqueue(Job job)
        {
            if (stopping) return;   // unloading: nobody will take it
            if (thread == null)
            {
                thread = new Thread(Run) { IsBackground = true, Name = "ArmorFragments damage maps", Priority = System.Threading.ThreadPriority.BelowNormal };
                thread.Start();
            }
            if (job.SimQuality >= 0) Interlocked.Increment(ref pendingSims);
            try
            {
                Jobs.Add(job);
            }
            catch (InvalidOperationException)
            {
                // Shutdown closed the queue between the check above and here.
                if (job.SimQuality >= 0) Interlocked.Decrement(ref pendingSims);
            }
        }

        // The plugin is being unloaded: stop the worker thread and the simulation's threads without waiting for them (a simulation can
        // take seconds, and this is called from the game's main thread). The queue is closed (no new job; the thread ends when it has
        // looked at what is left, which it now skips), a simulation in progress stops at its next step, and the pool's threads leave.
        // Nothing here touches a Unity object.
        public static void Shutdown()
        {
            stopping = true;
            try { Jobs.CompleteAdding(); }
            catch (ObjectDisposedException) { }

            lock (PoolGate)
            {
                if (pool != null) pool.Dispose();
                pool = null;
                poolWorkers = -1;
            }

            Delivery done;
            while (Done.TryDequeue(out done)) { }
            string text;
            while (Messages.TryDequeue(out text)) { }
            while (Infos.TryDequeue(out text)) { }
        }

        private static void Run()
        {
            foreach (Job job in Jobs.GetConsumingEnumerable())
            {
                if (stopping)
                {
                    if (job.SimQuality >= 0) Interlocked.Decrement(ref pendingSims);
                    continue;   // drain what is left without working on it
                }

                try
                {
                    System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
                    foreach (PlateImpact impact in job.Impacts)
                    {
                        if (stopping) break;
                        if (job.SimQuality >= 0 && SimBridge.Wants(impact) && TrySimulate(job, impact)) continue;
                        if (stopping) break;   // the simulation was cut short by Shutdown
                        PlateStamps.Apply(job.Plate.Cpu, impact);
                    }
                    if (stopping) continue;    // nobody is left to take what this job makes

                    List<Delivery> output = new List<Delivery>();
                    PlateStamps.Flush(job.Plate.Cpu, job.Plate, job.Owner, job.DeliverAt, output);
                    foreach (Delivery d in output) Done.Enqueue(d);
                    LastMs10 = (int)(watch.Elapsed.TotalMilliseconds * 10.0);
                }
                catch (Exception e)
                {
                    Messages.Enqueue("[DMAP] the worker failed on an impact: " + e);
                }
                finally
                {
                    if (job.SimQuality >= 0) Interlocked.Decrement(ref pendingSims);
                }
            }
        }

        // Plays one shell impact with the simulation and lays the result into the plate. False = the quick marks must be used.
        private static bool TrySimulate(Job job, PlateImpact impact)
        {
            try
            {
                int quality = job.AutoQuality ? Math.Min(job.SimQuality, TunedQuality) : job.SimQuality;
                SimPool simPool = EnsurePool(job.Threads);
                ImpactSimInput input = SimBridge.InputFor(impact, quality, job.TimeLimitMs, job.ResistanceJPerM3);
                input.Cancelled = StopRequested;

                // The quality is the finest grid the simulation may use. A thick plate or a big shell on a fine grid would run past the
                // time limit (and give nothing), so the grid is the finest one whose predicted time fits the budget.
                string grid;
                input.Columns = ChooseColumns(impact, input, quality, out grid);
                ImpactSimResult result = ImpactSim.Run(input, simPool);
                if (stopping) return false;   // cut short by Shutdown: not a slow run, nothing to tune or report
                LearnCost(impact, input, result);

                LastSimMs = (int)result.Ms;
                LastSimQuality = quality;
                LastSimThreads = Math.Min(simPool.Workers, simPool.MaxWorkers) + 1;
                Tune(job, result);

                string what = "[SIMFX] " + impact.Kind + ", quality " + new[] { "low", "medium", "high" }[quality] + grid + ", " + result.Particles +
                              " particles, " + result.Steps + " steps, " + result.Ms.ToString("F0") + " ms on " + LastSimThreads + " thread(s)";
                if (result.TimedOut)
                {
                    Fallbacks++;
                    Infos.Enqueue(what + ": too slow (limit " + job.TimeLimitMs + " ms), the quick marks are used");
                    return false;
                }
                if (!SimBridge.Agrees(impact.Kind, result))
                {
                    Fallbacks++;
                    Infos.Enqueue(what + ": it disagrees with the energy model (" + (result.Perforated ? "it got through" : "it did not") +
                                  "), the quick marks are used");
                    return false;
                }

                SimBridge.Apply(job.Plate.Cpu, impact, result);
                SimsDone++;
                Infos.Enqueue(what + ", dent " + (result.DepthM * 1000f).ToString("F0") + " mm" +
                              (result.HoleRadiusM > 0f ? ", hole " + (result.HoleRadiusM * 2000f).ToString("F0") + " mm" : ""));
                return true;
            }
            catch (Exception e)
            {
                Fallbacks++;
                Messages.Enqueue("[SIMFX] the simulation failed, the quick marks are used: " + e.Message);
                return false;
            }
        }

        // ---- Choosing the grid ---------------------------------------------------------------------
        //
        // A run costs about  c x columns^3 x layers  milliseconds: the lattice has columns^2 x layers particles and the shell takes
        // a number of steps that grows with the columns (a step is a fixed fraction of a cell). 'c' depends on the machine and on the
        // outcome (a shell that goes through takes more steps than one that is stopped), so it is learned from the runs themselves,
        // one value per kind of impact. The first guesses come from the first real runs in the game (Mono, 14 threads).
        private const double BudgetShare = 0.45;                // of the time limit: a mark that comes later than this is too late to matter
        private static readonly int[] Ladder = { 96, 80, 64, 56, 48, 40, 32 };
        private static readonly double[] CostPerUnit = { 2.0e-3, 1.4e-3, 2.1e-3 };   // ms per (columns^3 x layers): ricochet, blocked, penetrated

        private static int CostClass(DamageKind kind)
        {
            return kind == DamageKind.Penetrated ? 2 : kind == DamageKind.Blocked ? 1 : 0;
        }

        private static double Units(ImpactSimInput input, int columns)
        {
            return (double)columns * columns * columns * ImpactSim.LayersFor(input.CaliberM, input.ThicknessM, columns);
        }

        // The finest grid (not above the quality's own) whose predicted time fits the budget; the coarsest one if none does.
        private static int ChooseColumns(PlateImpact impact, ImpactSimInput input, int quality, out string note)
        {
            int wanted = ImpactSim.ColumnsOfQuality(quality);
            note = "";
            if (input.TimeLimitMs <= 0) return wanted;   // no limit: nothing to fit

            double budget = input.TimeLimitMs * BudgetShare;
            double cost = CostPerUnit[CostClass(impact.Kind)];
            int chosen = Ladder[Ladder.Length - 1];
            foreach (int columns in Ladder)
            {
                if (columns > wanted) continue;
                chosen = columns;
                if (Units(input, columns) * cost <= budget) break;
            }
            if (chosen < wanted)
                note = " (a " + chosen + "x" + chosen + " grid: " + wanted + "x" + wanted + " would take about " +
                       (Units(input, wanted) * cost / 1000.0).ToString("F1") + " s)";
            return chosen;
        }

        // What the run really cost, per unit. A run that was stopped by the time limit only says it cost AT LEAST this much.
        private static void LearnCost(PlateImpact impact, ImpactSimInput input, ImpactSimResult result)
        {
            int index = CostClass(impact.Kind);
            double observed = result.Ms / Math.Max(Units(input, input.Columns), 1.0);
            double known = CostPerUnit[index];
            CostPerUnit[index] = result.TimedOut ? Math.Max(known, observed * 1.2) : Math.Max(0.5 * known + 0.5 * observed, 2e-4);
        }

        // The simulation gives itself the best quality the time limit allows: a run that took most of the limit lowers it,
        // a few quick ones raise it again (up to the setting).
        private static void Tune(Job job, ImpactSimResult result)
        {
            if (!job.AutoQuality || job.TimeLimitMs <= 0) return;
            if (result.TimedOut || result.Ms > 0.7 * job.TimeLimitMs)
            {
                if (TunedQuality > 0)
                {
                    TunedQuality--;
                    Infos.Enqueue("[SIMFX] it was slow: the best quality it will use for now is " + new[] { "low", "medium", "high" }[TunedQuality]);
                }
                goodRuns = 0;
            }
            else if (result.Ms < 0.2 * job.TimeLimitMs)
            {
                if (++goodRuns >= 3 && TunedQuality < 2) { TunedQuality++; goodRuns = 0; }
            }
            else goodRuns = 0;
        }

        private static bool StopRequested()
        {
            return stopping;
        }

        private static SimPool EnsurePool(int threads)
        {
            int workers = ConfiguredWorkers(threads);
            lock (PoolGate)
            {
                if (pool == null || poolWorkers != workers)
                {
                    if (pool != null) pool.Dispose();
                    pool = new SimPool(workers);
                    poolWorkers = workers;
                    Infos.Enqueue("[SIMFX] simulation threads: " + (workers + 1) + " (" + Environment.ProcessorCount + " cores)");
                }
                pool.MaxWorkers = Math.Min(workers, allowedWorkers);
                // Shutdown ran while this job was starting: the pool just made must not outlive it.
                if (stopping) pool.Dispose();
                return pool;
            }
        }
    }
}
