using System;
using System.Collections.Generic;
using System.Diagnostics;
using ArmorFragments.Sim;

// The fused relaxation (one launch a step) against the 8-colors-a-launch way and the CPU, same inputs and seed.
internal static class Program
{
    private sealed class Case
    {
        public string Name;
        public float Caliber, Mass, Speed, Angle, Thickness, Hardness, Brittle;
        public int Quality;
        public bool Ricochet;
    }

    private static ImpactSimInput Input(Case c, int seed)
    {
        return new ImpactSimInput
        {
            CaliberM = c.Caliber, ShellMassKg = c.Mass, SpeedMS = c.Speed, AngleDeg = c.Angle, DirU = 1f, DirV = 0f,
            ThicknessM = c.Thickness, PlateHardness = c.Hardness, PlateBrittleness = c.Brittle,
            ResistanceJPerM3 = 600f * 1e7f, Ricochet = c.Ricochet, KeptEnergy = 0.3f, EnergyScale = 1f, Quality = c.Quality, Seed = seed,
        };
    }

    private static string Line(string label, ImpactSimResult r, double ms)
    {
        return string.Format("   {0,-8}{1,6:F1} ms | perforated {2,-5} | depth {3,6:F1} mm | hole r {4,6:F1} mm | energy left {5,5:F3} | steps {6,3} | charged {7,5} | broken {8,5}",
            label, ms, r.Perforated, r.DepthM * 1000f, r.HoleRadiusM * 1000f, r.EnergyLeft, r.Steps, r.Charged, r.Broken);
    }
    private static string label_(string s) { return s; }

    private static void Diff(string label, ImpactSimResult a, ImpactSimResult b)
    {
        double sumF = 0, maxF = 0, sumB = 0; int cells = a.Front.Length; int holeDiff = 0; int bigCells = 0;
        for (int i = 0; i < cells; i++)
        {
            double d = Math.Abs(a.Front[i] - b.Front[i]);
            sumF += d; if (d > maxF) maxF = d; if (d > 0.002) bigCells++;
            sumB += Math.Abs(a.Back[i] - b.Back[i]);
            if (a.Hole[i] != b.Hole[i]) holeDiff++;
        }
        Console.WriteLine(string.Format("   {0,-18} front mean {1:F3} mm (max {2:F1} mm, {3} cells over 2 mm), back mean {4:F3} mm, hole mask differs in {5} of {6} cells",
            label, sumF / cells * 1000.0, maxF * 1000.0, bigCells, sumB / cells * 1000.0, holeDiff, cells));
    }

    private static int Main()
    {
        try { return Run(); }
        catch (Exception e) { Console.WriteLine("EXCEPTION: " + e); return 3; }
    }

