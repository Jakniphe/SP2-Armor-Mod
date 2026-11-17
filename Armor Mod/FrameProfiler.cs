using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;
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

        // Which script's Update / LateUpdate / FixedUpdate took the time: every such method of every MonoBehaviour in the game and in the other
        // mods is wrapped by a pair of Harmony patches that add its time to a per-frame table.
        private static Harmony scriptPatches;
        private static readonly Dictionary<string, double> ScriptMs = new Dictionary<string, double>();
        private static readonly Dictionary<string, int> ScriptCalls = new Dictionary<string, int>();
        private static readonly Dictionary<MethodBase, string> ScriptNames = new Dictionary<MethodBase, string>();

        private static void ScriptBefore(out long __state)
        {
            __state = Stopwatch.GetTimestamp();
        }

        private static void ScriptAfter(MethodBase __originalMethod, long __state)
        {
            double ms = (Stopwatch.GetTimestamp() - __state) * 1000.0 / Stopwatch.Frequency;
            string name;
            if (!ScriptNames.TryGetValue(__originalMethod, out name))
                ScriptNames[__originalMethod] = name = __originalMethod.DeclaringType.FullName + "." + __originalMethod.Name;
            double sum;
            ScriptMs.TryGetValue(name, out sum);
            ScriptMs[name] = sum + ms;
            int calls;
            ScriptCalls.TryGetValue(name, out calls);
            ScriptCalls[name] = calls + 1;
        }

        private static void PatchScripts()
        {
            scriptPatches = new Harmony("com.yourname.armorfragments.frameprofiler");
            HarmonyMethod before = new HarmonyMethod(typeof(FrameProfiler).GetMethod("ScriptBefore", BindingFlags.Static | BindingFlags.NonPublic));
            HarmonyMethod after = new HarmonyMethod(typeof(FrameProfiler).GetMethod("ScriptAfter", BindingFlags.Static | BindingFlags.NonPublic));
            string[] names = { "Update", "LateUpdate", "FixedUpdate" };
            int patched = 0;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                string assemblyName = assembly.GetName().Name;
                if (assemblyName.StartsWith("UnityEngine") || assemblyName.StartsWith("System") || assemblyName.StartsWith("mscorlib") ||
                    assemblyName.StartsWith("Mono") || assemblyName.StartsWith("0Harmony") || assemblyName.StartsWith("BepInEx") ||
                    assemblyName.StartsWith("Unity.") || assemblyName.StartsWith("netstandard") || assemblyName.StartsWith("MonoMod") ||
                    assemblyName.StartsWith("Mono.")) continue;
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = Array.FindAll(e.Types, x => x != null); }
                foreach (Type type in types)
                {
                    if (type.IsGenericTypeDefinition || !typeof(MonoBehaviour).IsAssignableFrom(type)) continue;
                    foreach (string name in names)
                    {
                        MethodInfo method = type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                                                           null, Type.EmptyTypes, null);
                        if (method == null || method.IsAbstract || method.GetMethodBody() == null) continue;
                        try { scriptPatches.Patch(method, before, after); patched++; }
                        catch { }
                    }
                }
            }
            ArmorFragmentsPlugin.Log.LogInfo("[FRAME] timing " + patched + " Update/LateUpdate/FixedUpdate methods of scripts");
        }

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
            try { PatchScripts(); }
            catch (Exception e) { ArmorFragmentsPlugin.Log.LogWarning("[FRAME] the scripts could not be timed: " + e.Message); }
            ArmorFragmentsPlugin.Log.LogInfo("[FRAME] profiler on: " + labels.Length + " steps of the player loop are timed; frames over " + SlowMs + " ms are logged");
        }

        public static void Remove()
        {
            if (!installed) return;
            installed = false;
            try { if (scriptPatches != null) scriptPatches.UnpatchSelf(); }
            catch { }
            scriptPatches = null;
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
                ScriptMs.Clear();
                ScriptCalls.Clear();
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
            List<KeyValuePair<string, double>> scripts = new List<KeyValuePair<string, double>>(ScriptMs);
            scripts.Sort((a, b) => b.Value.CompareTo(a.Value));
            StringBuilder who = new StringBuilder();
            for (int k = 0; k < scripts.Count && k < 5; k++)
            {
                if (k > 0) who.Append(", ");
                who.Append(scripts[k].Key).Append(' ').Append(scripts[k].Value.ToString("F0")).Append(" ms (x").Append(ScriptCalls[scripts[k].Key]).Append(')');
            }
            ArmorFragmentsPlugin.Log.LogInfo("[FRAME] " + frameMs.ToString("F0") + " ms: " + text + " | scripts: " + who);
        }
    }
}
