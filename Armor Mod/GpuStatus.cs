using System;
using System.Threading;
using ArmorFragments.Sim;

namespace ArmorFragments
{
    // Is there a graphics card the impact simulation can run on (through OpenCL)? Found out once, in a thread of its own (a driver can
    // take its time, and the game must never wait for it), and told in the log and the panel. The setting UseGpu is the player's wish;
    // when the card cannot be used the CPU does the work, as it always did.
    internal static class GpuStatus
    {
        private static Thread thread;
        private static volatile ClProbeResult result;
        private static volatile bool logged;
        private static volatile bool stopping;

        // What the panel shows.
        public static volatile string Text = "not checked yet";

        // True when the card was found and tested (and the player wants it).
        public static bool Usable
        {
            get { return ArmorFragmentsPlugin.UseGpu.Value && result != null && result.Available; }
        }

        public static void Start()
        {
            if (thread != null) return;
            if (!ArmorFragmentsPlugin.UseGpu.Value)
            {
                Text = "off (the setting)";
                return;
            }

            Text = "checking...";
            thread = new Thread(Probe) { IsBackground = true, Name = "ArmorFragments gpu probe", Priority = ThreadPriority.BelowNormal };
            thread.Start();
        }

        private static void Probe()
        {
            ClProbeResult found = ClProbe.Run();
            if (stopping) return;
            result = found;
            Text = found.Available ? "OpenCL on " + found.Device + " (" + found.ComputeUnits + " CUs, " + found.MemoryMb + " MB)" : "CPU (" + found.Error + ")";
        }

        // Every frame (main thread): the log line, once, when the thread is done (the log is not written from other threads).
        public static void Tick()
        {
            if (logged || result == null) return;
            logged = true;
            ClProbeResult found = result;
            if (found.Available) ArmorFragmentsPlugin.Log.LogInfo("[GPU] " + found.Summary());
            else ArmorFragmentsPlugin.Log.LogInfo("[GPU] the graphics card cannot be used, the CPU does the impact simulation: " + found.Error +
                                                  (found.DeviceList.Length > 0 ? " (OpenCL devices: " + found.DeviceList + ")" : ""));
            if (found.Available && found.DeviceList.Contains(";")) ArmorFragmentsPlugin.Log.LogInfo("[GPU] OpenCL devices: " + found.DeviceList);
        }

        // The plugin is being unloaded: a probe still running is let go (it is a background thread and holds nothing the game uses).
        public static void Shutdown()
        {
            stopping = true;
            result = null;
        }
    }
}
