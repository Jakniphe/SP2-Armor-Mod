using System;
using System.Text;

namespace ArmorFragments.Sim
{
    // The impact simulation's per-particle work on the graphics card (OpenCL). ImpactSim keeps the shell's logic (its energy, its path, the
    // ricochet) on the CPU, one copy of it for both; each step it hands this solver the shell's state and the solver does what the CPU
    // does with a thread pool: push the particles out of the shell's way, tear the links, relax them color by color, let the metal flow,
    // and move the particles. Only the number of particles the shell has newly swept through comes back, once a step.
    //
    // Every OpenCL call must come from ONE thread (the worker's); the solver is not thread safe. Any failure throws, and the caller falls
    // back to the CPU for good.
    internal sealed class ClSolver : IDisposable
    {
        // The slots of the parameter array that goes to the card every step (the same numbers are named in the kernel source below).
        public const int ParamCount = 32;
        public const int PCx = 0, PCy = 1, PCz = 2, PAx = 3, PAy = 4, PAz = 5, PTipS = 6, PTx0 = 7, PTy0 = 8, PTz0 = 9, PAx0 = 10, PAy0 = 11,
                         PAz0 = 12, PRadius = 13, PBody = 14, PParticle = 15, PH = 16, PDt = 17, PDamp = 18, PHasShell = 19, PBackOpen = 20,
                         PBreak = 21, PYield = 22, PRate = 23, PDuctility = 24, PReach2 = 25;

