using System;

namespace ArmorFragments.Sim
{
    // What the simulation is told about one impact. Everything comes from the energy model's own numbers.
    internal struct ImpactSimInput
    {
        public float CaliberM;         // the shell's diameter
        public float ShellMassKg;
        public float SpeedMS;          // at the plate
        public float AngleDeg;         // from the plate's normal (0 = head-on)
        public float DirU, DirV;       // the way the shell is going along the plate (any length; 0,0 = along u)
        public float ThicknessM;       // the plate's thickness
        public float PlateHardness;    // percent, 100 = reference: how late the metal gives way
        public float PlateBrittleness; // 0..1: how early it cracks
        public float ResistanceJPerM3; // the energy it takes to push a cubic meter of plate out of the way
        public bool Ricochet;          // the model says the shell bounces: it only skims
        public float KeptEnergy;       // ricochet: the share of its energy the shell keeps
        public float EnergyScale;      // 1 = the shell's real energy (a lab knob)
        public int Quality;            // 0 low, 1 medium, 2 high
        public int Seed;
        public int TimeLimitMs;        // give up after this long (0 = no limit)
    }

    internal sealed class ImpactSimResult
    {
        public int N;                  // columns per side
        public float Cell;             // meters per column
        public float Size;             // meters, the whole patch
        public float[] Front;          // height of the front surface over its old place (meters, + = out)
        public float[] Back;           // the same for the back surface (+ = out, the way a shell comes out)
        public byte[] Hole;            // 0 where there is no material, 255 elsewhere
        public bool TimedOut;          // it took too long and was stopped: nothing else in the result is valid
        public bool Perforated;        // the shell got through
        public float DepthM;           // the deepest dent (the whole thickness if it is through)
        public float HoleRadiusM;      // of the hole, if any
        public float EnergyLeft;       // share of the shell's energy that was not spent
        public int Particles;
        public int Steps;
        public int Broken;             // links that tore
        public int Charged;            // particles the shell displaced (each cost it ResistanceJPerM3 x its volume)
        public double Ms;
    }

    // A SIMPLIFIED IMPACT SIMULATION, MADE TO LOOK RIGHT, NOT TO BE ENGINEERING-ACCURATE.
    //
    // A small patch of the plate is a 3D lattice of particles joined by links (6 along the axes and 12 along the
    // diagonals of its faces). The shell is a rigid capsule that moves through it. Position-based dynamics, with
    // the particles taken color by color so that each color can be worked out on all threads at once without any
    // two particles that are linked changing at the same time (so the result does not depend on the thread count).
    //
    //  - The shell pushes particles out of its way. Every particle it displaces costs it ResistanceJPerM3 x the
    //    particle's volume (once): the same price per volume the energy model charges (ArmorResistance), so a shell
    //    that has the energy for the plug goes through and one that has not stops, as the model says.
    //  - A link that stretches past the yield strain stays stretched (plastic flow: lips, bulges). Past the break
    //    strain it breaks (cracks, petals, a torn exit). A brittle plate breaks early, a ductile one flows.
    //  - After the shell has gone (or stopped) the lattice settles; the surface heights of the front and back and the
    //    places where nothing is left (the hole) are read from where the particles ended up.
    internal sealed class ImpactSim
    {
        private const int Links = 18;
        private static readonly int[] Ox = { 1, -1, 0, 0, 0, 0, 1, 1, -1, -1, 1, 1, -1, -1, 0, 0, 0, 0 };
        private static readonly int[] Oy = { 0, 0, 1, -1, 0, 0, 1, -1, 1, -1, 0, 0, 0, 0, 1, 1, -1, -1 };
        private static readonly int[] Oz = { 0, 0, 0, 0, 1, -1, 0, 0, 0, 0, 1, -1, 1, -1, 1, -1, 1, -1 };

