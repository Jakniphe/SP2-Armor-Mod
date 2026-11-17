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
                DeflectionDeg = impact.Kind == DamageKind.Penetrated ? impact.Deflection : 0f,
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
        //   cracks:      the torn links make thin grooves in the surfaces
        //   smoothEdges: the edge of a hole follows where the material runs out between the columns, a little ragged, not the columns' square steps
        public static void Apply(PlateCpu cpu, PlateImpact impact, ImpactSimResult sim, bool cracks = true, bool smoothEdges = true)
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
            bool grooves = cracks && sim.CrackFront != null && sim.CrackBack != null;
            bool smooth = smoothEdges && sim.Solid != null;
            float[] distance = smooth ? SignedDistance(sim) : null;   // to the hole's edge, meters, + on the metal (null = no hole)
            float pixel = 1f / Mathf.Max(cpu.PxU, cpu.PxV);
            float grooveDepth = Mathf.Clamp(0.04f * impact.Caliber, 0.0004f, 0.002f);   // meters at a full crack
            float ragged = smooth ? 0.10f + 0.20f * Mathf.Clamp01(impact.PlateBrittleness) : 0f;
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
                    bool hole;
                    if (smooth)
                    {
                        // The edge is written as a distance: 128 = on the edge, 128 more or less for a pixel's width of metal or of hole. The
                        // pixels near the edge then say where it is to a fraction of a pixel (the geometry and the hole map both read them
                        // smoothly), and the ragged edge is the distance moved a little by the noise.
                        hole = false;
                        if (distance != null)
                        {
                            float d = Sample(distance, n, i, j, fx, fy) - ragged * Noise(x, y, impact.Seed) * sim.Cell;
                            float value = Mathf.Clamp(128f + d / pixel * 128f, 0f, 255f);
                            if (value < 255f)
                            {
                                byte b = (byte)value;
                                if (b < faceIn.Mask[idx]) faceIn.Mask[idx] = b;
                                if (b < faceOut.Mask[idx]) faceOut.Mask[idx] = b;
                            }
                            hole = value < 128f;
                        }
                    }
                    else
                    {
                        hole = SampleHole(sim.Hole, n, i, j, fx, fy) < 0.5f;
                        if (hole) { faceIn.Mask[idx] = 0; faceOut.Mask[idx] = 0; }
                    }
                    if (hole) anyHole = true;
                    else if (grooves)
                    {
                        faceIn.Height[idx] -= grooveDepth * Sample(sim.CrackFront, n, i, j, fx, fy);
                        faceOut.Height[idx] -= grooveDepth * Sample(sim.CrackBack, n, i, j, fx, fy);
                    }
                }
            }
            PlateStamps.Dirty(faceIn, x0, y0, x1, y1, anyHole);
            PlateStamps.Dirty(faceOut, x0, y0, x1, y1, anyHole);
        }

        // The distance, in meters, from the middle of each of the simulation's columns to the edge of the hole (positive on the metal, negative
        // in the hole): half a column less than the distance to the nearest column of the other kind. Null if there is no hole. Looks 6 columns
        // out; beyond that it is that far.
        private static float[] SignedDistance(ImpactSimResult sim)
        {
            int n = sim.N;
            bool[] hole = new bool[n * n];
            bool any = false;
            for (int j = 1; j < n - 1; j++)
                for (int i = 1; i < n - 1; i++)
                    if (sim.Solid[j * n + i] < 0.5f) { hole[j * n + i] = true; any = true; }
            if (!any) return null;

            const int reach = 6;
            float[] distance = new float[n * n];
            for (int j = 0; j < n; j++)
            {
                for (int i = 0; i < n; i++)
                {
                    bool mine = hole[j * n + i];
                    int best = reach * reach * 2 + 1;
                    for (int dj = -reach; dj <= reach; dj++)
                    {
                        int jj = j + dj;
                        if (jj < 0 || jj >= n) continue;
                        for (int di = -reach; di <= reach; di++)
                        {
                            int ii = i + di;
                            if (ii < 0 || ii >= n || hole[jj * n + ii] == mine) continue;
                            int d2 = di * di + dj * dj;
                            if (d2 < best) best = d2;
                        }
                    }
                    float cells = best > reach * reach * 2 ? reach : Mathf.Sqrt(best) - 0.5f;
                    distance[j * n + i] = (mine ? -cells : cells) * sim.Cell;
                }
            }
            return distance;
        }

        // Smooth noise in -1..1 on a lattice of 3 pixels (the same for the same seed): the ragged edge of torn metal.
        private static float Noise(float x, float y, int seed)
        {
            float gx = x / 3f, gy = y / 3f;
            int ix = Mathf.FloorToInt(gx), iy = Mathf.FloorToInt(gy);
            float tx = gx - ix, ty = gy - iy;
            tx = tx * tx * (3f - 2f * tx);
            ty = ty * ty * (3f - 2f * ty);
            float a = Hash(ix, iy, seed), b = Hash(ix + 1, iy, seed), c = Hash(ix, iy + 1, seed), d = Hash(ix + 1, iy + 1, seed);
            return (a * (1f - tx) + b * tx) * (1f - ty) + (c * (1f - tx) + d * tx) * ty;
        }

        private static float Hash(int x, int y, int seed)
        {
            unchecked
            {
                uint h = (uint)(x * 374761393 + y * 668265263 + seed * 1442695041);
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                return (h & 0xFFFF) / 32767.5f - 1f;
            }
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