        private const string Source = @"
#define LINKS 18
__constant int OX[18] = {1,-1,0,0,0,0,1,1,-1,-1,1,1,-1,-1,0,0,0,0};
__constant int OY[18] = {0,0,1,-1,0,0,1,-1,1,-1,0,0,0,0,1,1,-1,-1};
__constant int OZ[18] = {0,0,0,0,1,-1,0,0,0,0,1,-1,1,-1,1,-1,1,-1};

#define P_CX 0
#define P_CY 1
#define P_CZ 2
#define P_AX 3
#define P_AY 4
#define P_AZ 5
#define P_TIPS 6
#define P_TX0 7
#define P_TY0 8
#define P_TZ0 9
#define P_AX0 10
#define P_AY0 11
#define P_AZ0 12
#define P_RADIUS 13
#define P_BODY 14
#define P_PARTICLE 15
#define P_H 16
#define P_DT 17
#define P_DAMP 18
#define P_HASSHELL 19
#define P_BACKOPEN 20
#define P_BREAK 21
#define P_YIELD 22
#define P_RATE 23
#define P_DUCT 24
#define P_REACH2 25

// The index of the neighbor of particle (i, j, l) through link o, or -1 if there is none (outside the patch).
inline int neighbor(int i, int j, int l, int o, int n, int k)
{
    int ni = i + OX[o], nj = j + OY[o], nl = l + OZ[o];
    if (ni < 0 || nj < 0 || nl < 0 || ni >= n || nj >= n || nl >= k) return -1;
    return (nl * n + nj) * n + ni;
}

// The particle is inside the shell: it goes to the surface of the capsule.
inline float3 push_one(float3 q, __constant float* P)
{
    float ax = P[P_AX], ay = P[P_AY], az = P[P_AZ];
    float bodyLength = P[P_BODY], radius = P[P_RADIUS];
    float bx = P[P_CX] - ax * bodyLength, by = P[P_CY] - ay * bodyLength, bz = P[P_CZ] - az * bodyLength;
    float along = (q.x - bx) * ax + (q.y - by) * ay + (q.z - bz) * az;
    along = fmin(fmax(along, 0.0f), bodyLength);
    float3 nearest = (float3)(bx + ax * along, by + ay * along, bz + az * along);
    float3 d = q - nearest;
    float d2 = dot(d, d);
    float reach = radius + P[P_PARTICLE];
    if (d2 >= reach * reach) return q;

    float dl = sqrt(d2);
    float3 u;
    if (dl > 1e-6f) u = d / dl;
    else { u = (float3)(-ay, ax, 0.0f); if (u.x * u.x + u.y * u.y < 1e-6f) u.x = 1.0f; }
    float move = fmin(reach - dl, 1.2f * P[P_H]);
    return q + u * move;
}

// Has the shell's nose swept past this particle's ORIGINAL place (so it pays for it)?
inline int swept(float4 original, __constant float* P)
{
    float rx = original.x - P[P_TX0], ry = original.y - P[P_TY0], rz = original.z - P[P_TZ0];
    float s = rx * P[P_AX0] + ry * P[P_AY0] + rz * P[P_AZ0];
    float lx = rx - P[P_AX0] * s, ly = ry - P[P_AY0] * s, lz = rz - P[P_AZ0] * s;
    float lateral2 = lx * lx + ly * ly + lz * lz;
    float radius = P[P_RADIUS];
    if (lateral2 >= radius * radius) return 0;
    float reached = P[P_TIPS] - radius + sqrt(radius * radius - lateral2);
    if (s > reached) return 0;
    return 1;
}

// A link may only break near the shell, and the back layer only once the shell has come through to it.
inline int can_break(int layer, int oz, float3 a, float3 b, __constant float* P)
{
    float3 m = 0.5f * (a + b) - (float3)(P[P_CX], P[P_CY], P[P_CZ]);
    if (dot(m, m) > P[P_REACH2]) return 0;
    if (P[P_BACKOPEN] < 0.5f)
    {
        if (layer == 0 || layer + oz == 0) return 0;
    }
    return 1;
}

__kernel void init_links(__global const float4* p, __global float* rest, __global float* rest0, int n, int k, int count)
{
    int idx = get_global_id(0);
    if (idx >= count) return;
    int i = idx % n, j = (idx / n) % n, l = idx / (n * n);
    for (int o = 0; o < LINKS; o++)
    {
        int nb = neighbor(i, j, l, o, n, k);
        float r = 0.0f;
        if (nb >= 0) r = length(p[nb].xyz - p[idx].xyz);
        rest[idx * LINKS + o] = r;
        rest0[idx * LINKS + o] = r;
    }
}

__kernel void predict_push(__global const float4* p, __global const float4* v, __global float4* q, __global const float4* orig,
                           __global const uchar* fixedFlag, __global uchar* charged, __global int* counter, __constant float* P, int count)
{
    int idx = get_global_id(0);
    if (idx >= count) return;
    if (fixedFlag[idx] != 0) return;
    float dt = P[P_DT];
    float3 pos = p[idx].xyz + v[idx].xyz * dt;
    if (P[P_HASSHELL] > 0.5f)
    {
        pos = push_one(pos, P);
        if (charged[idx] == 0 && swept(orig[idx], P) != 0)
        {
            charged[idx] = 1;
            atomic_add(counter, 1);
        }
    }
    q[idx] = (float4)(pos, 0.0f);
}

__kernel void push(__global float4* q, __global const uchar* fixedFlag, __constant float* P, int count)
{
    int idx = get_global_id(0);
    if (idx >= count) return;
    if (fixedFlag[idx] != 0) return;
    q[idx] = (float4)(push_one(q[idx].xyz, P), 0.0f);
}

__kernel void tear(__global const float4* q, __global float* rest, __constant float* P, int n, int k, int count)
{
    int idx = get_global_id(0);
    if (idx >= count) return;
    int i = idx % n, j = (idx / n) % n, l = idx / (n * n);
    float3 a = q[idx].xyz;
    for (int o = 0; o < LINKS; o++)
    {
        float r = rest[idx * LINKS + o];
        if (r <= 0.0f) continue;
        int nb = neighbor(i, j, l, o, n, k);
        float3 b = q[nb].xyz;
        float len = length(b - a);
        if ((len - r) / r > P[P_BREAK] && can_break(l, OZ[o], a, b, P) != 0) rest[idx * LINKS + o] = -1.0f;
    }
}

// One color of the links (the particles of a color are not linked to each other, so they can all move at once).
__kernel void relax(__global float4* q, __global const float* rest, __global const uchar* fixedFlag, int n, int k, int color)
{
    int cx = color & 1, cy = (color >> 1) & 1, cz = (color >> 2) & 1;
    int nx = (n - cx + 1) / 2, ny = (n - cy + 1) / 2, nz = (k - cz + 1) / 2;
    int gid = get_global_id(0);
    if (gid >= nx * ny * nz) return;
    int a = gid % nx, b = (gid / nx) % ny, d = gid / (nx * ny);
    int i = 2 * a + cx, j = 2 * b + cy, l = 2 * d + cz;
    int idx = (l * n + j) * n + i;
    if (fixedFlag[idx] != 0) return;

    float3 me = q[idx].xyz;
    float3 sum = (float3)(0.0f, 0.0f, 0.0f);
    int used = 0;
    for (int o = 0; o < LINKS; o++)
    {
        float r = rest[idx * LINKS + o];
        if (r <= 0.0f) continue;
        int nb = neighbor(i, j, l, o, n, k);
        float3 dv = q[nb].xyz - me;
        float len = length(dv);
        if (len < 1e-9f) continue;
        sum += dv * ((len - r) / len);
        used++;
    }
    if (used == 0) return;
    q[idx] = (float4)(me + sum * (1.0f / used), 0.0f);
}

__kernel void flow(__global const float4* q, __global float* rest, __global const float* rest0, __constant float* P, int n, int k, int count)
{
    int idx = get_global_id(0);
    if (idx >= count) return;
    int i = idx % n, j = (idx / n) % n, l = idx / (n * n);
    float3 a = q[idx].xyz;
    for (int o = 0; o < LINKS; o++)
    {
        float r = rest[idx * LINKS + o];
        if (r <= 0.0f) continue;
        int nb = neighbor(i, j, l, o, n, k);
        float3 b = q[nb].xyz;
        float len = length(b - a);
        float strain = (len - r) / r;
        if (strain > P[P_YIELD] || strain < -P[P_YIELD])
        {
            float flowed = r + P[P_RATE] * (len - r);
            if (flowed > rest0[idx * LINKS + o] * (1.0f + P[P_DUCT]) && can_break(l, OZ[o], a, b, P) != 0) flowed = -1.0f;
            rest[idx * LINKS + o] = flowed;
        }
    }
}

__kernel void finish(__global float4* p, __global float4* v, __global const float4* q, __global const uchar* fixedFlag, __constant float* P, int count)
{
    int idx = get_global_id(0);
    if (idx >= count) return;
    if (fixedFlag[idx] != 0) return;
    float inverse = 1.0f / P[P_DT];
    float damp = P[P_DAMP];
    float3 pos = p[idx].xyz, now = q[idx].xyz;
    v[idx] = (float4)((now - pos) * inverse * damp, 0.0f);
    p[idx] = (float4)(now, 0.0f);
}

__kernel void count_broken(__global const float* rest, __global int* counter, int total)
{
    int gid = get_global_id(0);
    if (gid >= total) return;
    if (rest[gid] < 0.0f) atomic_add(counter, 1);
}
";