        // ---- Grid ---------------------------------------------------------------------------------
        private int n, k, count;
        private float h, hz, t, size;
        private float[] px, py, pz, qx, qy, qz, vx, vy, vz;
        private float[] ox, oy, oz;    // where each particle started (the shell is charged by what it sweeps through)
        private float[] rest;          // count * Links; 0 or less = no link (or broken)
        private float[] rest0;         // the rest lengths the links started with (plastic flow lengthens them)
        private int[] nbr;             // count * Links
        private byte[] fixedFlag, charged;
        private int[][] colors;
        private float[] chunkCharged;

        // ---- Shell --------------------------------------------------------------------------------
        private float cx, cy, cz;      // the center of the capsule's nose sphere
        private float ax, ay, az;      // its direction
        private float tx0, ty0, tz0;   // where its tip started
        private float ax0, ay0, az0;   // the direction it started with (the tunnel it digs is along this)
        private float tipS;            // how far its tip has gone along that line
        private float radius, bodyLength;

        // ---- Material -----------------------------------------------------------------------------
        private float yieldStrain, breakStrain, ductility, plasticRate;
        private float particleRadius;
        private float deepestTip;      // the lowest the shell's tip has been
        private bool backOpen;         // the shell's tip has reached the back of the plate: only then may the back layer tear
        private float breakReach2;     // links only break close to the shell (squared); far away the plate bends but does not tear
        private float[] baseTop, baseBottom;
        private int[] baseCover;

        public static ImpactSimResult Run(ImpactSimInput input, SimPool pool)
        {
            ImpactSim sim = new ImpactSim();
            return sim.Execute(input, pool);
        }

