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

        private static bool profilerTried;

        public static void Tick()
        {
            Ring[next++ & (Size - 1)] = new Frame { Timestamp = Stopwatch.GetTimestamp(), Seconds = Time.unscaledDeltaTime };
            if (ArmorFragmentsPlugin.PerfSceneReport.Value && !profilerTried)
            {
                profilerTried = true;
                try { FrameProfiler.Install(); }
                catch (System.Exception e) { ArmorFragmentsPlugin.Log.LogWarning("[FRAME] the profiler could not start: " + e.Message); }
            }

            if (ArmorFragmentsPlugin.PerfSceneReport.Value && Time.unscaledTime >= nextSceneReport)
            {
                nextSceneReport = Time.unscaledTime + 15f;
                try { SceneReport(); }
                catch (System.Exception e) { ArmorFragmentsPlugin.Log.LogWarning("[PERF] could not report the scene: " + e.Message); }
            }

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

        // Milliseconds since a Stopwatch.GetTimestamp() value (for timing one section where it happens, outside the Update laps).
        public static double Since(long timestamp)
        {
            return (Stopwatch.GetTimestamp() - timestamp) * 1000.0 / Stopwatch.Frequency;
        }

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

        // ---- Every 15 s: how the frames fare, next to what is in the scene --------------------------
        // Idle or shooting, the line says how long frames take (average, median, worst of the last 15 s) and how many of the things that
        // usually cost frames exist right now. If frame time climbs while one of the counts climbs with it, that is the suspect.
        private static float nextSceneReport = 15f;

        private static int CountActive<T>() where T : Component
        {
            T[] all = Object.FindObjectsByType<T>(FindObjectsSortMode.None);
            int n = 0;
            for (int i = 0; i < all.Length; i++)
            {
                Behaviour b = all[i] as Behaviour;
                Renderer r = all[i] as Renderer;
                if (b != null ? b.isActiveAndEnabled : (r != null ? r.enabled && r.gameObject.activeInHierarchy : all[i].gameObject.activeInHierarchy)) n++;
            }
            return n;
        }

        private static System.Type decalType;
        private static bool decalSearched;

        // DecalProjector lives in the render pipeline's assembly, which the plugin does not reference: found by name, once.
        private static int CountDecals()
        {
            if (!decalSearched)
            {
                decalSearched = true;
                foreach (System.Reflection.Assembly a in System.AppDomain.CurrentDomain.GetAssemblies())
                {
                    try { decalType = a.GetType("UnityEngine.Rendering.Universal.DecalProjector", false); }
                    catch { }
                    if (decalType != null) break;
                }
            }
            if (decalType == null) return -1;
            Object[] all = Object.FindObjectsByType(decalType, FindObjectsSortMode.None);
            int n = 0;
            for (int i = 0; i < all.Length; i++)
            {
                Behaviour b = all[i] as Behaviour;
                if (b != null && b.isActiveAndEnabled) n++;
            }
            return n;
        }

        private static void SceneReport()
        {
            long now = Stopwatch.GetTimestamp();
            List<float> times = new List<float>();
            for (int i = 0; i < Size; i++)
            {
                Frame f = Ring[i];
                if (f.Timestamp >= now - 15 * Stopwatch.Frequency && f.Timestamp <= now) times.Add(f.Seconds);
            }
            if (times.Count == 0) return;
            times.Sort();
            double sum = 0.0;
            for (int i = 0; i < times.Count; i++) sum += times[i];

            long started = Stopwatch.GetTimestamp();
            int trails = 0, tracers = 0;
            TrailRenderer[] trailRenderers = Object.FindObjectsByType<TrailRenderer>(FindObjectsSortMode.None);
            for (int i = 0; i < trailRenderers.Length; i++)
            {
                if (!trailRenderers[i].gameObject.activeInHierarchy) continue;
                trails++;
                if (trailRenderers[i].gameObject.name.StartsWith("Tracer")) tracers++;
            }
            int particles = 0, playing = 0;
            ParticleSystem[] systems = Object.FindObjectsByType<ParticleSystem>(FindObjectsSortMode.None);
            for (int i = 0; i < systems.Length; i++)
            {
                if (!systems[i].gameObject.activeInHierarchy) continue;
                particles++;
                if (systems[i].isPlaying) playing++;
            }

            string line = "[PERF] frames over the last 15 s: " + times.Count + ", average " + (sum / times.Count * 1000.0).ToString("F1") + " ms, median " +
                (times[times.Count / 2] * 1000f).ToString("F1") + " ms, worst " + (times[times.Count - 1] * 1000f).ToString("F1") + " ms; " +
                (Application.isFocused ? "in focus" : "NOT in focus") + "; scene: trails " + trails + " (tracers " + tracers + "), particle systems " + particles +
                " (playing " + playing + "), renderers " + CountActive<Renderer>() + ", colliders " + CountActive<Collider>() + ", rigidbodies " +
                CountActive<Rigidbody>() + ", lights " + CountActive<Light>() + ", cameras " + CountActive<Camera>() + ", decals " +
                CountDecals() + "; time scale " + Time.timeScale.ToString("F2") + ", target fps " +
                Application.targetFrameRate + ", vsync " + QualitySettings.vSyncCount + "; C# heap " + System.GC.GetTotalMemory(false) / (1024 * 1024) +
                " MB; real geometry: " + PlateDamageMaps.GeometryTotals() + "; " + FrameTimes() + "; this count took " + Since(started).ToString("F1") + " ms";
            ArmorFragmentsPlugin.Log.LogInfo(line);
        }

        // Where a frame goes, as Unity's own frame timing says (the main thread, the render thread, the graphics card). 0 = the game does not
        // collect it (then only the sum is known).
        private static readonly FrameTiming[] Timings = new FrameTiming[1];

        private static string FrameTimes()
        {
            try
            {
                FrameTimingManager.CaptureFrameTimings();
                if (FrameTimingManager.GetLatestTimings(1, Timings) < 1) return "no frame timing";
                FrameTiming t = Timings[0];
                return "last frame: main thread " + t.cpuMainThreadFrameTime.ToString("F1") + " ms, render thread " + t.cpuRenderThreadFrameTime.ToString("F1") +
                       " ms, graphics card " + t.gpuFrameTime.ToString("F1") + " ms, whole " + t.cpuFrameTime.ToString("F1") + " ms";
            }
            catch (System.Exception e)
            {
                return "frame timing unavailable (" + e.Message + ")";
            }
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
            long managed = System.GC.GetTotalMemory(false) / (1024 * 1024);   // the C# heap (cheap; Unity's own profiler counters cost hundreds of ms)
            ArmorFragmentsPlugin.Log.LogInfo("[PERF] a simulation of " + e.Particles + " particles on " + (e.Gpu ? "the graphics card" : "the CPU") + " took " +
                e.Ms.ToString("F0") + " ms; frames during it: " + during + ", average " + (duringAverage * 1000f).ToString("F1") + " ms, worst " +
                (duringWorst * 1000f).ToString("F1") + " ms; the 3 s before it: " + before + " frames, average " + (beforeAverage * 1000f).ToString("F1") +
                " ms, worst " + (beforeWorst * 1000f).ToString("F1") + " ms; " + focus + "; C# heap " + managed + " MB");
        }
    }
}
