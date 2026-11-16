using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace ArmorFragments
{
    // Does the impact simulation cost the game frames? Every frame's duration is kept for a while; when a simulation ends (the damage
    // worker says when it began and ended), the frames that fell inside it are compared with the three seconds before it and the result
    // is logged as [PERF], with whether the game window had the focus (Unity slows an unfocused game down, and then no frame time means
    // anything) and the process's memory. Costs one array write a frame.
    internal static class PerfMonitor
    {
        private const int Size = 4096;

        private struct Frame
        {
            public long Timestamp;
            public float Seconds;
        }

        // What the worker reports about one simulation.
        public sealed class SimEvent
        {
            public long Start, End;
            public bool Gpu;
            public int Particles;
            public double Ms;
        }

        public static readonly ConcurrentQueue<SimEvent> Events = new ConcurrentQueue<SimEvent>();
        private static readonly Frame[] Ring = new Frame[Size];
        private static int next;

        public static void Tick()
        {
            Ring[next++ & (Size - 1)] = new Frame { Timestamp = Stopwatch.GetTimestamp(), Seconds = Time.unscaledDeltaTime };

            SimEvent simulation;
            while (Events.TryDequeue(out simulation))
            {
                try { Report(simulation); }
                catch (System.Exception e) { ArmorFragmentsPlugin.Log.LogWarning("[PERF] could not report: " + e.Message); }
            }
        }

        // ---- What the plugin's own Update costs, part by part -------------------------------------
        // Begin / Lap / End are called around the parts of ArmorFragmentsPlugin.Update. Every 30 s (of real time) the average and the worst
        // of the whole, and of its three biggest parts, are logged: so a slow frame can be told apart from a slow mod.
        private sealed class Part
        {
            public string Name;
            public double Sum, Worst;
        }

        private static readonly Dictionary<string, Part> Parts = new Dictionary<string, Part>();
        private static long beginAt, lapAt;
        private static double totalSum, totalWorst;
        private static int updates;
        private static float nextUpdateReport = 30f;

        public static void Begin()
        {
            beginAt = lapAt = Stopwatch.GetTimestamp();
        }

        public static void Lap(string name)
        {
            long now = Stopwatch.GetTimestamp();
            double ms = (now - lapAt) * 1000.0 / Stopwatch.Frequency;
            lapAt = now;
            Part part;
            if (!Parts.TryGetValue(name, out part)) Parts[name] = part = new Part { Name = name };
            part.Sum += ms;
            if (ms > part.Worst) part.Worst = ms;
        }

        public static void End()
        {
            double ms = (Stopwatch.GetTimestamp() - beginAt) * 1000.0 / Stopwatch.Frequency;
            totalSum += ms;
            if (ms > totalWorst) totalWorst = ms;
            updates++;

            if (Time.unscaledTime < nextUpdateReport) return;
            nextUpdateReport = Time.unscaledTime + 30f;

            List<Part> list = new List<Part>(Parts.Values);
            list.Sort((a, b) => b.Sum.CompareTo(a.Sum));
            string biggest = "";
            for (int i = 0; i < list.Count && i < 3; i++)
                biggest += (i > 0 ? ", " : "") + list[i].Name + " " + (list[i].Sum / updates).ToString("F3") + " ms (worst " + list[i].Worst.ToString("F1") + ")";
            ArmorFragmentsPlugin.Log.LogInfo("[PERF] the plugin's own Update: average " + (totalSum / updates).ToString("F3") + " ms a frame, worst " +
                totalWorst.ToString("F1") + " ms, over " + updates + " frames; biggest parts: " + biggest);

            Parts.Clear();
            totalSum = 0.0;
            totalWorst = 0.0;
            updates = 0;
        }

        private static void Window(long from, long to, out int frames, out float average, out float worst)
        {
            frames = 0;
            double sum = 0.0;
            worst = 0f;
            for (int i = 0; i < Size; i++)
            {
                Frame f = Ring[i];
                if (f.Timestamp < from || f.Timestamp > to) continue;
                frames++;
                sum += f.Seconds;
                if (f.Seconds > worst) worst = f.Seconds;
            }
            average = frames > 0 ? (float)(sum / frames) : 0f;
        }

        private static void Report(SimEvent e)
        {
            long perSecond = Stopwatch.Frequency;
            int during, before;
            float duringAverage, duringWorst, beforeAverage, beforeWorst;
            Window(e.Start, e.End, out during, out duringAverage, out duringWorst);
            Window(e.Start - 3 * perSecond, e.Start, out before, out beforeAverage, out beforeWorst);

            string focus = Application.isFocused ? "game window in focus" : "game window NOT in focus (frame times mean little)";
            long managed = System.GC.GetTotalMemory(false) / (1024 * 1024);                                   // the C# heap
            long unity = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() / (1024 * 1024);        // Unity's own allocations (meshes, textures...)
            ArmorFragmentsPlugin.Log.LogInfo("[PERF] a simulation of " + e.Particles + " particles on " + (e.Gpu ? "the graphics card" : "the CPU") + " took " +
                e.Ms.ToString("F0") + " ms; frames during it: " + during + ", average " + (duringAverage * 1000f).ToString("F1") + " ms, worst " +
                (duringWorst * 1000f).ToString("F1") + " ms; the 3 s before it: " + before + " frames, average " + (beforeAverage * 1000f).ToString("F1") +
                " ms, worst " + (beforeWorst * 1000f).ToString("F1") + " ms; " + focus + "; C# heap " + managed + " MB, Unity allocated " + unity + " MB");
        }
    }
}
