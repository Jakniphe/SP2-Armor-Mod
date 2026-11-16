using System;
using System.Collections.Generic;
using Assets.Scripts.Craft;
using Assets.Scripts.Craft.Parts;
using Assets.Scripts.Flight.Cameras;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ArmorFragments
{
    // What the damage maps know about one plate's mesh.
    internal sealed class PlateInfo
    {
        public PartScript Part;
        public PlateGeometry Geometry;
        public PartMaterialScript.RendererMaterialMap Map;
        public Mesh Mesh;
        public Matrix4x4 ToPart;    // the mesh's vertices to the part's local frame
        public float ThicknessM;    // the plate's thickness from its tag, meters
    }

    // DAMAGE MAPS. Every shot that resolves on an armor plate leaves a mark on it (a dent, a hole, a groove, pits), and
    // the mark counts: a hole lets shells and fragments through, a dent makes the plate thinner there.
    //
    // Two layers that never wait for each other:
    //  1. The MODEL (here, instant): a short list of holes and dents per plate, in plate coordinates. The energy model
    //     asks it ("is this point a hole?", "how thin is the plate here?") and nothing else decides the game.
    //  2. The LOOK (VehicleMaps, DamageWorker): the same impacts, drawn into the plate's normal and hole maps by a worker
    //     thread, and shown a small rectangle at a time when they are ready (and the shell's ray has arrived).
    internal static class PlateDamageMaps
    {
        private const string Tag = "[DMAP] ";
        private const int MaxRecords = 64;      // holes and dents kept per plate (the oldest go)
        private const int MaxPitsPerCloud = 96; // fragment marks drawn per plate for one burst

        private struct Hole { public bool Front; public float U, V, R; }
        private struct Dent { public float U, V, R, D; }

        private sealed class PlateState
        {
            public PlateInfo Info;
            public readonly List<Hole> Holes = new List<Hole>();
            public readonly List<Dent> Dents = new List<Dent>();
        }

        private static readonly Dictionary<PartScript, PlateInfo> Infos = new Dictionary<PartScript, PlateInfo>();
        private static readonly HashSet<PartScript> NoInfo = new HashSet<PartScript>();
        private static readonly Dictionary<PartScript, PlateState> States = new Dictionary<PartScript, PlateState>();
        private static readonly Dictionary<AircraftScript, VehicleMaps> Vehicles = new Dictionary<AircraftScript, VehicleMaps>();
        private static readonly HashSet<AircraftScript> Refused = new HashSet<AircraftScript>();
        private static readonly List<Delivery> Holding = new List<Delivery>();
        private static int counter;
        private static int frame;
        private static double lastDeliverMs;

        public static bool Visuals
        {
            get { return ArmorFragmentsPlugin.DamageMapsEnabled.Value; }
        }

        // A mark goes on the two big faces of a plate (the map has a tile for each): a hit whose surface normal is
        // less aligned than this with the plate's thickness axis (the side of a block, an edge) leaves no mark.
        private const float BigFaceCos = 0.5f;

        public static bool Weakens
        {
            get { return ArmorFragmentsPlugin.DamageMapsWeakenArmor.Value; }
        }

        // What the panel shows.
        public static string Message = "";

        public static string SimStatus
        {
            get
            {
                if (ArmorFragmentsPlugin.SimQuality.Value == 0) return "off (the quick marks)";
                string last = DamageWorker.LastSimQuality < 0
                    ? "no simulation yet"
                    : "last " + DamageWorker.LastSimMs + " ms, " + ArmorFragments.Sim.ImpactSim.QualityName(DamageWorker.LastSimQuality) +
                      ", " + (DamageWorker.LastSimGpu ? "graphics card" : DamageWorker.LastSimThreads + " thread(s)");
                return last + "; " + DamageWorker.SimsDone + " done, " + DamageWorker.Fallbacks + " fell back, waiting " +
                       DamageWorker.PendingSims + ", best quality now " + ArmorFragments.Sim.ImpactSim.QualityName(DamageWorker.TunedQuality) +
                       ", threads in use up to " + DamageWorker.WorkersNow;
            }
        }

        public static string Status
        {
            get
            {
                int plates = 0;
                foreach (VehicleMaps v in Vehicles.Values) plates += v.Plates.Count;
                return Vehicles.Count + " vehicle(s), " + plates + " plate(s), " + States.Count + " damaged, queue " +
                       (DamageWorker.Pending + Holding.Count) + ", last job " + (DamageWorker.LastMs10 / 10f).ToString("F1") +
                       " ms (worker), " + lastDeliverMs.ToString("F2") + " ms (main thread)";
            }
        }

        // ---- The plate's mesh -------------------------------------------------------------------

        public static PlateInfo InfoOf(PartScript part)
        {
            PlateInfo info;
            if (Infos.TryGetValue(part, out info)) return info;
            if (NoInfo.Contains(part)) return null;

            info = Build(part);
            if (info == null) NoInfo.Add(part);
            else Infos[part] = info;
            return info;
        }

        private static PlateInfo Build(PartScript part)
        {
            PartMaterialScript materials = part.PartMaterialScript;
            if (materials == null) return null;

            foreach (PartMaterialScript.RendererMaterialMap map in materials.RendererMaps)
            {
                Renderer renderer = map.Renderer;
                if (renderer == null || !map.MeshIsUnique) continue;   // a shared mesh is never touched
                Mesh mesh = map.Mesh;
                if (mesh == null || !mesh.isReadable || mesh.vertexCount < 3) continue;

                Matrix4x4 toPart = part.transform.worldToLocalMatrix * renderer.localToWorldMatrix;
                PlateGeometry geometry = PlateGeometry.Build(mesh, toPart);
                if (geometry == null) continue;

                ArmorTag armor = TagCodec.Parse(part.Part.Name).Armor;
                if (geometry.Curved)
                    ArmorFragmentsPlugin.Log.LogInfo(Tag + "plate '" + part.Part.Name + "' is not a slab (depth " + geometry.Depth.ToString("F2") +
                        " m of " + geometry.SizeU.ToString("F2") + " x " + geometry.SizeV.ToString("F2") + "): its maps are flat approximations");
                return new PlateInfo
                {
                    Part = part, Geometry = geometry, Map = map, Mesh = mesh, ToPart = toPart,
                    ThicknessM = (armor != null ? armor.Thickness : 20f) * 0.001f,
                };
            }
            return null;
        }

        // ---- What the model asks ------------------------------------------------------------------

        // Is this point of the plate a hole? 'worldNormal' is the surface normal on the side that was hit.
        public static bool InHole(PartScript part, Vector3 worldPoint, Vector3 worldNormal)
        {
            if (States.Count == 0 || !Weakens) return false;
            PlateState state;
            if (!States.TryGetValue(part, out state) || state.Holes.Count == 0) return false;

            PlateGeometry g = state.Info.Geometry;
            float facing = Vector3.Dot(g.N, part.transform.InverseTransformDirection(worldNormal).normalized);
            if (Mathf.Abs(facing) < BigFaceCos) return false;   // an edge or a side of a block: no map there
            bool front = facing > 0f;
            Vector2 at = g.ToPlane(part.transform.InverseTransformPoint(worldPoint));
            foreach (Hole hole in state.Holes)
            {
                if (hole.Front != front) continue;
                float du = at.x - hole.U, dv = at.y - hole.V;
                if (du * du + dv * dv < hole.R * hole.R) return true;
            }
            return false;
        }

        // The plate as it is at this point: thinner where it is dented. Returns the same tag if nothing changed.
        public static ArmorTag Weakened(PartScript part, ArmorTag tag, Vector3 worldPoint)
        {
            float factor = ThicknessFactor(part, tag.Thickness, worldPoint);
            if (factor > 0.999f) return tag;
            return new ArmorTag { Thickness = tag.Thickness * factor, Hardness = tag.Hardness, Brittleness = tag.Brittleness };
        }

        // The same for a plain thickness (a face of a per-face block): the thickness left, in mm.
        public static float WeakenedMm(PartScript part, float thicknessMm, Vector3 worldPoint)
        {
            return thicknessMm * ThicknessFactor(part, thicknessMm, worldPoint);
        }

        // 1 = untouched, down to 0.4 where the plate is dented.
        private static float ThicknessFactor(PartScript part, float thicknessMm, Vector3 worldPoint)
        {
            if (States.Count == 0 || !Weakens) return 1f;
            PlateState state;
            if (!States.TryGetValue(part, out state) || state.Dents.Count == 0) return 1f;

            PlateGeometry g = state.Info.Geometry;
            Vector2 at = g.ToPlane(part.transform.InverseTransformPoint(worldPoint));
            float thickness = Mathf.Max(thicknessMm * 0.001f, 0.001f);
            float lost = 0f;
            foreach (Dent dent in state.Dents)
            {
                float du = at.x - dent.U, dv = at.y - dent.V;
                float rho = Mathf.Sqrt(du * du + dv * dv) / dent.R;
                if (rho >= 1f) continue;
                float bowl = 1f - rho * rho;
                lost += dent.D * bowl * bowl;
            }
            return Mathf.Clamp(1f - lost / thickness, 0.4f, 1f);
        }

        // ---- What the model tells ------------------------------------------------------------------

        // A shot has been resolved on 'part'.
        //   direction: the way the shell was going (for a ricochet: the way it bounced off)
        //   strength:  0..1, how hard it was compared with what the plate could take
        //   delay:     seconds until the shell's ray arrives (the mark appears then)
        //   shellMass, speed, keptEnergy: the shell (at the plate), for the simulation; without them the quick marks are used
        //   visualOnly: a simulated shot's mark: drawn, but not recorded in the armor model (no hole to shoot through, no thinner plate)
        public static void OnImpact(PartScript part, ArmorTag armor, DamageKind kind, Vector3 point, Vector3 normalFacing,
                                    Vector3 direction, float caliberMeters, float strength, float angle, float delay,
                                    float shellMass = 0f, float speed = 0f, float keptEnergy = 1f, bool visualOnly = false, float deflection = 0f)
        {
            if (visualOnly ? !Visuals : !Visuals && !Weakens) return;
            if (part == null || part.Aircraft == null || part.Aircraft.LoadContext != CraftLoadContext.Flight) return;

            PlateInfo info = InfoOf(part);
            if (info == null)
            {
                NoMark(part, "its mesh cannot be marked (it is shared, unreadable or not a single mesh of its own)");
                return;
            }

            PlateGeometry g = info.Geometry;
            Vector3 normalLocal = part.transform.InverseTransformDirection(normalFacing).normalized;
            float facing = Mathf.Abs(Vector3.Dot(g.N, normalLocal));
            if (facing < BigFaceCos)   // an edge or a side of a block: it has no map
            {
                NoMark(part, "the shell hit a side or an edge of the block, which has no map (the hit is " +
                             (Mathf.Acos(Mathf.Clamp01(facing)) * Mathf.Rad2Deg).ToString("F0") + " deg off the block's thin axis; marks go on its two big faces)");
                return;
            }
            Vector3 directionLocal = part.transform.InverseTransformDirection(direction).normalized;
            Vector2 at = g.ToPlane(part.transform.InverseTransformPoint(point));
            Vector2 along = g.DirectionToPlane(directionLocal);

            float thickness = armor != null ? armor.Thickness * 0.001f : info.ThicknessM;
            PlateImpact impact = new PlateImpact
            {
                Kind = kind,
                Front = Vector3.Dot(g.N, normalLocal) > 0f,
                U = at.x, V = at.y,
                DirU = along.x, DirV = along.y,
                Angle = angle,
                Caliber = caliberMeters,
                Strength = strength,
                Thickness = thickness,
                Seed = unchecked(part.Part.Id * 7919 + (++counter) * 104729),
                ShellMass = shellMass,
                Speed = speed,
                PlateHardness = armor != null ? armor.Hardness : 100f,
                PlateBrittleness = armor != null ? armor.Brittleness / 100f : 0.5f,
                KeptEnergy = keptEnergy,
                Deflection = deflection,
            };

            if (Weakens && !visualOnly) Record(part, info, impact);   // a simulated shot only draws: it never changes the armor model
            if (Visuals) Show(part, new[] { impact }, delay);
        }

        // Why a shell left no mark on a plate, told once per plate and reason (so the log does not stay silent).
        private static readonly HashSet<string> NoMarkTold = new HashSet<string>();

        private static void NoMark(PartScript part, string reason)
        {
            if (NoMarkTold.Add(part.GetInstanceID() + reason))
                ArmorFragmentsPlugin.Log.LogInfo(Tag + "no mark on '" + part.Part.Name + "': " + reason);
        }

        // A fragment stopped in a plate (a pit) or went through it (a small hole). Gathered until the next frame.
        private sealed class PitBuffer
        {
            public readonly List<PlateImpact> Impacts = new List<PlateImpact>();
            public float Delay = float.MaxValue;
        }

        private static readonly Dictionary<PartScript, PitBuffer> Pits = new Dictionary<PartScript, PitBuffer>();

        public static void OnFragment(PartScript part, bool passed, Vector3 point, Vector3 normalFacing, Vector3 direction,
                                      float diameterMeters, float strength, float delay)
        {
            if (!Visuals || part == null || part.Aircraft == null || part.Aircraft.LoadContext != CraftLoadContext.Flight) return;

            PlateInfo info = InfoOf(part);
            if (info == null) return;

            PitBuffer buffer;
            if (!Pits.TryGetValue(part, out buffer)) Pits[part] = buffer = new PitBuffer();
            if (buffer.Impacts.Count >= MaxPitsPerCloud) return;

            PlateGeometry g = info.Geometry;
            Vector3 normalLocal = part.transform.InverseTransformDirection(normalFacing).normalized;
            if (Mathf.Abs(Vector3.Dot(g.N, normalLocal)) < BigFaceCos) return;   // an edge or a side of a block: it has no map
            Vector2 at = g.ToPlane(part.transform.InverseTransformPoint(point));
            buffer.Delay = Mathf.Min(buffer.Delay, delay);
            buffer.Impacts.Add(new PlateImpact
            {
                Kind = passed ? DamageKind.SmallHole : DamageKind.Pit,
                Front = Vector3.Dot(g.N, normalLocal) > 0f,
                U = at.x, V = at.y,
                Caliber = diameterMeters,
                Strength = strength,
                Thickness = info.ThicknessM,
                Seed = unchecked(part.Part.Id * 7919 + (++counter) * 104729),
            });
        }

        // The model's record of the impact: holes let things through, dents make the plate thinner there.
        private static void Record(PartScript part, PlateInfo info, PlateImpact impact)
        {
            PlateState state;
            if (!States.TryGetValue(part, out state)) States[part] = state = new PlateState { Info = info };

            float caliber = Mathf.Max(impact.Caliber, 0.002f);
            switch (impact.Kind)
            {
                case DamageKind.Blocked:
                    AddDent(state, impact.U, impact.V, caliber * (0.85f + 0.5f * impact.Strength), caliber * (0.08f + 0.32f * impact.Strength));
                    break;

                case DamageKind.Penetrated:
                {
                    AddDent(state, impact.U, impact.V, caliber * 0.85f, caliber * 0.10f);
                    AddHole(state, impact.Front, impact.U, impact.V, caliber * 0.5f);

                    Vector2 dir = new Vector2(impact.DirU, impact.DirV);
                    dir = dir.sqrMagnitude < 0.0001f ? new Vector2(1f, 0f) : dir.normalized;
                    float lateral = Mathf.Min(impact.Thickness * Mathf.Tan(Mathf.Clamp(impact.Angle, 0f, 85f) * Mathf.Deg2Rad), 0.6f);
                    AddHole(state, !impact.Front, impact.U + dir.x * lateral, impact.V + dir.y * lateral, caliber * 0.5f * 1.15f);
                    break;
                }
            }
        }

        private static void AddDent(PlateState state, float u, float v, float radius, float depth)
        {
            if (state.Dents.Count >= MaxRecords) state.Dents.RemoveAt(0);
            state.Dents.Add(new Dent { U = u, V = v, R = radius, D = depth });
        }

        private static void AddHole(PlateState state, bool front, float u, float v, float radius)
        {
            if (state.Holes.Count >= MaxRecords) state.Holes.RemoveAt(0);
            state.Holes.Add(new Hole { Front = front, U = u, V = v, R = radius });
        }

        // The look: hand the impacts to the worker thread, if the plate can be shown.
        private static void Show(PartScript part, PlateImpact[] impacts, float delay)
        {
            VehicleMaps maps = VehicleFor(part.Aircraft);
            if (maps == null) return;
            PlateVisual plate = maps.PlateFor(part);
            if (plate == null) return;

            DamageWorker.Job job = new DamageWorker.Job
            {
                Owner = maps, Plate = plate, Impacts = impacts, DeliverAt = Time.time + Mathf.Max(delay, 0f),
                Height = ArmorFragmentsPlugin.DamageMapsRelief.Value != 0,
                Damage = ArmorFragmentsPlugin.ImpactCameraDamageView.Value,
            };
            WantGeometry(job, plate);

            // A shell's impact is simulated if the setting says so and the simulations are not piling up; the settings are taken
            // here, on the main thread, and travel with the job.
            int quality = Math.Min(ArmorFragmentsPlugin.SimQuality.Value - 1, ArmorFragments.Sim.ImpactSim.MaxQuality);
            if (quality >= 0 && impacts.Length == 1 && SimBridge.Wants(impacts[0]))
            {
                if (DamageWorker.PendingSims < QueueLimit())
                {
                    job.SimQuality = quality;
                    job.AutoQuality = ArmorFragmentsPlugin.SimAutoQuality.Value;
                    job.TimeLimitMs = Mathf.RoundToInt(ArmorFragmentsPlugin.SimTimeLimit.Value * 1000f);
                    job.ResistanceJPerM3 = ArmorFragmentsPlugin.ArmorResistance.Value * 1e7f;   // J per mm per cm2 -> J per m3
                    job.Threads = ArmorFragmentsPlugin.SimThreads.Value;
                    job.UseGpu = ArmorFragmentsPlugin.UseGpu.Value;
                    job.SimCracks = ArmorFragmentsPlugin.SimCracks.Value;
                    job.SimSmooth = ArmorFragmentsPlugin.SimSmoothEdges.Value;
                }
                else
                {
                    ArmorFragmentsPlugin.Log.LogInfo("[SIMFX] " + DamageWorker.PendingSims + " simulations are waiting (the limit is " + QueueLimit() +
                                                     "): this impact gets the quick marks");
                }
            }
            DamageWorker.Enqueue(job);
        }

        private static VehicleMaps VehicleFor(AircraftScript aircraft)
        {
            VehicleMaps maps;
            if (Vehicles.TryGetValue(aircraft, out maps)) return maps;
            if (Refused.Contains(aircraft)) return null;

            // Not too many at once: the vehicle that has been quiet longest gives its atlas back.
            int most = Mathf.Max(ArmorFragmentsPlugin.DamageMapsMaxVehicles.Value, 1);
            while (Vehicles.Count >= most)
            {
                AircraftScript oldest = null;
                float oldestTime = float.MaxValue;
                foreach (KeyValuePair<AircraftScript, VehicleMaps> pair in Vehicles)
                {
                    if (pair.Value.LastUsed < oldestTime) { oldestTime = pair.Value.LastUsed; oldest = pair.Key; }
                }
                if (oldest == null) break;
                ArmorFragmentsPlugin.Log.LogInfo(Tag + "too many vehicles with damage maps: '" + oldest.name + "' gives its atlas back");
                Vehicles[oldest].Dispose(oldest != null);
                Vehicles.Remove(oldest);
            }

            maps = VehicleMaps.Create(aircraft);
            if (maps == null)
            {
                Refused.Add(aircraft);
                ArmorFragmentsPlugin.Log.LogInfo(Tag + "no damage maps for '" + aircraft.name + "': " + VehicleMaps.LastFailure);
                return null;
            }
            Vehicles[aircraft] = maps;
            return maps;
        }

        // ---- Every frame ------------------------------------------------------------------------

        public static void Tick()
        {
            string message;
            while (DamageWorker.Messages.TryDequeue(out message)) ArmorFragmentsPlugin.Log.LogWarning(message);
            while (DamageWorker.Infos.TryDequeue(out message)) ArmorFragmentsPlugin.Log.LogInfo(message);
            ShedLoad();
            PerfMonitor.Lap("DMAP messages");

            // The atlases a hit vehicle will need are made one a frame, not all in the frame of its first impact.
            foreach (VehicleMaps vehicle in Vehicles.Values)
                if (vehicle.WarmUp()) break;
            PerfMonitor.Lap("DMAP atlases");

            // The fragment marks go to the worker. A plate's first mark makes its setup (its mesh gets the atlas UVs and tangents, which can
            // take milliseconds on a big mesh), so a burst that reaches dozens of plates at once would be one very long frame: at most
            // MaxSetupsPerFrame plates are set up a frame, and the marks of the others wait for the next frames.
            if (Pits.Count > 0)
            {
                int setups = 0;
                List<PartScript> shown = null;
                foreach (KeyValuePair<PartScript, PitBuffer> pair in Pits)
                {
                    if (pair.Key == null || pair.Value.Impacts.Count == 0) { (shown ?? (shown = new List<PartScript>())).Add(pair.Key); continue; }
                    if (NeedsSetup(pair.Key))
                    {
                        if (setups >= MaxSetupsPerFrame) continue;   // the next frame
                        setups++;
                    }
                    Show(pair.Key, pair.Value.Impacts.ToArray(), pair.Value.Delay);
                    (shown ?? (shown = new List<PartScript>())).Add(pair.Key);
                }
                if (shown != null) foreach (PartScript part in shown) Pits.Remove(part);
            }
            PerfMonitor.Lap("DMAP pits");

            // What the worker has finished goes into the atlases, a few rectangles a frame (and not before the shell's
            // ray has arrived), never more than a couple of milliseconds.
            Delivery done;
            while (DamageWorker.Done.TryDequeue(out done)) Holding.Add(done);
            if (Holding.Count > 0)
            {
                System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
                float now = Time.time;
                int delivered = 0;
                for (int i = 0; i < Holding.Count && delivered < 16; )
                {
                    Delivery d = Holding[i];
                    if (d.Owner.Disposed) { Holding.RemoveAt(i); continue; }
                    if (d.DeliverAt > now) { i++; continue; }

                    try
                    {
                        lastDeliverMs = d.Owner.Deliver(d);
                    }
                    catch (Exception e)
                    {
                        ArmorFragmentsPlugin.Log.LogWarning(Tag + "a rectangle could not be delivered: " + e.Message);
                    }
                    Holding.RemoveAt(i);
                    delivered++;
                    if (watch.Elapsed.TotalMilliseconds > 2.0) break;
                }
            }

            PerfMonitor.Lap("DMAP deliver");
            if (++frame % 120 == 0) Prune();
            PerfMonitor.Lap("DMAP prune");
            TickSettings();
            PerfMonitor.Lap("DMAP settings");
            TickGeometrySettings();
            PerfMonitor.Lap("DMAP geometry settings");
            TickMeshes();
            PerfMonitor.Lap("DMAP meshes");
            GeometryView.Tick(Vehicles.Values);
            PerfMonitor.Lap("DMAP geometry view");
            TickTest();
        }

        private const int MaxSetupsPerFrame = 2;

        // Would marking this plate now have to set it up first (make the vehicle's maps, or the plate's own mesh and material)?
        private static bool NeedsSetup(PartScript part)
        {
            if (part.Aircraft == null) return false;
            VehicleMaps maps;
            if (!Vehicles.TryGetValue(part.Aircraft, out maps)) return !Refused.Contains(part.Aircraft);
            PlateVisual plate;
            return maps.Plates.TryGetValue(part, out plate) && !plate.Applied && !plate.Failed;
        }

        // ---- Settings that change what is drawn ---------------------------------------------------

        private static int shownRelief;
        private static bool shownDamageView;

        // Real geometry (experimental): the job also rewrites the plate's mesh, if the setting is on and the plate is a flat rectangle.
        private static void WantGeometry(DamageWorker.Job job, PlateVisual plate)
        {
            if (!ArmorFragmentsPlugin.DamageMapsGeometry.Value || !plate.CanRemesh()) return;
            job.Geometry = true;
            job.GridMeters = ArmorFragmentsPlugin.DamageMapsGridMm.Value * 0.001f;
            job.MaxVertices = ArmorFragmentsPlugin.DamageMapsMaxKiloVertices.Value * 1000;
        }

        // How many simulations may wait their turn. One shot that goes through several plates asks for one simulation per plate, so a fixed
        // small number sent the last plates of a chain to the quick marks. The setting is the least; while simulations are quick (the graphics
        // card: tens of milliseconds) more may wait, as many as fit in about 0.8 s of work, up to 12. A slow machine keeps the setting.
        private static int QueueLimit()
        {
            int least = ArmorFragmentsPlugin.SimMaxQueue.Value;
            int typical = Math.Max(DamageWorker.LastSimMs, 20);
            return Math.Max(least, Math.Min(12, 800 / typical));
        }

        private static readonly List<MeshData> HoldingMeshes = new List<MeshData>();
        private static bool shownGeometry;
        private static float shownGrid;

        // What the worker has rewritten goes into the plates' meshes, one at a time and not before the shell's ray has arrived.
        private static void TickMeshes()
        {
            MeshData data;
            while (DamageWorker.Meshes.TryDequeue(out data)) HoldingMeshes.Add(data);
            if (HoldingMeshes.Count == 0) return;

            float now = Time.time;
            for (int i = 0; i < HoldingMeshes.Count; i++)
            {
                data = HoldingMeshes[i];
                if (data.Plate.Owner.Disposed || !ArmorFragmentsPlugin.DamageMapsGeometry.Value) { HoldingMeshes.RemoveAt(i); i--; continue; }
                if (data.DeliverAt > now) continue;

                HoldingMeshes.RemoveAt(i);
                try
                {
                    double ms = data.Plate.ApplyGeometry(data, GeometryView.Need(data.Plate, data.MaxHeight));
                    if (data.Plate.GeometryApplied <= 3)
                        ArmorFragmentsPlugin.Log.LogInfo("[GEOM] plate '" + data.Plate.Info.Part.Part.Name + "': big-face triangles " + data.Before + " -> " + data.After +
                            " around the damage, " + data.Vertices.Length + " vertices, " + (data.Triangles.Length / 3) + " triangles, up to " +
                            (data.MaxHeight * 1000f).ToString("F0") + " mm of push; built in " +
                            data.BuildMs.ToString("F1") + " ms (worker), put in the mesh in " + ms.ToString("F1") + " ms");
                }
                catch (Exception e)
                {
                    ArmorFragmentsPlugin.Log.LogWarning("[GEOM] a plate's mesh could not be rewritten: " + e.Message);
                }
                return;   // one a frame
            }
        }

        // The geometry setting (or its grid) changed: every damaged plate is rewritten, or given its own mesh back.
        private static void TickGeometrySettings()
        {
            bool on = ArmorFragmentsPlugin.DamageMapsGeometry.Value;
            float grid = ArmorFragmentsPlugin.DamageMapsGridMm.Value;
            if (on == shownGeometry && (!on || Mathf.Approximately(grid, shownGrid))) return;

            bool turnedOff = shownGeometry && !on;
            shownGeometry = on;
            shownGrid = grid;

            foreach (VehicleMaps maps in Vehicles.Values)
            {
                if (maps.Disposed) continue;
                foreach (PlateVisual plate in maps.Plates.Values)
                {
                    if (!plate.Applied || (plate.Cpu.Faces[0] == null && plate.Cpu.Faces[1] == null)) continue;
                    if (turnedOff) plate.RestoreGeometry();

                    DamageWorker.Job job = new DamageWorker.Job
                    {
                        Owner = maps, Plate = plate, Impacts = new PlateImpact[0], DeliverAt = Time.time,
                        Height = ArmorFragmentsPlugin.DamageMapsRelief.Value != 0, Damage = ArmorFragmentsPlugin.ImpactCameraDamageView.Value,
                        Everything = turnedOff,   // without geometry the holes need the hole atlas again
                    };
                    WantGeometry(job, plate);
                    DamageWorker.Enqueue(job);
                }
            }
        }

        // The vehicle's maps, if it has any (the impact camera's damage view draws from them).
        internal static VehicleMaps MapsFor(AircraftScript aircraft)
        {
            VehicleMaps maps;
            return aircraft != null && Vehicles.TryGetValue(aircraft, out maps) && !maps.Disposed ? maps : null;
        }

        // The relief mode or the damage view was switched: the materials are set again, and a stream that was off until now gets
        // everything the plates already have (they are marked on the CPU; the worker draws them into the new atlas).
        private static void TickSettings()
        {
            int relief = Mathf.Clamp(ArmorFragmentsPlugin.DamageMapsRelief.Value, 0, 2);
            bool view = ArmorFragmentsPlugin.ImpactCameraDamageView.Value;
            if (relief == shownRelief && view == shownDamageView) return;

            bool catchUpHeight = relief != 0 && shownRelief == 0;
            bool catchUpView = view && !shownDamageView;
            bool reliefChanged = relief != shownRelief;
            shownRelief = relief;
            shownDamageView = view;

            foreach (VehicleMaps maps in Vehicles.Values)
            {
                if (maps.Disposed) continue;
                if (reliefChanged) maps.RefreshRelief();
                if (!catchUpHeight && !catchUpView) continue;

                foreach (PlateVisual plate in maps.Plates.Values)
                {
                    if (!plate.Applied || (plate.Cpu.Faces[0] == null && plate.Cpu.Faces[1] == null)) continue;
                    DamageWorker.Job job = new DamageWorker.Job
                    {
                        Owner = maps, Plate = plate, Impacts = new PlateImpact[0], DeliverAt = Time.time,
                        Height = relief != 0, Damage = view, Everything = true,
                    };
                    WantGeometry(job, plate);
                    DamageWorker.Enqueue(job);
                }
            }
        }

        // ---- The simulation's load ------------------------------------------------------------------

        private static float fastFrame, slowFrame, lastLoadChange;
        private static int allowedWorkers = int.MaxValue;

        // If the frame rate drops while the simulation works, it lends threads back (one at a time), and takes them again
        // after a calm moment. "Dropped" = the last few frames are both slow (under 40 fps) and clearly slower than usual.
        private static void ShedLoad()
        {
            float dt = Time.unscaledDeltaTime;
            if (dt <= 0f || dt > 1f) return;
            fastFrame = fastFrame <= 0f ? dt : Mathf.Lerp(fastFrame, dt, 0.25f);
            slowFrame = slowFrame <= 0f ? dt : Mathf.Lerp(slowFrame, dt, 0.02f);

            int configured = DamageWorker.ConfiguredWorkers(ArmorFragmentsPlugin.SimThreads.Value);
            if (!ArmorFragmentsPlugin.SimShedLoad.Value || DamageWorker.PendingSims == 0)
            {
                if (allowedWorkers != int.MaxValue) { allowedWorkers = int.MaxValue; DamageWorker.SetAllowedWorkers(int.MaxValue); }
                return;
            }

            float now = Time.unscaledTime;
            bool pressure = fastFrame > 0.025f && fastFrame > slowFrame * 1.35f;
            int current = Mathf.Min(allowedWorkers, configured);
            if (pressure && now - lastLoadChange > 0.5f && current > 0)
            {
                allowedWorkers = current - 1;
                lastLoadChange = now;
                DamageWorker.SetAllowedWorkers(allowedWorkers);
            }
            else if (!pressure && now - lastLoadChange > 1.5f && allowedWorkers < configured)
            {
                allowedWorkers++;
                lastLoadChange = now;
                DamageWorker.SetAllowedWorkers(allowedWorkers);
            }
        }

        // Aircraft that are gone: free their atlases and forget their plates.
        private static void Prune()
        {
            List<AircraftScript> gone = null;
            foreach (AircraftScript aircraft in Vehicles.Keys)
                if (aircraft == null) (gone ?? (gone = new List<AircraftScript>())).Add(aircraft);
            if (gone != null)
                foreach (AircraftScript aircraft in gone)
                {
                    Vehicles[aircraft].Dispose(false);
                    Vehicles.Remove(aircraft);
                }

            List<PartScript> parts = null;
            foreach (PartScript part in States.Keys)
                if (part == null) (parts ?? (parts = new List<PartScript>())).Add(part);
            if (parts != null)
                foreach (PartScript part in parts) States.Remove(part);

            parts = null;
            foreach (PartScript part in Infos.Keys)
                if (part == null) (parts ?? (parts = new List<PartScript>())).Add(part);
            if (parts != null)
                foreach (PartScript part in parts) Infos.Remove(part);
            NoInfo.RemoveWhere(p => p == null);
            Refused.RemoveWhere(a => a == null);
        }

        // Everything back as it was: the plates' looks and the model's holes and dents (the armor is whole again).
        public static void Clear()
        {
            foreach (VehicleMaps maps in Vehicles.Values) maps.Dispose(maps.Aircraft != null);
            Vehicles.Clear();
            States.Clear();
            Refused.Clear();
            Pits.Clear();
            Holding.Clear();
            HoldingMeshes.Clear();
            Message = "Cleared";
        }

        // The plugin is being unloaded: the plates look as the game made them, the worker thread and the simulation's threads stop
        // (without waiting for them) and every texture is freed. The model's holes and dents are forgotten with the rest.
        public static void Shutdown()
        {
            Clear();                     // restores every plate and frees the atlases
            DamageWorker.Shutdown();
            VehicleMaps.ReleaseStaging();
            Infos.Clear();
            NoInfo.Clear();
            testPending = false;
        }

        // ---- Test impacts (panel) ----------------------------------------------------------------

        private static bool testPending;
        private static DamageKind testKind;
        private static float testAt;

        public static void RequestTest(DamageKind kind)
        {
            testPending = true;
            testKind = kind;
            testAt = Time.unscaledTime + 3f;
            Message = "Put the mouse over a plate: 3 s";
        }

        private static void TickTest()
        {
            if (!testPending) return;
            float left = testAt - Time.unscaledTime;
            if (left > 0f)
            {
                Message = "Put the mouse over a plate: " + Mathf.CeilToInt(left) + " s";
                return;
            }
            testPending = false;

            try
            {
                CameraManagerScript manager = CameraManagerScript.Instance;
                Camera camera = manager != null && manager.MainCamera != null ? manager.MainCamera : Camera.main;
                if (camera == null) { Message = "No camera"; return; }

                Vector2 screen = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
                Mouse mouse = Mouse.current;
                if (mouse != null) screen = mouse.position.ReadValue();

                Ray ray = camera.ScreenPointToRay(screen);
                int mask = -1 & ~LayerMask.GetMask("WorldSpaceUI");
                RaycastHit[] hits = Physics.RaycastAll(ray, 2000f, mask, QueryTriggerInteraction.Ignore);
                Array.Sort(hits, (x, y) => x.distance.CompareTo(y.distance));
                foreach (RaycastHit hit in hits)
                {
                    PartScript part = hit.collider.GetComponentInParent<PartScript>();
                    if (part == null) continue;

                    float angle = Vector3.Angle(ray.direction, -hit.normal);
                    Vector3 direction = ray.direction;
                    if (testKind == DamageKind.Ricochet) direction = Vector3.Reflect(ray.direction, hit.normal);
                    OnImpact(part, TagCodec.Parse(part.Part.Name).Armor, testKind, hit.point, hit.normal, direction,
                             0.075f, 0.7f, Mathf.Min(angle, 85f), 0f,
                             EnergyModel.ShellMassForCaliber(75f), 800f, testKind == DamageKind.Ricochet ? 0.3f : 1f);
                    Message = "Test " + testKind + " on '" + part.Part.Name + "'";
                    ArmorFragmentsPlugin.Log.LogInfo(Tag + Message + ", angle " + angle.ToString("F0") + " deg");
                    return;
                }
                Message = "No part under the mouse";
            }
            catch (Exception e)
            {
                Message = "Test failed: " + e.Message;
                ArmorFragmentsPlugin.Log.LogError(Tag + "test failed: " + e);
            }
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
                if (aircraft == null || aircraft.LoadContext != CraftLoadContext.Flight) return;

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
                    ArmorFragmentsPlugin.Log.LogInfo("[DMAP] " + plates + " armor plate(s) of a group kept out of the mesh merge");
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("[DMAP] plate merge patch failed: " + e.Message);
            }
        }
    }
}