        private ImpactSimResult Execute(ImpactSimInput input, SimPool pool)
        {
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();

            // ---- The patch --------------------------------------------------------------------
            radius = Math.Max(input.CaliberM * 0.5f, 0.002f);
            t = Math.Max(input.ThicknessM, 0.002f);
            n = input.Quality <= 0 ? 40 : (input.Quality == 1 ? 64 : 96);
            size = Math.Max(radius * 2f * 7f, 0.1f);
            h = size / n;
            k = Math.Max(2, Math.Min(14, (int)MathF.Round(t / h)));
            hz = t / k;
            count = n * n * k;
            particleRadius = 0.35f * Math.Min(h, hz);
            bodyLength = radius * 2.5f;

            float hardness = Math.Max(input.PlateHardness, 10f) / 100f;
            float brittle = Math.Min(Math.Max(input.PlateBrittleness, 0f), 1f);
            yieldStrain = 0.02f * MathF.Sqrt(hardness);
            float give = Math.Min(Math.Max(1f / MathF.Sqrt(hardness), 0.6f), 1.3f);   // a harder plate gives a little less
            breakStrain = (1.2f + (0.12f - 1.2f) * brittle) * give;   // how much a link can be stretched at once
            ductility = (1.5f + (0.10f - 1.5f) * brittle) * give;     // how much it can be lengthened by flowing, in all
            plasticRate = 0.25f;

            Allocate();
            Build(input.Seed);
            Surfaces(out baseTop, out baseBottom, out baseCover);   // the plate before the shot (its lattice is not perfectly smooth)

            // ---- The shell --------------------------------------------------------------------
            float angle = Math.Min(Math.Max(input.AngleDeg, 0f), 85f) * (float)(Math.PI / 180.0);
            float du = input.DirU, dv = input.DirV;
            float dl = MathF.Sqrt(du * du + dv * dv);
            if (dl < 1e-4f) { du = 1f; dv = 0f; } else { du /= dl; dv /= dl; }
            ax = MathF.Sin(angle) * du;
            ay = MathF.Sin(angle) * dv;
            az = -MathF.Cos(angle);

            // The tip starts a few cells above the point of impact on the front surface.
            float start = radius + 3f * h;
            cx = -ax * start;
            cy = -ay * start;
            cz = t * 0.5f - az * start;
            ax0 = ax; ay0 = ay; az0 = az;
            deepestTip = t * 0.5f;
            tx0 = cx + ax * radius; ty0 = cy + ay * radius; tz0 = cz + az * radius;

            float speed0 = Math.Max(input.SpeedMS, 1f);
            double energy0 = 0.5 * input.ShellMassKg * speed0 * speed0 * Math.Max(input.EnergyScale, 0.0001f);
            double energy = energy0;
            float cellVolume = h * h * hz;
            double costPerParticle = input.ResistanceJPerM3 * cellVolume;

            float dt = 0.4f * h / speed0;
            int iterations = input.Quality <= 0 ? 4 : 5;
            float depthLimit = Math.Min(0.45f * radius, 0.5f * t);
            bool bounced = false, skimming = false;
            float skimLeft = 0f;
            float sin = MathF.Sin(angle);
            float skimLength = Math.Min(Math.Max(radius * (2f + 8f * sin * sin), radius * 2f), size * 0.4f);
            breakReach2 = (3f * radius) * (3f * radius);
            float keptSpeedFactor = MathF.Sqrt(Math.Min(Math.Max(input.KeptEnergy, 0.02f), 1f));

            bool shellActive = true;
            bool stopped = false;
            bool exited = false;
            int steps = 0, settle = 0, stoppedSteps = 0;
            const int MaxActive = 600;
            const int SettleSteps = 24;

            while (steps < MaxActive + SettleSteps)
            {
                if (input.TimeLimitMs > 0 && watch.ElapsedMilliseconds > input.TimeLimitMs)
                    return new ImpactSimResult { TimedOut = true, Steps = steps, Particles = count, Ms = watch.Elapsed.TotalMilliseconds };

                // Where the shell is this step.
                float speed = 0f;
                if (shellActive)
                {
                    if (energy <= 0.0 || (energy < energy0 * 0.0004)) { stopped = true; energy = 0.0; }
                    else
                    {
                        speed = (float)Math.Sqrt(2.0 * energy / input.ShellMassKg);
                        speed = Math.Min(speed, speed0 * 1.0001f);
                    }
                    cx += ax * speed * dt;
                    cy += ay * speed * dt;
                    cz += az * speed * dt;

                    float tipZ = cz + az * radius;
                    tipS = (cx + ax * radius - tx0) * ax0 + (cy + ay * radius - ty0) * ay0 + (tipZ - tz0) * az0;
                    // A shell that ricochets digs in to a shallow depth, skims along the plate for a while (the groove), then leaves.
                    if (input.Ricochet && !bounced && !skimming && (t * 0.5f - tipZ) >= depthLimit)
                    {
                        skimming = true;
                        float lateral = MathF.Sqrt(ax * ax + ay * ay);
                        if (lateral < 1e-4f) { ax = du; ay = dv; lateral = 1f; }
                        ax /= lateral; ay /= lateral; az = 0f;
                        skimLeft = skimLength;
                    }
                    if (skimming)
                    {
                        skimLeft -= speed * dt;
                        if (skimLeft <= 0f)
                        {
                            skimming = false;
                            bounced = true;
                            az = 0.5f;                     // it turns up and leaves
                            float norm = MathF.Sqrt(ax * ax + ay * ay + az * az);
                            ax /= norm; ay /= norm; az /= norm;
                            energy *= input.KeptEnergy;
                        }
                    }
                    if (tipZ <= -t * 0.5f + hz) backOpen = true;
                    if (tipZ < deepestTip) deepestTip = tipZ;
                    if (az < 0f && tipZ < -t * 0.5f - radius * 1.2f) { exited = true; shellActive = false; }
                    if (az > 0f && bounced && tipZ > t * 0.5f + radius * 1.5f) { shellActive = false; }
                    if (stopped)
                    {
                        // It stays where it is, in the way, for a moment while the plate takes up the blow; then it is gone.
                        shellActive = ++stoppedSteps <= 8;
                    }
                    if (steps >= MaxActive) shellActive = false;
                }
                else
                {
                    settle++;
                    if (settle > SettleSteps) break;
                }

                bool hasShell = shellActive;
                float damp = shellActive ? 0.995f : 0.85f;

                // ---- Predict, and push the particles out of the shell's way ------------------
                int chunks = (count + 2047) / 2048;
                if (chunkCharged == null || chunkCharged.Length < chunks) chunkCharged = new float[chunks];
                Array.Clear(chunkCharged, 0, chunkCharged.Length);
                float dtLocal = dt;
                pool.For(count, 2048, (s, e, c) => PredictAndPush(s, e, c, dtLocal, hasShell));

                // ---- Tearing: the links the push has stretched past what the metal can take ------
                pool.For(count, 2048, (s, e, c) => Tear(s, e));

                // ---- Links ---------------------------------------------------------------------
                for (int it = 0; it < iterations; it++)
                {
                    for (int color = 0; color < 8; color++)
                    {
                        int[] list = colors[color];
                        pool.For(list.Length, 1024, (s, e, c) => Relax(list, s, e));
                    }
                }
                if (hasShell) pool.For(count, 2048, (s, e, c) => Push(s, e, c));

                // ---- Plastic flow, then the new velocities ---------------------------------------
                pool.For(count, 2048, (s, e, c) => Flow(s, e));
                float dampLocal = damp;
                pool.For(count, 2048, (s, e, c) => Finish(s, e, dtLocal, dampLocal));

                if (shellActive)
                {
                    float charged = 0f;
                    for (int c = 0; c < chunks; c++) charged += chunkCharged[c];
                    energy -= charged * costPerParticle;
                }
                steps++;
            }

            ImpactSimResult result = Sample(exited || (energy > 0 && !stopped && az < 0f && exited), input.EnergyScale);
            result.EnergyLeft = (float)Math.Max(energy / energy0, 0.0);
            result.Particles = count;
            result.Steps = steps;
            int chargedCount = 0;
            for (int c = 0; c < count; c++) if (charged[c] != 0) chargedCount++;
            result.Charged = chargedCount;
            int brokenLinks = 0;
            for (int slot = 0; slot < rest.Length; slot++) if (rest[slot] < 0f) brokenLinks++;
            result.Broken = brokenLinks / 2;
            result.Ms = watch.Elapsed.TotalMilliseconds;
            return result;
        }