        private IntPtr context, queue, program, device;
        private IntPtr kInit, kPredict, kPush, kTear, kRelax, kFlow, kFinish, kBroken;
        private IntPtr bP, bV, bQ, bO, bRest, bRest0, bFixed, bCharged, bCounter, bParams;
        private int capacity;
        private int n, k, count;
        private float[] zeros4;
        private readonly int[] counterValue = new int[1];

        public string DeviceName = "";
        public int ComputeUnits, ClockMhz;
        public long MemoryMb;
        public bool Discrete;
        public double BuildMs;

        public static ClSolver Create(out string error)
        {
            ClSolver s = new ClSolver();
            try
            {
                s.Open();
                error = null;
                return s;
            }
            catch (Exception e)
            {
                s.Dispose();
                error = e is DllNotFoundException ? "OpenCL.dll is not installed" : e.GetType().Name + ": " + e.Message;
                return null;
            }
        }

        private static void Check(int status, string what)
        {
            if (status != 0) throw new InvalidOperationException(what + " failed (OpenCL error " + status + ")");
        }

        private void Open()
        {
            uint platformCount;
            Check(ClNative.clGetPlatformIDs(0, null, out platformCount), "clGetPlatformIDs");
            if (platformCount == 0) throw new InvalidOperationException("OpenCL has no platform");
            IntPtr[] platforms = new IntPtr[platformCount];
            ClNative.clGetPlatformIDs(platformCount, platforms, out platformCount);

            double best = -1.0;
            foreach (IntPtr platform in platforms)
            {
                uint deviceCount;
                if (ClNative.clGetDeviceIDs(platform, 0xFFFFFFFF, 0, null, out deviceCount) != 0 || deviceCount == 0) continue;
                IntPtr[] devices = new IntPtr[deviceCount];
                ClNative.clGetDeviceIDs(platform, 0xFFFFFFFF, deviceCount, devices, out deviceCount);
                foreach (IntPtr candidate in devices)
                {
                    if (ClNative.DeviceNumber(candidate, 0x1000) != 4) continue;   // a graphics card only
                    bool discrete = ClNative.DeviceNumber(candidate, 0x1035) == 0;
                    double score = (discrete ? 1e9 : 0.0) + ClNative.DeviceNumber(candidate, 0x1002) * (double)ClNative.DeviceNumber(candidate, 0x100C);
                    if (score <= best) continue;
                    best = score;
                    device = candidate;
                    DeviceName = ClNative.DeviceString(candidate, 0x102B);
                    ComputeUnits = (int)ClNative.DeviceNumber(candidate, 0x1002);
                    ClockMhz = (int)ClNative.DeviceNumber(candidate, 0x100C);
                    MemoryMb = ClNative.DeviceNumber(candidate, 0x101F) / (1024 * 1024);
                    Discrete = discrete;
                }
            }
            if (device == IntPtr.Zero) throw new InvalidOperationException("no graphics card among the OpenCL devices");

            int err;
            context = ClNative.clCreateContext(IntPtr.Zero, 1, new[] { device }, IntPtr.Zero, IntPtr.Zero, out err);
            Check(err, "clCreateContext");
            queue = ClNative.clCreateCommandQueue(context, device, 0, out err);
            Check(err, "clCreateCommandQueue");

            System.Diagnostics.Stopwatch build = System.Diagnostics.Stopwatch.StartNew();
            program = ClNative.clCreateProgramWithSource(context, 1, new[] { Source }, null, out err);
            Check(err, "clCreateProgramWithSource");
            int status = ClNative.clBuildProgram(program, 1, new[] { device }, "", IntPtr.Zero, IntPtr.Zero);
            if (status != 0)
            {
                byte[] log = new byte[8192];
                UIntPtr size;
                ClNative.clGetProgramBuildInfo(program, device, 0x1183, (UIntPtr)log.Length, log, out size);
                throw new InvalidOperationException("the kernels did not compile (" + status + "): " + ClNative.Text(log, size).Trim());
            }
            BuildMs = build.Elapsed.TotalMilliseconds;

            kInit = Kernel("init_links"); kPredict = Kernel("predict_push"); kPush = Kernel("push"); kTear = Kernel("tear");
            kRelax = Kernel("relax"); kFlow = Kernel("flow"); kFinish = Kernel("finish"); kBroken = Kernel("count_broken");
            bParams = Buffer(ParamCount * 4);
            bCounter = Buffer(4);
        }

