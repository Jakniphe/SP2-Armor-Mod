using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using ArmorFragments;
using ArmorFragments.Sim;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SimLab
{
    // What the page sends: the armor, the ammunition and the shot.
    public sealed class Scenario
    {
        public float Thickness { get; set; } = 20;      // mm
        public float Hardness { get; set; } = 100;      // % (the plate's)
        public float Brittleness { get; set; } = 50;    // % (the plate's)
        public float Caliber { get; set; } = 75;        // mm
        public float Mass { get; set; } = 0;            // kg; 0 = what the game gives a shell of that caliber
        public float Speed { get; set; } = 800;         // m/s
        public float ShellHardness { get; set; } = 100; // %
        public string Paint { get; set; } = "#6b7352";  // the plate's paint (the marks scorch it and flake it off)
        public string Nose { get; set; } = "ap";        // the shape of the shell's nose: legacy (the old hemisphere), ap, aphe, he, or custom
        public float NoseLength { get; set; } = 1.4f;   // custom: the nose's length in shell radii
        public float NoseFullness { get; set; } = 1.95f;// custom: its exponent (1 = cone, 2 = ellipse, more = blunter)
        public float Angle { get; set; } = 0;           // degrees from the plate's normal
        public float Azimuth { get; set; } = 0;         // degrees: the way the shell goes along the plate
        public int Quality { get; set; } = 1;           // 0 low, 1 medium, 2 high
        public int Threads { get; set; } = 0;           // 0 = all the cores
        public int Seed { get; set; } = 1;
        public float MapPxPerM { get; set; } = 120;     // the plate's map, pixels per meter (the game gives 120 to a vehicle with many plates): caps the grid
        public bool Cracks { get; set; } = true;        // the mod's SimCracks
        public bool Smooth { get; set; } = true;        // the mod's SimSmoothEdges
        public bool Gpu { get; set; }                   // run on the graphics card (OpenCL) if the computer has one that works
        public float EnergyScale { get; set; } = 1;     // a knob: 1 = the shell's real energy
    }

    public sealed class SweepRequest
    {
        public Scenario Scenario { get; set; } = new Scenario();
        public float From { get; set; } = 200;
        public float To { get; set; } = 1200;
        public int Count { get; set; } = 6;
    }

    // The lab's engine: the web API and the pages, ready to be hosted by a console program (SimLab.Web) or a desktop window
    // (SimLab.Desktop). Everything runs in this process; nothing leaves the computer.
    public static class LabServer
    {
        private static SimPool pool;
        private static int poolWorkers = -1;

        // ---- The graphics card ----------------------------------------------------------------------
        // Every OpenCL call must come from one thread, so the card has a thread of its own that does all the lab's card work. It is started
        // (and the kernels are built, which can take seconds the first time) as soon as the lab starts.
        private static System.Collections.Concurrent.BlockingCollection<Action> gpuQueue;
        private static ClSolver gpuSolver;
        private static volatile string gpuState = "starting";   // starting / ready / not available: <why>
        private static volatile string gpuName = "";

        // How far the last run of the page is, 0..1 (read by /api/progress).
        private static volatile float progress;
        private static volatile bool running;

        private static void StartGpu()
        {
            if (gpuQueue != null) return;
            gpuQueue = new System.Collections.Concurrent.BlockingCollection<Action>();
            System.Threading.Thread thread = new System.Threading.Thread(() =>
            {
                try
                {
                    string error;
                    gpuSolver = ClSolver.Create(out error);
                    if (gpuSolver == null) gpuState = "not available: " + error;
                    else { gpuName = gpuSolver.DeviceName; gpuState = "ready"; }
                }
                catch (Exception e) { gpuState = "not available: " + e.Message; }
                foreach (Action work in gpuQueue.GetConsumingEnumerable()) work();
            });
            thread.IsBackground = true;
            thread.Name = "lab graphics card";
            thread.Start();
        }

        // Runs the simulation on the card, on the card's thread. Null = the card cannot be used (not there, still starting, or it failed).
        private static ImpactSimResult OnCard(ImpactSimInput input)
        {
            StartGpu();
            System.Threading.Tasks.TaskCompletionSource<ImpactSimResult> done = new System.Threading.Tasks.TaskCompletionSource<ImpactSimResult>();
            gpuQueue.Add(() =>
            {
                try { done.SetResult(gpuSolver == null ? null : ImpactSim.Run(input, null, gpuSolver)); }
                catch (Exception e) { gpuState = "failed: " + e.Message; gpuSolver = null; done.SetResult(null); }
            });
            return done.Task.Result;
        }

        // The page, as embedded in this assembly.
        private static string html;

        private static string Html()
        {
            if (html != null) return html;
            using (Stream stream = typeof(LabServer).Assembly.GetManifestResourceStream("index.html"))
            using (StreamReader reader = new StreamReader(stream))
                html = reader.ReadToEnd();
            return html;
        }

        // Builds the web application (not started). 'url' may use port 0 to take a free one.
        public static WebApplication Create(string url, bool quiet)
        {
            ArmorFragmentsPlugin.RicochetRandomness.Value = 0f;   // the same answer every time
            StartGpu();

            WebApplicationBuilder builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls(url);
            if (quiet) builder.Logging.ClearProviders();
            WebApplication app = builder.Build();

            app.MapGet("/", () => Results.Content(Html(), "text/html; charset=utf-8"));
            app.MapGet("/api/info", () => Results.Json(new
            {
                cores = Environment.ProcessorCount,
                defaults = new
                {
                    resistance = ArmorFragmentsPlugin.ArmorResistance.Value,
                    ricochetAngle = ArmorFragmentsPlugin.ArmorRicochetAngle.Value,
                    maxRicochets = ArmorFragmentsPlugin.MaxRicochets.Value,
                },
                source = ArmorFragmentsPlugin.Source,
                gpu = new { state = gpuState, name = gpuName },
            }));
            app.MapGet("/api/progress", () => Results.Json(new { value = progress, running }));
            app.MapPost("/api/run", (Scenario s) =>
            {
                progress = 0f;
                running = true;
                try { return Results.Json(RunScenario(s, true, true)); }
                finally { progress = 1f; running = false; }
            });
            app.MapPost("/api/sweep", (SweepRequest r) => Results.Json(Sweep(r)));
            app.MapPost("/api/scaling", (Scenario s) => Results.Json(Scaling(s)));
            return app;
        }

        // The address the application is listening on (after it has started).
        public static string AddressOf(WebApplication app)
        {
            IServerAddressesFeature feature = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
            return feature.Addresses.First();
        }

        // ---- One scenario -------------------------------------------------------------------------

        private static SimPool PoolFor(int threads)
        {
            int total = threads > 0 ? threads : Environment.ProcessorCount;
            int workers = Math.Max(0, total - 1);   // the calling thread works too
            if (pool == null || poolWorkers != workers)
            {
                if (pool != null) pool.Dispose();
                pool = new SimPool(workers);
                poolWorkers = workers;
            }
            return pool;
        }

        private static object RunScenario(Scenario s, bool images, bool view3d = false)
        {
            float caliberM = s.Caliber * 0.001f;
            float mass = s.Mass > 0f ? s.Mass : EnergyModel.ShellMassForCaliber(s.Caliber);
            ShellInfo shell = new ShellInfo { Mass = mass, Speed = s.Speed, CaliberMm = s.Caliber, Hardness = s.ShellHardness };
            ArmorTag armor = new ArmorTag { Thickness = s.Thickness, Hardness = s.Hardness, Brittleness = s.Brittleness };

            // What the game's own energy model says about this shot.
            string breakdown;
            float critical = EnergyModel.RicochetCriticalAngle(armor, shell, out breakdown);
            bool ricochet = ArmorFragmentsPlugin.MaxRicochets.Value > 0 && s.Angle >= critical;
            float effective;
            float absorbed = EnergyModel.ArmorAbsorption(armor, shell, s.Angle, out effective);
            float energy = shell.Energy;
            string verdict = ricochet ? "Ricochet" : (energy < absorbed ? "Blocked" : "Penetrated");
            float kept = ricochet ? EnergyModel.RicochetEnergyOut(energy, s.Angle) / Math.Max(energy, 1f) : 1f;

            // The simulation, with the same numbers.
            float azimuth = s.Azimuth * (float)(Math.PI / 180.0);
            ImpactSimInput input = new ImpactSimInput
            {
                CaliberM = caliberM, ShellMassKg = mass, SpeedMS = s.Speed, AngleDeg = s.Angle,
                DirU = (float)Math.Cos(azimuth), DirV = (float)Math.Sin(azimuth),
                ThicknessM = s.Thickness * 0.001f, PlateHardness = s.Hardness, PlateBrittleness = s.Brittleness / 100f,
                ResistanceJPerM3 = ArmorFragmentsPlugin.ArmorResistance.Value * 1e7f,
                Ricochet = ricochet, KeptEnergy = kept, EnergyScale = s.EnergyScale, Quality = s.Quality, Seed = s.Seed,
                Profile = ProfileOf(s),
                // a shell that goes through is turned toward the plate's normal by the energy model's own number
                DeflectionDeg = verdict == "Penetrated" ? EnergyModel.DeflectionDegrees(armor, shell, s.Angle) : 0f,
            };
            // No finer than the plate's map can show (the game's ChooseColumns does the same; the ladder is its Ladder)
            int wantedColumns = ImpactSim.ColumnsOfQuality(s.Quality);
            int usefulColumns = ImpactSim.UsefulColumns(caliberM, s.Angle, s.MapPxPerM);
            bool mapLimited = false;
            if (usefulColumns < wantedColumns)
            {
                int[] ladder = { 128, 112, 96, 80, 64, 56, 48, 40, 32 };
                int enough = ladder[0];
                for (int i = ladder.Length - 1; i >= 0; i--) if (ladder[i] >= usefulColumns) { enough = ladder[i]; break; }
                if (enough < wantedColumns) { input.Columns = enough; mapLimited = true; }
            }
            input.Progress = p => { if (p > progress) progress = p; };
            ImpactSimResult sim = s.Gpu ? OnCard(input) : null;
            bool onCard = sim != null;
            if (sim == null) sim = ImpactSim.Run(input, PoolFor(s.Threads));

            // What the mod draws today (the analytic stamps), on a patch of the same size.
            float[] anaFront, anaBack; byte[] anaHole;
            Analytic(s, input, verdict, energy, absorbed, kept, sim, out anaFront, out anaBack, out anaHole);

            // What the game would draw: the simulation laid into a plate tile (256 px per meter), or the quick marks if the
            // simulation does not agree with the model (as the game does).
            float[] gameFront, gameBack; byte[] gameHole; int gameN; byte[] gameFrontColor, gameBackColor;
            DamageKind kind = verdict == "Ricochet" ? DamageKind.Ricochet : (verdict == "Blocked" ? DamageKind.Blocked : DamageKind.Penetrated);
            bool agrees = SimBridge.Agrees(kind, sim);
            InGame(s, input, kind, energy, absorbed, kept, sim, agrees, out gameFront, out gameBack, out gameHole, out gameN, out gameFrontColor, out gameBackColor);

            object picture = null;
            if (images)
            {
                float scale = 4f;
                picture = new
                {
                    gameFront = Png.Relief(gameFront, gameHole, gameN, 1f / Math.Max(s.MapPxPerM, 32f), 512f / gameN),
                    gameBack = Png.Relief(gameBack, gameHole, gameN, 1f / Math.Max(s.MapPxPerM, 32f), 512f / gameN),
                    simFront = Png.Relief(sim.Front, sim.Hole, sim.N, sim.Cell, scale),
                    simBack = Png.Relief(sim.Back, sim.Hole, sim.N, sim.Cell, scale),
                    simSection = Png.Section(sim.Front, sim.Back, sim.Hole, sim.N, sim.Cell, input.ThicknessM, input.CaliberM),
                    anaFront = Png.Relief(anaFront, anaHole, sim.N, sim.Cell, scale),
                    anaBack = Png.Relief(anaBack, anaHole, sim.N, sim.Cell, scale),
                    anaSection = Png.Section(anaFront, anaBack, anaHole, sim.N, sim.Cell, input.ThicknessM, input.CaliberM),
                };
            }

            return new
            {
                model = new
                {
                    verdict,
                    criticalAngle = Math.Min(critical, 90f),
                    breakdown,
                    shellEnergyKJ = energy / 1000f,
                    absorbedKJ = absorbed / 1000f,
                    ratio = energy / Math.Max(absorbed, 1f),
                    effectiveMm = effective,
                    massKg = mass,
                    keptPercent = kept * 100f,
                },
                sim = new
                {
                    perforated = sim.Perforated,
                    holeCells = sim.Hole.Count(b => b == 0),
                    depthMm = sim.DepthM * 1000f,
                    holeDiameterMm = sim.HoleRadiusM * 2000f,
                    energyLeftPercent = sim.EnergyLeft * 100f,
                    steps = sim.Steps,
                    particles = sim.Particles, charged = sim.Charged, broken = sim.Broken,
                    ms = sim.Ms,
                    agrees,
                    threads = s.Threads > 0 ? s.Threads : Environment.ProcessorCount,
                    columns = sim.N,
                    mapLimited,
                    mapPxPerM = s.MapPxPerM,
                    gpu = onCard,
                    gpuName,
                    gpuState,
                    cellMm = sim.Cell * 1000f,
                    patchCm = sim.Size * 100f,
                },
                images = picture,
                // the plate itself, for the three-dimensional view: what the game draws, the simulation's own grid, the quick marks
                plate3d = view3d ? new[]
                {
                    Pack3D("As the game draws it", gameFront, gameBack, gameHole, gameN, 1f / Math.Min(Math.Max(s.MapPxPerM, 32f), 1024f), input.ThicknessM, 240, gameFrontColor, gameBackColor),
                    Pack3D("The simulation's own grid", sim.Front, sim.Back, sim.Hole, sim.N, sim.Cell, input.ThicknessM),
                    Pack3D("The quick marks (what the mod drew before the simulation)", anaFront, anaBack, anaHole, sim.N, sim.Cell, input.ThicknessM),
                } : null,
            };
        }

        // A plate's heights as the page's viewer wants them: the middle (at most 'limit' pixels a side) of the front's and the back's height
        // fields (16-bit, in units of 10 micrometers) and the hole mask (0 = hole), each as base64.
        private static object Pack3D(string label, float[] front, float[] back, byte[] hole, int n, float cell, float thickness, int limit = 240,
                                     byte[] frontColor = null, byte[] backColor = null)
        {
            int w = Math.Min(n, limit), x0 = (n - w) / 2, y0 = (n - w) / 2;
            byte[] f = new byte[w * w * 2], b = new byte[w * w * 2], h = new byte[w * w];
            byte[] fc = frontColor != null ? new byte[w * w * 3] : null, bc = backColor != null ? new byte[w * w * 3] : null;
            for (int j = 0; j < w; j++)
            {
                for (int i = 0; i < w; i++)
                {
                    int src = (y0 + j) * n + (x0 + i), dst = j * w + i;
                    short fv = (short)Math.Max(-32000, Math.Min(32000, (int)Math.Round(front[src] / 1e-5f)));
                    short bv = (short)Math.Max(-32000, Math.Min(32000, (int)Math.Round(back[src] / 1e-5f)));
                    f[2 * dst] = (byte)(fv & 0xFF); f[2 * dst + 1] = (byte)((fv >> 8) & 0xFF);
                    b[2 * dst] = (byte)(bv & 0xFF); b[2 * dst + 1] = (byte)((bv >> 8) & 0xFF);
                    h[dst] = hole[src];
                    for (int k = 0; k < 3; k++)
                    {
                        if (fc != null) fc[3 * dst + k] = frontColor[3 * src + k];
                        if (bc != null) bc[3 * dst + k] = backColor[3 * src + k];
                    }
                }
            }
            return new
            {
                label, n = w, cell, thickness, front = Convert.ToBase64String(f), back = Convert.ToBase64String(b), hole = Convert.ToBase64String(h),
                frontColor = fc != null ? Convert.ToBase64String(fc) : null, backColor = bc != null ? Convert.ToBase64String(bc) : null,
            };
        }

        // The analytic stamp of the same impact, as arrays of the same size as the simulation's.
        private static void Analytic(Scenario s, ImpactSimInput input, string verdict, float energy, float absorbed, float kept,
                                     ImpactSimResult sim, out float[] front, out float[] back, out byte[] hole)
        {
            int n = sim.N;
            PlateCpu cpu = new PlateCpu
            {
                Width = n + 2 * PlateCpu.Border,
                Height = n + 2 * PlateCpu.Border,
                PxU = n / sim.Size,
                PxV = n / sim.Size,
            };
            DamageKind kind = verdict == "Ricochet" ? DamageKind.Ricochet : (verdict == "Blocked" ? DamageKind.Blocked : DamageKind.Penetrated);
            float strength = kind == DamageKind.Blocked ? Math.Min(1f, energy / Math.Max(absorbed, 1f))
                           : kind == DamageKind.Penetrated ? Math.Min(1f, absorbed / Math.Max(energy, 1f))
                           : 1f - kept;
            PlateStamps.Apply(cpu, new PlateImpact
            {
                Kind = kind, Front = true, U = sim.Size * 0.5f, V = sim.Size * 0.5f,
                DirU = input.DirU, DirV = input.DirV, Angle = s.Angle, Caliber = input.CaliberM,
                Strength = strength, Thickness = input.ThicknessM, Seed = s.Seed, Profile = input.Profile,
            });

            front = new float[n * n]; back = new float[n * n]; hole = new byte[n * n];
            for (int i = 0; i < hole.Length; i++) hole[i] = 255;
            PlateCpu.Face f0 = cpu.Faces[0], f1 = cpu.Faces[1];
            for (int j = 0; j < n; j++)
            {
                for (int i = 0; i < n; i++)
                {
                    int at = (j + PlateCpu.Border) * cpu.Width + (i + PlateCpu.Border);
                    if (f0 != null) { front[j * n + i] = f0.Height[at]; if (f0.Mask[at] < 128) hole[j * n + i] = 0; }
                    if (f1 != null) { back[j * n + i] = f1.Height[at]; if (f1.Mask[at] < 128) hole[j * n + i] = 0; }
                }
            }
        }

        // The same thing the game does with a finished simulation: lays it into the plate's tile (or, if it disagrees with the
        // energy model, draws the quick marks), then reads the tile back as arrays.
        private static void InGame(Scenario s, ImpactSimInput input, DamageKind kind, float energy, float absorbed, float kept,
                                   ImpactSimResult sim, bool agrees, out float[] front, out float[] back, out byte[] hole, out int n,
                                   out byte[] frontColor, out byte[] backColor)
        {
            const float plate = 1.0f;
            float density = Math.Min(Math.Max(s.MapPxPerM, 32f), 1024f);   // the map's pixels per meter, as the game's plate has it
            n = (int)(plate * density);
            PlateCpu cpu = new PlateCpu { Width = n + 2 * PlateCpu.Border, Height = n + 2 * PlateCpu.Border, PxU = n / plate, PxV = n / plate, PaintMode = true };
            cpu.Paint = ParsePaint(s.Paint);
            float strength = kind == DamageKind.Blocked ? Math.Min(1f, energy / Math.Max(absorbed, 1f))
                           : kind == DamageKind.Penetrated ? Math.Min(1f, absorbed / Math.Max(energy, 1f)) : 1f - kept;
            PlateImpact impact = new PlateImpact
            {
                Kind = kind, Front = true, U = plate * 0.5f, V = plate * 0.5f, DirU = input.DirU, DirV = input.DirV, Angle = s.Angle,
                Caliber = input.CaliberM, Strength = strength, Thickness = input.ThicknessM, Seed = s.Seed,
                ShellMass = input.ShellMassKg, Speed = input.SpeedMS, PlateHardness = input.PlateHardness,
                PlateBrittleness = input.PlateBrittleness, KeptEnergy = kept, Deflection = input.DeflectionDeg, Profile = input.Profile,
            };
            if (agrees) SimBridge.Apply(cpu, impact, sim, s.Cracks, s.Smooth); else PlateStamps.Apply(cpu, impact);

            front = new float[n * n]; back = new float[n * n]; hole = new byte[n * n];
            frontColor = new byte[n * n * 3]; backColor = new byte[n * n * 3];
            for (int i = 0; i < hole.Length; i++) hole[i] = 255;
            PlateCpu.Face f0 = cpu.Faces[0], f1 = cpu.Faces[1];
            for (int j = 0; j < n; j++)
            {
                for (int i = 0; i < n; i++)
                {
                    int at = (j + PlateCpu.Border) * cpu.Width + (i + PlateCpu.Border);
                    int dst = j * n + i;
                    if (f0 != null) { front[dst] = f0.Height[at]; if (f0.Mask[at] < 128) hole[dst] = 0; }
                    if (f1 != null) { back[dst] = f1.Height[at]; if (f1.Mask[at] < 128) hole[dst] = 0; }
                    PlateStamps.PixelColor(cpu, f0, at, out frontColor[3 * dst], out frontColor[3 * dst + 1], out frontColor[3 * dst + 2]);
                    PlateStamps.PixelColor(cpu, f1, at, out backColor[3 * dst], out backColor[3 * dst + 1], out backColor[3 * dst + 2]);
                }
            }
        }

        // "#rrggbb" to the 0..1 color the plate's paint is kept in.
        private static UnityEngine.Vector3 ParsePaint(string text)
        {
            try
            {
                string hex = (text ?? "").TrimStart('#');
                if (hex.Length == 6)
                    return new UnityEngine.Vector3(Convert.ToInt32(hex.Substring(0, 2), 16) / 255f, Convert.ToInt32(hex.Substring(2, 2), 16) / 255f, Convert.ToInt32(hex.Substring(4, 2), 16) / 255f);
            }
            catch (Exception) { }
            return new UnityEngine.Vector3(0.42f, 0.45f, 0.32f);
        }

        // ---- Sweeps -------------------------------------------------------------------------------

        private static object Sweep(SweepRequest r)
        {
            List<object> rows = new List<object>();
            int count = Math.Max(2, Math.Min(r.Count, 10));
            for (int i = 0; i < count; i++)
            {
                Scenario s = Clone(r.Scenario);
                s.Speed = r.From + (r.To - r.From) * i / (count - 1);
                object one = RunScenario(s, true);
                rows.Add(new { speed = s.Speed, result = one });
            }
            return rows;
        }

        private static object Scaling(Scenario s)
        {
            List<object> rows = new List<object>();
            int cores = Environment.ProcessorCount;
            List<int> counts = new List<int>();
            for (int c = 1; c < cores; c *= 2) counts.Add(c);
            counts.Add(cores);
            foreach (int threads in counts)
            {
                Scenario one = Clone(s);
                one.Threads = threads;
                RunScenario(one, false);   // a warm-up
                double best = double.MaxValue;
                for (int rep = 0; rep < 3; rep++)
                {
                    ImpactSimResult sim = Quick(one);
                    best = Math.Min(best, sim.Ms);
                }
                rows.Add(new { threads, ms = best });
            }
            return rows;
        }

        // The nose profile the scenario asks for (the mod picks it by the kind of ammunition; the lab lets it be chosen, or made up).
        private static ShellProfile ProfileOf(Scenario s)
        {
            switch ((s.Nose ?? "ap").ToLowerInvariant())
            {
                case "legacy": return default(ShellProfile);
                case "aphe": return Ammunition.ProfileOf(AmmoKind.APHE);
                case "he": return Ammunition.ProfileOf(AmmoKind.HE);
                case "custom": return ShellProfile.Procedural(s.NoseLength, s.NoseFullness);
                default: return Ammunition.ProfileOf(AmmoKind.AP);
            }
        }

        private static ImpactSimResult Quick(Scenario s)
        {
            float mass = s.Mass > 0f ? s.Mass : EnergyModel.ShellMassForCaliber(s.Caliber);
            float az = s.Azimuth * (float)(Math.PI / 180.0);
            return ImpactSim.Run(new ImpactSimInput
            {
                CaliberM = s.Caliber * 0.001f, ShellMassKg = mass, SpeedMS = s.Speed, AngleDeg = s.Angle,
                DirU = (float)Math.Cos(az), DirV = (float)Math.Sin(az), ThicknessM = s.Thickness * 0.001f,
                PlateHardness = s.Hardness, PlateBrittleness = s.Brittleness / 100f,
                ResistanceJPerM3 = ArmorFragmentsPlugin.ArmorResistance.Value * 1e7f,
                EnergyScale = s.EnergyScale, Quality = s.Quality, Seed = s.Seed, Profile = ProfileOf(s),
            }, PoolFor(s.Threads));
        }

        private static Scenario Clone(Scenario s)
        {
            return new Scenario
            {
                Thickness = s.Thickness, Hardness = s.Hardness, Brittleness = s.Brittleness, Caliber = s.Caliber, Mass = s.Mass,
                Speed = s.Speed, ShellHardness = s.ShellHardness, Angle = s.Angle, Azimuth = s.Azimuth, Quality = s.Quality,
                Threads = s.Threads, Seed = s.Seed, EnergyScale = s.EnergyScale,
                Nose = s.Nose, NoseLength = s.NoseLength, NoseFullness = s.NoseFullness, Paint = s.Paint,
            };
        }

        // ---- Command line (for trying things without the page) -------------------------------------

        public static int Cli(string[] args)
        {
            string folder = args.Length > 1 ? args[1] : "out";
            Directory.CreateDirectory(folder);
            Scenario s = new Scenario();
            for (int i = 2; i + 1 < args.Length; i += 2)
            {
                string key = args[i].TrimStart('-');
                var property = typeof(Scenario).GetProperty(key, System.Reflection.BindingFlags.IgnoreCase | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (property == null) { Console.WriteLine("unknown " + key); return 2; }
                if (property.PropertyType == typeof(string)) { property.SetValue(s, args[i + 1]); continue; }
                float value = float.Parse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture);
                if (property.PropertyType == typeof(int)) property.SetValue(s, (int)value); else property.SetValue(s, value);
            }
            string json = System.Text.Json.JsonSerializer.Serialize(RunScenario(s, true));
            using (var doc = System.Text.Json.JsonDocument.Parse(json))
            {
                var root = doc.RootElement;
                Console.WriteLine(root.GetProperty("model").ToString());
                Console.WriteLine(root.GetProperty("sim").ToString());
                foreach (var p in root.GetProperty("images").EnumerateObject())
                    File.WriteAllBytes(Path.Combine(folder, p.Name + ".png"), Convert.FromBase64String(p.Value.GetString()));
            }
            return 0;
        }
    }

    // The pictures: shaded relief of a height field, and a cross-section through the middle.
    internal static class Png
    {
        public static string Relief(float[] height, byte[] hole, int n, float cell, float scale)
        {
            int size = (int)(n * scale);
            byte[] rgb = new byte[size * size * 3];
            float lx = -0.5f, ly = 0.6f, lz = 0.62f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float gx = Math.Min(x / scale, n - 1.001f), gy = Math.Min(y / scale, n - 1.001f);
                    int i = (int)gx, j = (int)gy;
                    int cellIndex = j * n + i;
                    int at = ((size - 1 - y) * size + x) * 3;   // the picture's top is the plate's +v
                    if (hole[cellIndex] == 0) { rgb[at] = 24; rgb[at + 1] = 90; rgb[at + 2] = 160; continue; }

                    float h0 = height[cellIndex];
                    float hr = height[j * n + Math.Min(i + 1, n - 1)], hl = height[j * n + Math.Max(i - 1, 0)];
                    float hu = height[Math.Min(j + 1, n - 1) * n + i], hd = height[Math.Max(j - 1, 0) * n + i];
                    float nx = -(hr - hl) * 0.5f / cell, ny = -(hu - hd) * 0.5f / cell;
                    float inv = 1f / (float)Math.Sqrt(nx * nx + ny * ny + 1f);
                    float lam = Math.Max(0f, (nx * lx + ny * ly + lz) * inv);
                    float tone = 40f + 175f * lam;
                    float tint = Math.Max(-1f, Math.Min(1f, h0 / 0.01f));   // dents a bit dark-blue, bumps a bit warm
                    rgb[at] = (byte)Math.Max(0, Math.Min(255, tone + 25f * tint));
                    rgb[at + 1] = (byte)Math.Max(0, Math.Min(255, tone));
                    rgb[at + 2] = (byte)Math.Max(0, Math.Min(255, tone - 25f * tint));
                }
            }
            return Encode(size, size, rgb);
        }

        // The plate as it is along the line through the impact (the row in the middle), seen from the side.
        public static string Section(float[] front, float[] back, byte[] hole, int n, float cell, float thickness, float caliber)
        {
            int w = 640, hgt = 220;
            byte[] rgb = new byte[w * hgt * 3];
            for (int i = 0; i < rgb.Length; i += 3) { rgb[i] = 245; rgb[i + 1] = 247; rgb[i + 2] = 250; }
            int row = n / 2;
            float meters = n * cell;
            float scale = w / meters;                 // pixels per meter, the same both ways
            float mid = hgt * 0.5f;
            for (int x = 0; x < w; x++)
            {
                int i = Math.Min(n - 1, (int)(x / scale / cell));
                if (hole[row * n + i] == 0) continue;
                float top = thickness * 0.5f + front[row * n + i];
                float bottom = -thickness * 0.5f - back[row * n + i];
                int yTop = (int)(mid - top * scale), yBottom = (int)(mid - bottom * scale);
                for (int y = Math.Max(0, yTop); y <= Math.Min(hgt - 1, yBottom); y++)
                {
                    int at = (y * w + x) * 3;
                    rgb[at] = 92; rgb[at + 1] = 104; rgb[at + 2] = 118;
                }
            }
            // The original thickness as thin lines.
            foreach (float z in new[] { thickness * 0.5f, -thickness * 0.5f })
            {
                int y = (int)(mid - z * scale);
                if (y < 0 || y >= hgt) continue;
                for (int x = 0; x < w; x += 4) { int at = (y * w + x) * 3; rgb[at] = 200; rgb[at + 1] = 60; rgb[at + 2] = 60; }
            }
            return Encode(w, hgt, rgb);
        }

        private static string Encode(int w, int h, byte[] rgb)
        {
            using (MemoryStream file = new MemoryStream())
            {
                file.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);
                Action<string, byte[]> chunk = (type, data) =>
                {
                    byte[] length = BitConverter.GetBytes(data.Length); Array.Reverse(length); file.Write(length, 0, 4);
                    byte[] name = System.Text.Encoding.ASCII.GetBytes(type); file.Write(name, 0, 4); file.Write(data, 0, data.Length);
                    uint crc = 0xFFFFFFFF;
                    foreach (byte b in name) crc = Crc(crc, b);
                    foreach (byte b in data) crc = Crc(crc, b);
                    byte[] sum = BitConverter.GetBytes(~crc); Array.Reverse(sum); file.Write(sum, 0, 4);
                };
                byte[] header = new byte[13];
                byte[] ww = BitConverter.GetBytes(w); Array.Reverse(ww); Array.Copy(ww, 0, header, 0, 4);
                byte[] hh = BitConverter.GetBytes(h); Array.Reverse(hh); Array.Copy(hh, 0, header, 4, 4);
                header[8] = 8; header[9] = 2;
                chunk("IHDR", header);
                MemoryStream raw = new MemoryStream();
                for (int y = 0; y < h; y++) { raw.WriteByte(0); raw.Write(rgb, y * w * 3, w * 3); }
                MemoryStream compressed = new MemoryStream();
                using (ZLibStream z = new ZLibStream(compressed, CompressionLevel.Fastest, true)) { byte[] bytes = raw.ToArray(); z.Write(bytes, 0, bytes.Length); }
                chunk("IDAT", compressed.ToArray());
                chunk("IEND", new byte[0]);
                return Convert.ToBase64String(file.ToArray());
            }
        }

        private static uint Crc(uint c, byte b)
        {
            c ^= b;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            return c;
        }
    }
}