        // ---- Setup --------------------------------------------------------------------------------

        private void Allocate()
        {
            px = new float[count]; py = new float[count]; pz = new float[count];
            ox = new float[count]; oy = new float[count]; oz = new float[count];
            qx = new float[count]; qy = new float[count]; qz = new float[count];
            vx = new float[count]; vy = new float[count]; vz = new float[count];
            rest = new float[count * Links];
            rest0 = new float[count * Links];
            nbr = new int[count * Links];
            fixedFlag = new byte[count];
            charged = new byte[count];
        }

        private void Build(int seed)
        {
            uint state = (uint)(seed * 747796405 + 2891336453);
            Func<float> noise = () =>
            {
                state ^= state << 13; state ^= state >> 17; state ^= state << 5;
                return ((state & 0xFFFFFF) / 16777216f) * 2f - 1f;
            };

            for (int kk = 0; kk < k; kk++)
            {
                for (int j = 0; j < n; j++)
                {
                    for (int i = 0; i < n; i++)
                    {
                        int idx = (kk * n + j) * n + i;
                        bool edge = i == 0 || j == 0 || i == n - 1 || j == n - 1;
                        float jitter = edge ? 0f : 0.12f;
                        px[idx] = (i - (n - 1) * 0.5f) * h + noise() * h * jitter;
                        py[idx] = (j - (n - 1) * 0.5f) * h + noise() * h * jitter;
                        pz[idx] = -t * 0.5f + (kk + 0.5f) * hz; noise();   // (no jitter across the thickness: the surface heights would be noisy)
                        qx[idx] = px[idx]; qy[idx] = py[idx]; qz[idx] = pz[idx];
                        ox[idx] = px[idx]; oy[idx] = py[idx]; oz[idx] = pz[idx];
                        fixedFlag[idx] = (byte)(edge ? 1 : 0);
                    }
                }
            }

            int[] colorCounts = new int[8];
            for (int idx = 0; idx < count; idx++)
            {
                int i = idx % n, j = (idx / n) % n, kk = idx / (n * n);
                colorCounts[(i & 1) | ((j & 1) << 1) | ((kk & 1) << 2)]++;
            }
            colors = new int[8][];
            for (int c = 0; c < 8; c++) colors[c] = new int[colorCounts[c]];
            int[] fill = new int[8];

            for (int idx = 0; idx < count; idx++)
            {
                int i = idx % n, j = (idx / n) % n, kk = idx / (n * n);
                int color = (i & 1) | ((j & 1) << 1) | ((kk & 1) << 2);
                colors[color][fill[color]++] = idx;

                for (int o = 0; o < Links; o++)
                {
                    int ni = i + Ox[o], nj = j + Oy[o], nk = kk + Oz[o];
                    int slot = idx * Links + o;
                    if (ni < 0 || nj < 0 || nk < 0 || ni >= n || nj >= n || nk >= k) { nbr[slot] = -1; rest[slot] = 0f; continue; }
                    int other = (nk * n + nj) * n + ni;
                    nbr[slot] = other;
                    float dx = px[other] - px[idx], dy = py[other] - py[idx], dz = pz[other] - pz[idx];
                    rest[slot] = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                    rest0[slot] = rest[slot];
                }
            }
        }