        private IntPtr Kernel(string name)
        {
            int err;
            IntPtr kernel = ClNative.clCreateKernel(program, name, out err);
            Check(err, "clCreateKernel " + name);
            return kernel;
        }

        private IntPtr Buffer(long bytes)
        {
            int err;
            IntPtr buffer = ClNative.clCreateBuffer(context, 1, (UIntPtr)bytes, IntPtr.Zero, out err);   // read-write
            Check(err, "clCreateBuffer (" + bytes + " bytes)");
            return buffer;
        }

        private void Release(ref IntPtr buffer)
        {
            if (buffer != IntPtr.Zero) ClNative.clReleaseMemObject(buffer);
            buffer = IntPtr.Zero;
        }

        // Buffers big enough for this many particles (kept and reused: a bigger simulation makes them again).
        private void Reserve(int particles)
        {
            if (particles <= capacity) return;
            Release(ref bP); Release(ref bV); Release(ref bQ); Release(ref bO); Release(ref bRest); Release(ref bRest0); Release(ref bFixed); Release(ref bCharged);
            capacity = particles;
            bP = Buffer(16L * capacity); bV = Buffer(16L * capacity); bQ = Buffer(16L * capacity); bO = Buffer(16L * capacity);
            bRest = Buffer(4L * 18 * capacity); bRest0 = Buffer(4L * 18 * capacity);
            bFixed = Buffer(capacity); bCharged = Buffer(capacity);
            zeros4 = new float[4 * capacity];
        }

        private static void Arg(IntPtr kernel, uint index, IntPtr buffer)
        {
            Check(ClNative.clSetKernelArg(kernel, index, (UIntPtr)IntPtr.Size, ref buffer), "clSetKernelArg");
        }

        private static void Arg(IntPtr kernel, uint index, int value)
        {
            Check(ClNative.clSetKernelArg(kernel, index, (UIntPtr)4, ref value), "clSetKernelArg");
        }

        private static UIntPtr Rounded(int items)
        {
            return (UIntPtr)(((Math.Max(items, 1) + 63) / 64) * 64);
        }

        private static readonly UIntPtr[] Local = { (UIntPtr)64 };

        private void Run(IntPtr kernel, int items)
        {
            Check(ClNative.clEnqueueNDRangeKernel(queue, kernel, 1, null, new[] { Rounded(items) }, Local, 0, null, IntPtr.Zero), "a dispatch");
        }

