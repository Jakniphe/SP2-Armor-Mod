using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using Assets.Scripts.Craft;
using Assets.Scripts.Craft.Parts;
using HarmonyLib;

namespace ArmorFragments
{
    // Log only: when the game kills a vehicle by itself (not through VehicleRules.Kill, which logs its own reason),
    // says why. The game kills a vehicle whenever its structure is rebuilt (after a blast reaches it, or a joint
    // breaks) and fewer than CriticalDamageThreshold (70%) of its starting parts, or of its starting wing area, are
    // still connected to the main cockpit; or when the network tells it the vehicle died. Nothing is changed.
    [HarmonyPatch(typeof(AircraftScript), "MarkAsCriticallyDamaged")]
    internal static class KillDiagnosticsPatch
    {
        private static readonly FieldInfo InitialParts = AccessTools.Field(typeof(AircraftScript), "_initialNonWeaponPartCount");
        private static readonly FieldInfo CurrentWing = AccessTools.Field(typeof(AircraftScript), "_currentWingSurfaceArea");
        private static readonly FieldInfo InitialWing = AccessTools.Field(typeof(AircraftScript), "_initialWingSurfaceArea");

        private static void Prefix(AircraftScript __instance)
        {
            try
            {
                if (__instance == null || __instance.CriticallyDamaged) return;

                string path = Callers();
                if (path.Contains("VehicleRules")) return;   // the mod's own kill: logged by VehicleRules.Kill

                StringBuilder text = new StringBuilder("The game killed vehicle '");
                text.Append(__instance.name).Append("'");
                if (__instance.RemoteAircraft) text.Append(" (another player's copy)");

                int parts = __instance.Parts != null ? __instance.Parts.Count : -1;
                int initialParts = InitialParts != null ? (int)InitialParts.GetValue(__instance) : -1;
                float wing = CurrentWing != null ? (float)CurrentWing.GetValue(__instance) : -1f;
                float initialWing = InitialWing != null ? (float)InitialWing.GetValue(__instance) : -1f;
                float threshold = __instance.CriticalDamageThreshold;

                text.Append(": parts connected to the cockpit ").Append(parts).Append(" of ").Append(initialParts)
                    .Append(" at the start (").Append(Percent(parts, initialParts)).Append(")");
                text.Append(", wing area ").Append(wing.ToString("F2")).Append(" of ").Append(initialWing.ToString("F2"))
                    .Append(" m2 (").Append(Percent(wing, initialWing)).Append(")");
                text.Append("; it dies below ").Append((threshold * 100f).ToString("F0")).Append("% of either");
                text.Append("; armored: ").Append(VehicleRules.IsArmored(__instance) ? "yes" : "no");

                List<string> cut = Disconnected(__instance, 12);
                if (cut.Count > 0) text.Append("; parts no longer connected: ").Append(string.Join(", ", cut.ToArray()));
                text.Append("; called from ").Append(path);
                ArmorFragmentsPlugin.Log.LogWarning(text.ToString());
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Couldn't describe why the game killed a vehicle: " + e.Message);
            }
        }

        private static string Percent(float value, float of)
        {
            if (!(of > 0f)) return "no starting amount";
            return (value / of * 100f).ToString("F0") + "%";
        }

        // Parts of the vehicle that the game no longer counts as connected to the main cockpit.
        private static List<string> Disconnected(AircraftScript aircraft, int max)
        {
            List<string> names = new List<string>();
            if (aircraft.Aircraft == null || aircraft.Aircraft.Assembly == null) return names;
            int more = 0;
            foreach (PartData part in aircraft.Aircraft.Assembly.Parts)
            {
                if (part == null || part.PartScript == null || part.PartScript.ConnectedToMainCockpit) continue;
                if (names.Count < max) names.Add("'" + part.Name + "' #" + part.Id);
                else more++;
            }
            if (more > 0) names.Add("and " + more + " more");
            return names;
        }

        // The game methods that led here, nearest first.
        private static string Callers()
        {
            StackFrame[] frames = new StackTrace(2, false).GetFrames();
            List<string> names = new List<string>();
            if (frames != null)
                foreach (StackFrame frame in frames)
                {
                    MethodBase method = frame.GetMethod();
                    if (method == null || method.DeclaringType == null) continue;
                    names.Add(method.DeclaringType.Name + "." + method.Name);
                    if (names.Count >= 5) break;
                }
            return string.Join(" < ", names.ToArray());
        }
    }
}