        // ---- The steps (each runs on whatever threads are free) -----------------------------------

        private void PredictAndPush(int start, int end, int chunk, float dt, bool hasShell)
        {
            float newly = 0f;
            for (int idx = start; idx < end; idx++)
            {
                if (fixedFlag[idx] != 0) continue;
                qx[idx] = px[idx] + vx[idx] * dt;
                qy[idx] = py[idx] + vy[idx] * dt;
                qz[idx] = pz[idx] + vz[idx] * dt;
                if (hasShell)
                {
                    PushOne(idx);
                    newly += Sweep(idx);
                }
            }
            chunkCharged[chunk] += newly;
        }

        private void Push(int start, int end, int chunk)
        {
            for (int idx = start; idx < end; idx++)
                if (fixedFlag[idx] == 0) PushOne(idx);
        }

        // The shell pays for the metal it digs through: a particle whose place the shell's nose has swept past is
        // charged once (ResistanceJPerM3 x its volume), wherever the push has taken it since. So the price is the
        // volume of the tunnel, the same as the energy model charges for a plug.
        private float Sweep(int idx)
        {
            if (charged[idx] != 0) return 0f;
            float rx = ox[idx] - tx0, ry = oy[idx] - ty0, rz = oz[idx] - tz0;
            float s = rx * ax0 + ry * ay0 + rz * az0;                 // along the line the shell digs
            float lx = rx - ax0 * s, ly = ry - ay0 * s, lz = rz - az0 * s;
            float lateral2 = lx * lx + ly * ly + lz * lz;
            if (lateral2 >= radius * radius) return 0f;
            // The nose is a sphere of the shell's radius: it has reached this point when its center is level with it.
            float reached = tipS - radius + MathF.Sqrt(radius * radius - lateral2);
            if (s > reached) return 0f;
            charged[idx] = 1;
            return 1f;
        }

        // The particle is inside the shell: it goes to the surface of the capsule.
        private void PushOne(int idx)
        {
            float x = qx[idx], y = qy[idx], z = qz[idx];

            // The nearest point of the capsule's axis, from the back of its body to the center of its nose.
            float bx = cx - ax * bodyLength, by = cy - ay * bodyLength, bz = cz - az * bodyLength;
            float along = (x - bx) * ax + (y - by) * ay + (z - bz) * az;
            along = Math.Min(Math.Max(along, 0f), bodyLength);
            float nx = bx + ax * along, ny = by + ay * along, nz = bz + az * along;

            float dx = x - nx, dy = y - ny, dz = z - nz;
            float d2 = dx * dx + dy * dy + dz * dz;
            float reach = radius + particleRadius;
            if (d2 >= reach * reach) return;

            float d = MathF.Sqrt(d2);
            float ux, uy, uz;
            if (d > 1e-6f) { ux = dx / d; uy = dy / d; uz = dz / d; }
            else { ux = -ay; uy = ax; uz = 0f; if (ux * ux + uy * uy < 1e-6f) ux = 1f; }

            float move = Math.Min(reach - d, 1.2f * h);
            qx[idx] = x + ux * move;
            qy[idx] = y + uy * move;
            qz[idx] = z + uz * move;
        }