        // A new simulation: the particles go to the card (their places, where they started, and which are held fixed), and the links' rest
        // lengths are worked out there from those places.
        // Where the time of the last simulation went (Stopwatch ticks), for the [PERF] breakdown: Begin = everything the start does; ParamWrite =
        // the blocking upload of each step's numbers (an in-order queue makes it wait for the step before); Dispatch = queueing the kernels;
        // CounterRead = the wait for the shell's swept-particle count; Finish = the wait for the card to end; Readback = the particles back.
        public long TicksBegin, TicksParamWrite, TicksDispatch, TicksCounterRead, TicksFinish, TicksReadback;
        public int Steps, SyncedSteps;

        public void Begin(float[] px, float[] py, float[] pz, float[] ox, float[] oy, float[] oz, byte[] fixedFlag, int columns, int layers)
        {
            long beganAt = System.Diagnostics.Stopwatch.GetTimestamp();
            TicksBegin = TicksParamWrite = TicksDispatch = TicksCounterRead = TicksFinish = TicksReadback = 0;
            Steps = SyncedSteps = 0;
            n = columns; k = layers; count = n * n * k;
            Reserve(count);

            float[] position = new float[4 * count], original = new float[4 * count];
            for (int i = 0; i < count; i++)
            {
                position[4 * i] = px[i]; position[4 * i + 1] = py[i]; position[4 * i + 2] = pz[i];
                original[4 * i] = ox[i]; original[4 * i + 1] = oy[i]; original[4 * i + 2] = oz[i];
            }
            UIntPtr bytes4 = (UIntPtr)(16L * count);
            Check(ClNative.clEnqueueWriteBuffer(queue, bP, 1, UIntPtr.Zero, bytes4, position, 0, null, IntPtr.Zero), "upload positions");
            Check(ClNative.clEnqueueWriteBuffer(queue, bQ, 1, UIntPtr.Zero, bytes4, position, 0, null, IntPtr.Zero), "upload predicted positions");
            Check(ClNative.clEnqueueWriteBuffer(queue, bO, 1, UIntPtr.Zero, bytes4, original, 0, null, IntPtr.Zero), "upload original positions");
            Check(ClNative.clEnqueueWriteBuffer(queue, bV, 1, UIntPtr.Zero, bytes4, zeros4, 0, null, IntPtr.Zero), "clear velocities");
            Check(ClNative.clEnqueueWriteBuffer(queue, bFixed, 1, UIntPtr.Zero, (UIntPtr)count, fixedFlag, 0, null, IntPtr.Zero), "upload fixed flags");
            Check(ClNative.clEnqueueWriteBuffer(queue, bCharged, 1, UIntPtr.Zero, (UIntPtr)count, new byte[count], 0, null, IntPtr.Zero), "clear charged flags");
            counterValue[0] = 0;
            Check(ClNative.clEnqueueWriteBuffer(queue, bCounter, 1, UIntPtr.Zero, (UIntPtr)4, counterValue, 0, null, IntPtr.Zero), "clear the counter");

            // Kernel arguments (the buffers and the sizes stay the same for the whole simulation; only the relax color changes).
            Arg(kInit, 0, bP); Arg(kInit, 1, bRest); Arg(kInit, 2, bRest0); Arg(kInit, 3, n); Arg(kInit, 4, k); Arg(kInit, 5, count);
            Arg(kPredict, 0, bP); Arg(kPredict, 1, bV); Arg(kPredict, 2, bQ); Arg(kPredict, 3, bO); Arg(kPredict, 4, bFixed);
            Arg(kPredict, 5, bCharged); Arg(kPredict, 6, bCounter); Arg(kPredict, 7, bParams); Arg(kPredict, 8, count);
            Arg(kPush, 0, bQ); Arg(kPush, 1, bFixed); Arg(kPush, 2, bParams); Arg(kPush, 3, count);
            Arg(kTear, 0, bQ); Arg(kTear, 1, bRest); Arg(kTear, 2, bParams); Arg(kTear, 3, n); Arg(kTear, 4, k); Arg(kTear, 5, count);
            Arg(kRelax, 0, bQ); Arg(kRelax, 1, bRest); Arg(kRelax, 2, bFixed); Arg(kRelax, 3, n); Arg(kRelax, 4, k);
            Arg(kFlow, 0, bQ); Arg(kFlow, 1, bRest); Arg(kFlow, 2, bRest0); Arg(kFlow, 3, bParams); Arg(kFlow, 4, n); Arg(kFlow, 5, k); Arg(kFlow, 6, count);
            Arg(kFinish, 0, bP); Arg(kFinish, 1, bV); Arg(kFinish, 2, bQ); Arg(kFinish, 3, bFixed); Arg(kFinish, 4, bParams); Arg(kFinish, 5, count);

            Run(kInit, count);
            TicksBegin = System.Diagnostics.Stopwatch.GetTimestamp() - beganAt;
        }

