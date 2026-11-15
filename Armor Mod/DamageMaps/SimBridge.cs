using System;
using ArmorFragments.Sim;
using UnityEngine;

namespace ArmorFragments
{
    // Between the impact simulation and the plate's maps: what the simulation is told, and how its result (a coarse grid of
    // heights and holes around the impact) is laid into the plate's tiles.
    internal static class SimBridge
    {
        // Does the impact get a simulation (a shell's impact, with what the simulation needs)?
        public static bool Wants(PlateImpact impact)
        {
            if (impact.Kind != DamageKind.Blocked && impact.Kind != DamageKind.Penetrated && impact.Kind != DamageKind.Ricochet) return false;
            return impact.ShellMass > 0f && impact.Speed > 1f && impact.Caliber > 0f;
        }

        public static ImpactSimInput InputFor(PlateImpact impact, int quality, int timeLimitMs, float resistanceJPerM3)
        {
            return new ImpactSimInput
            {
                CaliberM = impact.Caliber,
                ShellMassKg = impact.ShellMass,
                SpeedMS = impact.Speed,
                AngleDeg = impact.Angle,
                DirU = impact.DirU,
                DirV = impact.DirV,
                ThicknessM = impact.Thickness,
                PlateHardness = impact.PlateHardness,
                PlateBrittleness = impact.PlateBrittleness,
                ResistanceJPerM3 = resistanceJPerM3,
                Ricochet = impact.Kind == DamageKind.Ricochet,
                KeptEnergy = impact.KeptEnergy,
                EnergyScale = 1f,
                Quality = quality,
                Seed = impact.Seed,
                TimeLimitMs = timeLimitMs,
            };
        }

        // The simulation must agree with the energy model about the outcome (the model decides the game: a shell the model
        // lets through must make a hole, one it stops must not). If not, the quick marks are used instead.
        public static bool Agrees(DamageKind kind, ImpactSimResult result)
        {
            bool hole = result.Perforated || result.HoleRadiusM > 0f;
            if (kind == DamageKind.Penetrated) return hole;
            if (kind == DamageKind.Blocked) return !hole;
            return true;
        }

        // Lays the simulation's grid into the plate's face tiles: the front's heights on the face the shell hit, the back's on
        // the other face, and the holes through both. The grid is coarser than the tile's pixels, so it is sampled smoothly.
        public static void Apply(PlateCpu cpu, PlateImpact impact, ImpactSimResult sim)
        {
            int entry = impact.Front ? 0 : 1;
            PlateCpu.Face faceIn = cpu.Get(entry), faceOut = cpu.Get(1 - entry);

            float half = sim.Size * 0.5f;
            int x0 = Mathf.Max(PlateCpu.Border, Mathf.FloorToInt(PlateCpu.Border + (impact.U - half) * cpu.PxU));
            int x1 = Mathf.Min(cpu.Width - PlateCpu.Border, Mathf.CeilToInt(PlateCpu.Border + (impact.U + half) * cpu.PxU));
            int y0 = Mathf.Max(PlateCpu.Border, Mathf.FloorToInt(PlateCpu.Border + (impact.V - half) * cpu.PxV));
            int y1 = Mathf.Min(cpu.Height - PlateCpu.Border, Mathf.CeilToInt(PlateCpu.Border + (impact.V + half) * cpu.PxV));
            if (x1 <= x0 || y1 <= y0) return;

            int n = sim.N;
            bool anyHole = false;
            for (int y = y0; y < y1; y++)
            {
                for (int x = x0; x < x1; x++)
                {
                    float u = (x + 0.5f - PlateCpu.Border) / cpu.PxU;
                    float v = (y + 0.5f - PlateCpu.Border) / cpu.PxV;
                    float gx = (u - impact.U) / sim.Cell + (n - 1) * 0.5f;
                    float gy = (v - impact.V) / sim.Cell + (n - 1) * 0.5f;
                    if (gx < 0f || gy < 0f || gx > n - 1 || gy > n - 1) continue;

                    int i = Mathf.Min((int)gx, n - 2), j = Mathf.Min((int)gy, n - 2);
                    float fx = gx - i, fy = gy - j;
                    int idx = y * cpu.Width + x;
                    faceIn.Height[idx] += Sample(sim.Front, n, i, j, fx, fy);
                    faceOut.Height[idx] += Sample(sim.Back, n, i, j, fx, fy);
                    if (SampleHole(sim.Hole, n, i, j, fx, fy) < 0.5f)
                    {
                        faceIn.Mask[idx] = 0;
                        faceOut.Mask[idx] = 0;
                        anyHole = true;
                    }
                }
            }
            PlateStamps.Dirty(faceIn, x0, y0, x1, y1, anyHole);
            PlateStamps.Dirty(faceOut, x0, y0, x1, y1, anyHole);
        }

        private static float Sample(float[] grid, int n, int i, int j, float fx, float fy)
        {
            float a = grid[j * n + i], b = grid[j * n + i + 1], c = grid[(j + 1) * n + i], d = grid[(j + 1) * n + i + 1];
            return (a * (1f - fx) + b * fx) * (1f - fy) + (c * (1f - fx) + d * fx) * fy;
        }

        private static float SampleHole(byte[] grid, int n, int i, int j, float fx, float fy)
        {
            float a = grid[j * n + i], b = grid[j * n + i + 1], c = grid[(j + 1) * n + i], d = grid[(j + 1) * n + i + 1];
            return ((a * (1f - fx) + b * fx) * (1f - fy) + (c * (1f - fx) + d * fx) * fy) / 255f;
        }
    }
}
