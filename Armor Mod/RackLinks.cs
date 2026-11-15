using System;
using System.Collections.Generic;
using Assets.Scripts.Craft.Parts;
using Assets.Scripts.Craft.Parts.Modifiers;
using Assets.Scripts.Design;
using Assets.Scripts.Design.Symmetry;
using Assets.Scripts.Design.Tools;
using HarmonyLib;

namespace ArmorFragments
{
    // A cannon names its ammo racks by part id ([CAN r=]). The game gives parts new ids when they are added (a
    // subassembly or a paste), duplicated, or made symmetric, and tells the parts that point at other parts
    // through IPartIDChangedListener (wheels and their engines, targeting parts). A part's name doesn't get that,
    // so these patches do the same for [CAN r=]: ids in the game's old -> new map are rewritten, the others are
    // kept, as the game does for its own links. A deleted part is taken out of every cannon's list, so a part
    // that later gets its id isn't picked up.
    internal static class RackLinks
    {
        // Rewrites the rack ids in this part's [CAN r=] through 'idMap' (old id -> new id).
        public static void Remap(PartData part, Dictionary<int, int> idMap)
        {
            if (part == null || idMap == null || idMap.Count == 0) return;
            CannonTag cannon = TagCodec.Parse(part.Name).Cannon;
            if (cannon == null || cannon.Racks.Count == 0) return;

            List<int> ids = new List<int>();
            bool changed = false;
            foreach (int id in cannon.Racks)
            {
                int newId;
                int kept = idMap.TryGetValue(id, out newId) ? newId : id;
                if (kept != id) changed = true;
                if (!ids.Contains(kept)) ids.Add(kept);
            }
            if (changed) SetName(part, TagCodec.WithCannonRacks(part.Name, ids));
        }

        // Takes 'removedId' out of the rack list of every cannon among 'parts'.
        public static void Forget(IEnumerable<PartData> parts, int removedId)
        {
            foreach (PartData part in parts)
            {
                if (part == null) continue;
                CannonTag cannon = TagCodec.Parse(part.Name).Cannon;
                if (cannon == null || !cannon.Racks.Contains(removedId)) continue;

                List<int> ids = new List<int>(cannon.Racks);
                ids.Remove(removedId);
                SetName(part, TagCodec.WithCannonRacks(part.Name, ids));
            }
        }

        // Sets a part's name the way the game's name field does.
        public static void SetName(PartData part, string name)
        {
            if (part.Name == name) return;
            part.Name = name;
            foreach (PartModifierData modifier in part.Modifiers)
            {
                try
                {
                    modifier.OnPartNameChanged(name);
                }
                catch (Exception)
                {
                    // a modifier that isn't set up yet: it reads the name when it is
                }
            }
        }
    }

    // Parts added to the craft (a subassembly, a paste): the game renumbers them all.
    [HarmonyPatch(typeof(Assembly), "Absorb")]
    internal static class RackLinksAbsorbPatch
    {
        // The absorbed parts with their ids before the game renumbers them.
        private static void Prefix(Assembly assembly, out List<KeyValuePair<PartData, int>> __state)
        {
            __state = null;
            try
            {
                if (assembly == null) return;
                __state = new List<KeyValuePair<PartData, int>>();
                foreach (PartData part in assembly.Parts)
                    if (part != null) __state.Add(new KeyValuePair<PartData, int>(part, part.Id));
            }
            catch (Exception e)
            {
                __state = null;
                ArmorFragmentsPlugin.Log.LogWarning("Ammo rack links (added parts) failed: " + e.Message);
            }
        }

        private static void Postfix(List<KeyValuePair<PartData, int>> __state)
        {
            if (__state == null || __state.Count == 0) return;
            try
            {
                Dictionary<int, int> idMap = new Dictionary<int, int>();
                foreach (KeyValuePair<PartData, int> pair in __state) idMap[pair.Value] = pair.Key.Id;
                foreach (KeyValuePair<PartData, int> pair in __state) RackLinks.Remap(pair.Key, idMap);
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Ammo rack links (added parts) failed: " + e.Message);
            }
        }
    }

    // Duplicated parts (clone): copy i of the result is part i of the selection.
    [HarmonyPatch(typeof(SymmetryUtility), "DuplicateParts")]
    internal static class RackLinksDuplicatePatch
    {
        private static void Prefix(PartSelection partSelection, out List<int> __state)
        {
            __state = null;
            try
            {
                if (partSelection == null || partSelection.Parts == null) return;
                __state = new List<int>();
                foreach (PartScript part in partSelection.Parts)
                    __state.Add(part != null && part.Part != null ? part.Part.Id : -1);
            }
            catch (Exception e)
            {
                __state = null;
                ArmorFragmentsPlugin.Log.LogWarning("Ammo rack links (duplicated parts) failed: " + e.Message);
            }
        }

        private static void Postfix(List<PartData> __result, List<int> __state)
        {
            if (__result == null || __state == null || __state.Count != __result.Count) return;
            try
            {
                Dictionary<int, int> idMap = new Dictionary<int, int>();
                for (int i = 0; i < __state.Count; i++)
                    if (__state[i] >= 0 && __result[i] != null) idMap[__state[i]] = __result[i].Id;
                foreach (PartData copy in __result) RackLinks.Remap(copy, idMap);
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Ammo rack links (duplicated parts) failed: " + e.Message);
            }
        }
    }

    // Symmetric copies: copy i of a cannon points at copy i of its racks, if those are symmetric too.
    [HarmonyPatch(typeof(SymmetryUtility), "RemapSymmetricPartIDs")]
    internal static class RackLinksSymmetryPatch
    {
        private static void Postfix(IReadOnlyList<ValueTuple<PartData, IReadOnlyList<PartData>>> symmetricPartGroups)
        {
            if (symmetricPartGroups == null || symmetricPartGroups.Count == 0) return;
            try
            {
                int copies = 0;
                foreach (ValueTuple<PartData, IReadOnlyList<PartData>> group in symmetricPartGroups)
                    if (group.Item2 != null && group.Item2.Count > copies) copies = group.Item2.Count;

                for (int i = 0; i < copies; i++)
                {
                    Dictionary<int, int> idMap = new Dictionary<int, int>();
                    foreach (ValueTuple<PartData, IReadOnlyList<PartData>> group in symmetricPartGroups)
                        if (group.Item1 != null && group.Item2 != null && i < group.Item2.Count && group.Item2[i] != null)
                            idMap[group.Item1.Id] = group.Item2[i].Id;
                    if (idMap.Count == 0) continue;

                    foreach (ValueTuple<PartData, IReadOnlyList<PartData>> group in symmetricPartGroups)
                        if (group.Item2 != null && i < group.Item2.Count) RackLinks.Remap(group.Item2[i], idMap);
                }
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Ammo rack links (symmetry) failed: " + e.Message);
            }
        }
    }

    // A part deleted in the designer.
    [HarmonyPatch(typeof(Assembly), "RemovePart")]
    internal static class RackLinksRemovePatch
    {
        private static void Postfix(Assembly __instance, PartData part)
        {
            if (part == null) return;
            try
            {
                Designer designer = Designer.Instance;
                if (designer == null || designer.Aircraft == null || designer.Aircraft.Aircraft == null ||
                    designer.Aircraft.Aircraft.Assembly != __instance) return;
                RackLinks.Forget(__instance.Parts, part.Id);
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Ammo rack links (deleted part) failed: " + e.Message);
            }
        }
    }
}
