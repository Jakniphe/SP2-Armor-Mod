using System;
using BepInEx.Logging;
using UnityEngine;

namespace ArmorFragments
{
    // Unloading the plugin so that it can be loaded again without a restart (the ScriptEngine does that: it destroys the plugin's
    // object, which calls ArmorFragmentsPlugin.OnDestroy, and loads the new DLL).
    //
    // What a copy of the plugin leaves behind, and where each part is undone:
    //  - Harmony patches on the game      -> SafePatcher.UnpatchAll (only this copy's own, see there)
    //  - the log listener                 -> removed here
    //  - threads                          -> PlateDamageMaps.Shutdown (damage-map worker, simulation pool)
    //  - Unity objects and materials      -> the Shutdown() of RayDrawer, XRayView, ImpactCamera, RackVisualizer, FaceHighlight, ...
    //  - the panel, its button, the designer's section -> ArmorPanel.Shutdown, DesignerSection.Shutdown
    //  - the damage module variables      -> ModuleOutputs.Shutdown
    // Every step has its own try/catch: one that fails is logged and the others still run.
    //
    // The order matters in two places. The ScriptEngine makes the NEW copy before it destroys the old one, so this runs with the new
    // copy's patches already in the game (hence the unpatching by assembly), and the module variables are refreshed after the
    // unpatching so that the old copy does not make them again.
    internal static class Lifecycle
    {
        private static bool done;
        private static bool quitting;

        // Called from Awake. When the whole game is closing the plugin is destroyed too, but then there is nothing to put back (the
        // threads are background threads and everything else goes with the process), so Shutdown does nothing at all.
        public static void Start()
        {
            Application.quitting += OnQuitting;
        }

        private static void OnQuitting()
        {
            quitting = true;
        }

        // The log line says which step failed; the step's own text is the exception's message.
        private static void Step(string name, Action action)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("[LIFECYCLE] Unload step '" + name + "' failed: " +
                                                    e.GetBaseException().GetType().Name + ": " + e.GetBaseException().Message);
            }
        }

        public static void Shutdown()
        {
            if (done) return;
            done = true;
            Application.quitting -= OnQuitting;
            if (quitting) return;

            ManualLogSource log = ArmorFragmentsPlugin.Log;
            if (log == null) return;
            log.LogInfo("Armor Fragments unloading");

            // The settings of a server go back to the player's own before anything else reads them.
            Step("host settings", NetSync.RestoreUser);

            // Things on screen and in the scene.
            Step("impact camera", ImpactCamera.Stop);
            Step("x-ray view", XRayView.Shutdown);
            Step("rays and bubbles", RayDrawer.Shutdown);
            Step("rack view", RackVisualizer.Shutdown);
            Step("face highlight", FaceHighlight.Shutdown);

            // Damage maps: plates back as the game made them, threads stopped.
            Step("damage maps", PlateDamageMaps.Shutdown);

            // The panel, its button and the designer's section.
            Step("panel", UI.ArmorPanel.Shutdown);
            Step("designer section", UI.DesignerSection.Shutdown);

            // The patches, then what depends on them being gone.
            Step("patches", () =>
            {
                if (ArmorFragmentsPlugin.HarmonyInstance == null)
                {
                    log.LogWarning("[LIFECYCLE] No Harmony instance was kept: no patch was removed");
                    return;
                }
                int methods;
                int removed = SafePatcher.UnpatchAll(ArmorFragmentsPlugin.HarmonyInstance,
                                                     typeof(ArmorFragmentsPlugin).Assembly, out methods);
                log.LogInfo("[LIFECYCLE] " + removed + " patch(es) removed from " + methods + " game method(s)");
                ArmorFragmentsPlugin.HarmonyInstance = null;
            });
            Step("module variables", ModuleOutputs.Shutdown);

            log.LogInfo("Armor Fragments unloaded");

            // Last, because the lines above still go through them: the panel's copy of the log and the log source itself.
            Step("log listener", () =>
            {
                if (ArmorFragmentsPlugin.MirrorListener != null)
                    BepInEx.Logging.Logger.Listeners.Remove(ArmorFragmentsPlugin.MirrorListener);
                ArmorFragmentsPlugin.MirrorListener = null;
            });
            Step("log source", () => BepInEx.Logging.Logger.Sources.Remove(log));
        }
    }
}