    private static int Run()
    {
        string error;
        ClSolver.AllowFused = true;
        ClSolver gpu = ClSolver.Create(out error);
        if (gpu == null) { Console.WriteLine("no GPU solver: " + error); return 1; }
        Console.WriteLine("GPU solver on " + gpu.DeviceName + ", local memory " + gpu.LocalMemBytes + " bytes, fused ready: " + gpu.FusedReady + " " + gpu.FusedNote + "\n");
        if (!gpu.FusedReady) return 2;

        int workers = Math.Max(1, Environment.ProcessorCount - 2) - 1;
        SimPool pool = new SimPool(workers);

        List<Case> cases = new List<Case>
        {
            new Case { Name = "50 mm, 11 mm plate, head-on, 96", Caliber = 0.05f, Mass = 1.67f, Speed = 750f, Angle = 0f, Thickness = 0.011f, Hardness = 100f, Brittle = 0.5f, Quality = 2 },
            new Case { Name = "50 mm, 20 mm plate, 0 deg, 96", Caliber = 0.05f, Mass = 1.67f, Speed = 750f, Angle = 0f, Thickness = 0.020f, Hardness = 100f, Brittle = 0.5f, Quality = 2 },
            new Case { Name = "50 mm, 20 mm plate, 47 deg, 96", Caliber = 0.05f, Mass = 1.67f, Speed = 750f, Angle = 47f, Thickness = 0.020f, Hardness = 100f, Brittle = 0.5f, Quality = 2 },
            new Case { Name = "50 mm, 60 mm plate (blocked), 96", Caliber = 0.05f, Mass = 1.67f, Speed = 750f, Angle = 0f, Thickness = 0.060f, Hardness = 100f, Brittle = 0.5f, Quality = 2 },
            new Case { Name = "75 mm, 40 mm hard plate, 30 deg, 96", Caliber = 0.075f, Mass = 6.3f, Speed = 800f, Angle = 30f, Thickness = 0.040f, Hardness = 300f, Brittle = 0.2f, Quality = 2 },
            new Case { Name = "50 mm, 20 mm plate, 78 deg RICOCHET, 96", Caliber = 0.05f, Mass = 1.67f, Speed = 750f, Angle = 78f, Thickness = 0.020f, Hardness = 100f, Brittle = 0.5f, Quality = 2, Ricochet = true },
            new Case { Name = "75 mm, 30 mm plate, 82 deg RICOCHET, 96", Caliber = 0.075f, Mass = 6.3f, Speed = 800f, Angle = 82f, Thickness = 0.030f, Hardness = 200f, Brittle = 0.3f, Quality = 2, Ricochet = true },
            new Case { Name = "50 mm, 20 mm plate, 0 deg, 64", Caliber = 0.05f, Mass = 1.67f, Speed = 750f, Angle = 0f, Thickness = 0.020f, Hardness = 100f, Brittle = 0.5f, Quality = 1 },
            new Case { Name = "50 mm, 20 mm plate, 0 deg, 40", Caliber = 0.05f, Mass = 1.67f, Speed = 750f, Angle = 0f, Thickness = 0.020f, Hardness = 100f, Brittle = 0.5f, Quality = 0 },
            new Case { Name = "120 mm, 80 mm plate, 20 deg, 96", Caliber = 0.12f, Mass = 20f, Speed = 900f, Angle = 20f, Thickness = 0.080f, Hardness = 150f, Brittle = 0.4f, Quality = 2 },
            new Case { Name = "50 mm, 30 mm plate, 20 deg, 80 columns", Caliber = 0.05f, Mass = 1.67f, Speed = 700f, Angle = 20f, Thickness = 0.030f, Hardness = 100f, Brittle = 0.5f, Quality = 1 },
        };

        gpu.Fused = false; ImpactSim.Run(Input(cases[8], 1), pool, gpu);
        gpu.Fused = true; ImpactSim.Run(Input(cases[8], 1), pool, gpu);

        double totalOld = 0, totalNew = 0;
        foreach (Case c in cases)
        {
            ImpactSimInput input = Input(c, 12345);
            if (c.Name.Contains("80 columns")) input.Columns = 80;
            Console.WriteLine(c.Name);

            Stopwatch w = Stopwatch.StartNew();
            ImpactSimResult cpu = ImpactSim.Run(input, pool);
            double cpuMs = w.Elapsed.TotalMilliseconds;

            ImpactSimResult plain = null, fused = null; double plainMs = 1e9, fusedMs = 1e9;
            for (int rep = 0; rep < 4; rep++)
            {
                gpu.Fused = false;
                w.Restart(); plain = ImpactSim.Run(input, pool, gpu); plainMs = Math.Min(plainMs, w.Elapsed.TotalMilliseconds);
                gpu.Fused = true;
                w.Restart(); fused = ImpactSim.Run(input, pool, gpu); fusedMs = Math.Min(fusedMs, w.Elapsed.TotalMilliseconds);
            }
            totalOld += plainMs; totalNew += fusedMs;

            Console.WriteLine(Line("CPU", cpu, cpuMs));
            Console.WriteLine(Line("8 colors", plain, plainMs));
            Console.WriteLine(Line("fused", fused, fusedMs));
            Console.WriteLine(string.Format("   fused is x{0:F2} faster than the 8-colors way (best of 4 each)", plainMs / fusedMs));
            Diff("fused vs 8-colors", plain, fused);
            Diff("fused vs CPU", cpu, fused);
            Diff("8-colors vs CPU", cpu, plain);
            Console.WriteLine();
        }
        Console.WriteLine(string.Format("Total: 8 colors {0:F0} ms, fused {1:F0} ms (x{2:F2})", totalOld, totalNew, totalOld / totalNew));
        gpu.Dispose();
        return 0;
    }
}