        // One color of the links: each particle of the color moves toward what its links ask (their average).
        // The neighbors are all of other colors, so nothing else changes at the same time.
        private void Relax(int[] list, int start, int end)
        {
            for (int n0 = start; n0 < end; n0++)
            {
                int idx = list[n0];
                if (fixedFlag[idx] != 0) continue;

                float sx = 0f, sy = 0f, sz = 0f;
                int used = 0;
                float x = qx[idx], y = qy[idx], z = qz[idx];
                int baseSlot = idx * Links;
                for (int o = 0; o < Links; o++)
                {
                    float r = rest[baseSlot + o];
                    if (r <= 0f) continue;
                    int j = nbr[baseSlot + o];
                    float dx = qx[j] - x, dy = qy[j] - y, dz = qz[j] - z;
                    float len = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                    if (len < 1e-9f) continue;
                    float c = (len - r) / len;
                    sx += c * dx; sy += c * dy; sz += c * dz;
                    used++;
                }
                if (used == 0) continue;
                float f = 1f / used;
                qx[idx] = x + sx * f;
                qy[idx] = y + sy * f;
                qz[idx] = z + sz * f;
            }
        }

        // A link stretched past the break strain by the push breaks (a brittle plate breaks early, a ductile one rather
        // flows). It is looked at right after the push, before the links pull the particles back: afterwards the whole
        // neighborhood has followed the pushed particles and nothing looks stretched any more. Both ends of a link see
        // the same numbers, so they decide the same.
        private void Tear(int start, int end)
        {
            for (int idx = start; idx < end; idx++)
            {
                int baseSlot = idx * Links;
                float x = qx[idx], y = qy[idx], z = qz[idx];
                for (int o = 0; o < Links; o++)
                {
                    float r = rest[baseSlot + o];
                    if (r <= 0f) continue;
                    int j = nbr[baseSlot + o];
                    float dx = qx[j] - x, dy = qy[j] - y, dz = qz[j] - z;
                    float len = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                    if ((len - r) / r > breakStrain && CanBreak(idx, j, o, x, y, z))
                        rest[baseSlot + o] = -1f;
                }
            }
        }

        // A link stretched or squeezed past the yield strain stays that way (plastic flow: the lips and bulges).
        private void Flow(int start, int end)
        {
            for (int idx = start; idx < end; idx++)
            {
                int baseSlot = idx * Links;
                float x = qx[idx], y = qy[idx], z = qz[idx];
                for (int o = 0; o < Links; o++)
                {
                    float r = rest[baseSlot + o];
                    if (r <= 0f) continue;
                    int j = nbr[baseSlot + o];
                    float dx = qx[j] - x, dy = qy[j] - y, dz = qz[j] - z;
                    float len = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                    float strain = (len - r) / r;
                    if (strain > yieldStrain || strain < -yieldStrain)
                    {
                        float flowed = r + plasticRate * (len - r);
                        // Metal that has flowed too far is torn: that is what ductility is.
                        if (flowed > rest0[baseSlot + o] * (1f + ductility) && CanBreak(idx, j, o, x, y, z)) flowed = -1f;
                        rest[baseSlot + o] = flowed;
                    }
                }
            }
        }

        // A link may only break near the shell, and the back layer only once the shell has come through to it: elsewhere the
        // plate bends and stays bent but does not tear (so a shell that is stopped does not make a hole in the back).
        private bool CanBreak(int idx, int j, int o, float x, float y, float z)
        {
            float mx = 0.5f * (x + qx[j]) - cx, my = 0.5f * (y + qy[j]) - cy, mz = 0.5f * (z + qz[j]) - cz;
            if (mx * mx + my * my + mz * mz > breakReach2) return false;
            if (!backOpen)
            {
                int layer = idx / (n * n);
                if (layer == 0 || layer + Oz[o] == 0) return false;
            }
            return true;
        }