        // One step of the particles. Returns how many particles the shell has swept through so far (in all); that number is only read back
        // while the shell is in play (afterwards nobody waits for the card between steps).
        public int Step(float[] parameters, bool hasShell, int iterations)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            Check(ClNative.clEnqueueWriteBuffer(queue, bParams, 1, UIntPtr.Zero, (UIntPtr)(ParamCount * 4), parameters, 0, null, IntPtr.Zero), "upload the step's numbers");
            long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
            TicksParamWrite += t1 - t0;
            Steps++;

            Run(kPredict, count);
            Run(kTear, count);
            for (int iteration = 0; iteration < iterations; iteration++)
            {
                for (int color = 0; color < 8; color++)
                {
                    Arg(kRelax, 5, color);
                    int cx = color & 1, cy = (color >> 1) & 1, cz = (color >> 2) & 1;
                    Run(kRelax, ((n - cx + 1) / 2) * ((n - cy + 1) / 2) * ((k - cz + 1) / 2));
                }
            }
            if (hasShell) Run(kPush, count);
            Run(kFlow, count);
            Run(kFinish, count);
            long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
            TicksDispatch += t2 - t1;

            if (!hasShell) return counterValue[0];
            Check(ClNative.clEnqueueReadBuffer(queue, bCounter, 1, UIntPtr.Zero, (UIntPtr)4, counterValue, 0, null, IntPtr.Zero), "read the counter");
            TicksCounterRead += System.Diagnostics.Stopwatch.GetTimestamp() - t2;
            SyncedSteps++;
            return counterValue[0];
        }

        // The simulation is over: the particles' places come back, and the number of links that tore.
        public void End(float[] px, float[] py, float[] pz, out int brokenLinks)
        {
            long f0 = System.Diagnostics.Stopwatch.GetTimestamp();
            ClNative.clFinish(queue);
            long f1 = System.Diagnostics.Stopwatch.GetTimestamp();
            TicksFinish = f1 - f0;
            float[] position = new float[4 * count];
            Check(ClNative.clEnqueueReadBuffer(queue, bP, 1, UIntPtr.Zero, (UIntPtr)(16L * count), position, 0, null, IntPtr.Zero), "read the particles back");
            for (int i = 0; i < count; i++)
            {
                px[i] = position[4 * i]; py[i] = position[4 * i + 1]; pz[i] = position[4 * i + 2];
            }

            counterValue[0] = 0;
            Check(ClNative.clEnqueueWriteBuffer(queue, bCounter, 1, UIntPtr.Zero, (UIntPtr)4, counterValue, 0, null, IntPtr.Zero), "clear the counter");
            Arg(kBroken, 0, bRest); Arg(kBroken, 1, bCounter); Arg(kBroken, 2, count * 18);
            Run(kBroken, count * 18);
            Check(ClNative.clEnqueueReadBuffer(queue, bCounter, 1, UIntPtr.Zero, (UIntPtr)4, counterValue, 0, null, IntPtr.Zero), "read the count of torn links");
            brokenLinks = counterValue[0] / 2;
            TicksReadback = System.Diagnostics.Stopwatch.GetTimestamp() - f1;
        }

        public void Dispose()
        {
            try
            {
                Release(ref bP); Release(ref bV); Release(ref bQ); Release(ref bO); Release(ref bRest); Release(ref bRest0);
                Release(ref bFixed); Release(ref bCharged); Release(ref bCounter); Release(ref bParams);
                foreach (IntPtr kernel in new[] { kInit, kPredict, kPush, kTear, kRelax, kFlow, kFinish, kBroken })
                    if (kernel != IntPtr.Zero) ClNative.clReleaseKernel(kernel);
                kInit = kPredict = kPush = kTear = kRelax = kFlow = kFinish = kBroken = IntPtr.Zero;
                if (program != IntPtr.Zero) ClNative.clReleaseProgram(program);
                if (queue != IntPtr.Zero) ClNative.clReleaseCommandQueue(queue);
                if (context != IntPtr.Zero) ClNative.clReleaseContext(context);
                program = queue = context = IntPtr.Zero;
            }
            catch (Exception)
            {
                // releasing is best effort
            }
        }
    }
}
