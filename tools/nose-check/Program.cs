using System;
using System.Collections.Generic;
using System.Diagnostics;
using ArmorFragments;
using ArmorFragments.Sim;

// Each nose profile on the processor and on the card, same inputs and seed. Checks:
//   1. the card agrees with the processor (the shape of the dent and the hole: mean height difference, hole cells that differ);
//   2. the nose does not change what the shell pays: for one shot, every nose gives the same outcome and nearly the same energy left.
internal static class Program
{
    private sealed class Case
    {
        public string Name;
        public float Caliber, Mass, Speed, Angle, Thickness, Hardness, Brittle;
        public bool Ricochet;
    }

    private static ImpactSimInput Input(Case c, ShellProfile profile)
    {
        return new ImpactSimInput
        {
            CaliberM = c.Caliber, ShellMassKg = c.Mass, SpeedMS = c.Speed, AngleDeg = c.Angle, DirU = 1f, DirV = 0f,
            ThicknessM = c.Thickness, PlateHardness = c.Hardness, PlateBrittleness = c.Brittle,
            ResistanceJPerM3 = 600f * 1e7f, Ricochet = c.Ricochet, KeptEnergy = 0.3f, EnergyScale = 1f, Quality = 1, Seed = 777,
            Profile = profile,
        };
    }

    private static double[] Diff(ImpactSimResult a, ImpactSimResult b)
    {
        double sumF = 0, sumB = 0, maxF = 0; int holeDiff = 0;
        int cells = a.Front.Length;
        for (int i = 0; i < cells; i++)
        {
            double d = Math.Abs(a.Front[i] - b.Front[i]);
            sumF += d; if (d > maxF) maxF = d;
            sumB += Math.Abs(a.Back[i] - b.Back[i]);
            if (a.Hole[i] != b.Hole[i]) holeDiff++;
        }
        return new[] { sumF / cells * 1000.0, sumB / cells * 1000.0, maxF * 1000.0, holeDiff };
    }

    private static int Main()
    {
        try { return Run(); }
        catch (Exception e) { Console.WriteLine("EXCEPTION: " + e); return 3; }
    }

    private static int Run()
    {
        string error;
        ClSolver gpu = ClSolver.Create(out error);
        if (gpu == null) { Console.WriteLine("no GPU solver: " + error); return 1; }
        Console.WriteLine("GPU solver on " + gpu.DeviceName + "\n");
        SimPool pool = new SimPool(Math.Max(1, Environment.ProcessorCount - 2) - 1);

        List<KeyValuePair<string, ShellProfile>> noses = new List<KeyValuePair<string, ShellProfile>>
        {
            new KeyValuePair<string, ShellProfile>("legacy hemisphere", default(ShellProfile)),
            new KeyValuePair<string, ShellProfile>("AP ogive", ShellProfile.Ogive),
            new KeyValuePair<string, ShellProfile>("APHE capped", ShellProfile.Capped),
            new KeyValuePair<string, ShellProfile>("HE blunt", ShellProfile.Blunt),
            new KeyValuePair<string, ShellProfile>("cone (custom p=1)", ShellProfile.Procedural(2.2f, 1f)),
        };
        List<Case> cases = new List<Case>
        {
            new Case { Name = "50 mm into 20 mm, head-on (goes through)", Caliber = 0.05f, Mass = 1.67f, Speed = 750f, Angle = 0f, Thickness = 0.020f, Hardness = 100f, Brittle = 0.5f },
            new Case { Name = "50 mm into 60 mm, head-on (blocked)", Caliber = 0.05f, Mass = 1.67f, Speed = 750f, Angle = 0f, Thickness = 0.060f, Hardness = 100f, Brittle = 0.5f },
            new Case { Name = "75 mm into 40 mm hard plate, 30 deg", Caliber = 0.075f, Mass = 6.3f, Speed = 800f, Angle = 30f, Thickness = 0.040f, Hardness = 300f, Brittle = 0.2f },
            new Case { Name = "50 mm into 20 mm, 78 deg (ricochet)", Caliber = 0.05f, Mass = 1.67f, Speed = 750f, Angle = 78f, Thickness = 0.020f, Hardness = 100f, Brittle = 0.5f, Ricochet = true },
        };

        int bad = 0;
        foreach (Case c in cases)
        {
            Console.WriteLine(c.Name);
            ImpactSimResult reference = null;
            foreach (KeyValuePair<string, ShellProfile> nose in noses)
            {
                ImpactSimInput input = Input(c, nose.Value);
                Stopwatch w = Stopwatch.StartNew();
                ImpactSimResult cpu = ImpactSim.Run(input, pool);
                double cpuMs = w.Elapsed.TotalMilliseconds;
                w.Restart();
                ImpactSimResult card = ImpactSim.Run(input, pool, gpu);
                double cardMs = w.Elapsed.TotalMilliseconds;
                double[] d = Diff(cpu, card);
                Console.WriteLine(string.Format(
                    "   {0,-20} CPU {1,6:F0} ms | card {2,5:F0} ms | perforated {3,-5} | depth {4,5:F1} mm | hole r {5,5:F1} mm | left {6:F3} | charged {7,5} | card vs CPU: front {8:F3} mm, back {9:F3} mm, worst {10:F1} mm, hole cells {11}",
                    nose.Key, cpuMs, cardMs, cpu.Perforated, cpu.DepthM * 1000f, cpu.HoleRadiusM * 1000f, cpu.EnergyLeft, cpu.Charged, d[0], d[1], d[2], (int)d[3]));
                if (d[0] > 0.3 || d[1] > 0.3) { Console.WriteLine("      ^ the card and the processor disagree on the shape"); bad++; }
                if (card.Perforated != cpu.Perforated) { Console.WriteLine("      ^ the card and the processor disagree on the outcome"); bad++; }

                if (reference == null) { reference = cpu; continue; }
                if (cpu.Perforated != reference.Perforated) { Console.WriteLine("      ^ the nose changed the outcome"); bad++; }
                if (Math.Abs(cpu.EnergyLeft - reference.EnergyLeft) > 0.03f) { Console.WriteLine("      ^ the nose changed the energy left by more than 0.03"); bad++; }
            }
            Console.WriteLine();
        }
        Console.WriteLine(bad == 0 ? "ALL CHECKS PASSED" : bad + " CHECK(S) FAILED");
        gpu.Dispose();
        return bad == 0 ? 0 : 1;
    }
}
