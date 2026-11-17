using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using UnityEngine.LowLevel;

namespace ArmorFragments
{
    // A tiny profiler for the main thread, for finding what a slow frame is waiting on when neither the graphics card nor the processor is busy.
    // It puts a marker in front of every step of Unity's player loop (FixedUpdate, Update, PostLateUpdate and their parts); the time from one marker
    // to the next is the time of the step in between. A frame that takes longer than SlowMs is logged with its slowest steps:
    //
    //   [FRAME] 1432 ms: PostLateUpdate.FinishFrameRendering 1380 ms (x1), FixedUpdate.ScriptRunBehaviourFixedUpdate 20 ms (x6), ...
    //
    // Only installed when Debug/PerfSceneReport is on (the markers cost a few microseconds a frame). Rendering done by the render thread shows up
    // as time in the step where the main thread waits for it.
    internal static class FrameProfiler
    {
        private const double SlowMs = 120.0;
        private static string[] labels = new string[0];
        private static double[] sums = new double[0];
        private static int[] counts = new int[0];
        private static int last = -1;
        private static long lastAt, frameStart;
        private static long lastLog;
        private static bool installed;

        public static void Install()
        {
            if (installed) return;
            PlayerLoopSystem root = PlayerLoop.GetCurrentPlayerLoop();
            List<string> names = new List<string>();
            PlayerLoopSystem[] groups = root.subSystemList;
            if (groups == null) return;

            for (int g = 0; g < groups.Length; g++)
            {
                PlayerLoopSystem group = groups[g];
                if (group.subSystemList == null) continue;
                List<PlayerLoopSystem> children = new List<PlayerLoopSystem>();
                foreach (PlayerLoopSystem child in group.subSystemList)
                {
                    if (child.type != null && child.type.Name == "FrameProfiler") continue;   // a copy of this plugin from before a reload
                    int id = names.Count;
                    names.Add(group.type.Name + "." + (child.type != null ? child.type.Name : "?"));
                    children.Add(new PlayerLoopSystem { type = typeof(FrameProfiler), updateDelegate = () => Mark(id) });
                    children.Add(child);
                }
                group.subSystemList = children.ToArray();
                groups[g] = group;
            }
            root.subSystemList = groups;
            labels = names.ToArray();
            sums = new double[labels.Length];
            counts = new int[labels.Length];
            last = -1;
            PlayerLoop.SetPlayerLoop(root);
            installed = true;
            ArmorFragmentsPlugin.Log.LogInfo("[FRAME] profiler on: " + labels.Length + " steps of the player loop are timed; frames over " + SlowMs + " ms are logged");
        }

        public static void Remove()
        {
            if (!installed) return;
            installed = false;
            PlayerLoopSystem root = PlayerLoop.GetCurrentPlayerLoop();
            if (root.subSystemList == null) return;
            PlayerLoopSystem[] groups = root.subSystemList;
            for (int g = 0; g < groups.Length; g++)
            {
                if (groups[g].subSystemList == null) continue;
                List<PlayerLoopSystem> children = new List<PlayerLoopSystem>();
                foreach (PlayerLoopSystem child in groups[g].subSystemList)
                    if (child.type != typeof(FrameProfiler)) children.Add(child);
                groups[g].subSystemList = children.ToArray();
            }
            root.subSystemList = groups;
            PlayerLoop.SetPlayerLoop(root);
        }

        private static void Mark(int id)
        {
            long now = Stopwatch.GetTimestamp();
            if (last >= 0)
            {
                sums[last] += (now - lastAt) * 1000.0 / Stopwatch.Frequency;
                counts[last]++;
            }
            if (id == 0 && frameStart != 0)
            {
                double frameMs = (now - frameStart) * 1000.0 / Stopwatch.Frequency;
                if (frameMs > SlowMs && now - lastLog > 2 * Stopwatch.Frequency) Report(frameMs);
                Array.Clear(sums, 0, sums.Length);
                Array.Clear(counts, 0, counts.Length);
                frameStart = now;
            }
            else if (frameStart == 0)
            {
                frameStart = now;
            }
            last = id;
            lastAt = now;
        }

        private static void Report(double frameMs)
        {
            lastLog = Stopwatch.GetTimestamp();
            List<int> order = new List<int>();
            for (int i = 0; i < sums.Length; i++) if (counts[i] > 0) order.Add(i);
            order.Sort((a, b) => sums[b].CompareTo(sums[a]));
            StringBuilder text = new StringBuilder();
            for (int k = 0; k < order.Count && k < 6; k++)
            {
                int i = order[k];
                if (k > 0) text.Append(", ");
                text.Append(labels[i]).Append(' ').Append(sums[i].ToString("F0")).Append(" ms (x").Append(counts[i]).Append(')');
            }
            ArmorFragmentsPlugin.Log.LogInfo("[FRAME] " + frameMs.ToString("F0") + " ms: " + text);
        }
    }
}