        private void Finish(int start, int end, float dt, float damp)
        {
            float inverse = 1f / dt;
            for (int idx = start; idx < end; idx++)
            {
                if (fixedFlag[idx] != 0) continue;
                vx[idx] = (qx[idx] - px[idx]) * inverse * damp;
                vy[idx] = (qy[idx] - py[idx]) * inverse * damp;
                vz[idx] = (qz[idx] - pz[idx]) * inverse * damp;
                px[idx] = qx[idx]; py[idx] = qy[idx]; pz[idx] = qz[idx];
            }
        }

        // ---- The result ---------------------------------------------------------------------------

        // The top and bottom of the particles in every column (and how many there are), read from where they are now.
        // Particles that were thrown far from the plate do not count.
        private void Surfaces(out float[] top, out float[] bottom, out int[] cover)
        {
            top = new float[n * n];
            bottom = new float[n * n];
            cover = new int[n * n];
            for (int i = 0; i < top.Length; i++) { top[i] = float.MinValue; bottom[i] = float.MaxValue; }

            float zMin = -t * 0.5f - radius * 0.6f, zMax = t * 0.5f + radius * 0.6f;
            for (int idx = 0; idx < count; idx++)
            {
                float z = pz[idx];
                if (z < zMin || z > zMax) continue;
                float gx = px[idx] / h + (n - 1) * 0.5f;
                float gy = py[idx] / h + (n - 1) * 0.5f;
                int ix = (int)Math.Floor(gx), iy = (int)Math.Floor(gy);
                for (int dj = 0; dj <= 1; dj++)
                {
                    for (int di = 0; di <= 1; di++)
                    {
                        int cxi = ix + di, cyi = iy + dj;
                        if (cxi < 0 || cyi < 0 || cxi >= n || cyi >= n) continue;
                        int cell = cyi * n + cxi;
                        cover[cell]++;
                        float upper = z + hz * 0.5f, lower = z - hz * 0.5f;
                        if (upper > top[cell]) top[cell] = upper;
                        if (lower < bottom[cell]) bottom[cell] = lower;
                    }
                }
            }
        }

        // The change of the front and back surfaces, per column, and the columns where nothing is left (the hole).
        private ImpactSimResult Sample(bool perforated, float energyScale)
        {
            float[] top, bottom;
            int[] cover;
            Surfaces(out top, out bottom, out cover);

            ImpactSimResult result = new ImpactSimResult
            {
                N = n, Cell = h, Size = size,
                Front = new float[n * n], Back = new float[n * n], Hole = new byte[n * n],
                Perforated = perforated,
            };
            int holeCells = 0;
            float deepest = 0f;
            for (int j = 0; j < n; j++)
            {
                for (int i = 0; i < n; i++)
                {
                    int cell = j * n + i;
                    bool border = i == 0 || j == 0 || i == n - 1 || j == n - 1;
                    if (cover[cell] == 0 && !border)
                    {
                        if (perforated)
                        {
                            result.Hole[cell] = 0;
                            holeCells++;
                            deepest = t;
                            continue;
                        }
                        // A shell that was stopped leaves a crater, not a hole, however empty the column looks: the dent
                        // is as deep as the tip got.
                        result.Hole[cell] = 255;
                        result.Front[cell] = -Math.Min(t, t * 0.5f - deepestTip + hz);
                        deepest = Math.Max(deepest, -result.Front[cell]);
                        continue;
                    }
                    result.Hole[cell] = 255;
                    if (cover[cell] == 0) continue;
                    if (border || baseCover[cell] == 0) continue;
                    result.Front[cell] = top[cell] - baseTop[cell];
                    result.Back[cell] = baseBottom[cell] - bottom[cell];
                    float dent = -result.Front[cell];
                    if (dent > deepest) deepest = dent;
                }
            }
            result.DepthM = Math.Min(deepest, t);
            result.HoleRadiusM = holeCells > 0 ? MathF.Sqrt(holeCells * h * h / (float)Math.PI) : 0f;
            return result;
        }
    }
}
