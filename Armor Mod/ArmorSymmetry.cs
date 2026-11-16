using System;
using System.Collections.Generic;
using Assets.Scripts.Craft.Parts;
using Assets.Scripts.Craft.Parts.Modifiers;
using Assets.Scripts.Design;
using Assets.Scripts.Design.Symmetry;
using HarmonyLib;
using UnityEngine;

namespace ArmorFragments
{
    // Armor and the designer's symmetry.
    //  - A mirrored copy (mirror symmetry) is the original flipped along its own left-right axis (the game negates
    //    the fuselage's offset and mirrors its sections), so its Left and Right faces swap: the original's Right face
    //    is the copy's Left. Radial copies are only turned, and keep their faces.
    //  - While the designer's symmetry is on, armor edits reach the parts linked to the edited one by symmetry: each
    //    gets the same armor (and a cutter's hole wall armor), a mirrored one with Left and Right swapped. Whether a
    //    partner is mirrored comes from its shape (a mirrored fuselage has its sideways offset negated) or, when
    //    that can't tell, from the symmetry mode (mirror: mirrored; radial: turned).
    internal static class ArmorSymmetry
    {
        private static readonly List<PartData> Partners = new List<PartData>();

        // The armor with the two faces along one of the block's axes swapped (0: left/right, 1: top/bottom,
        // 2: front/back).
        public static ArmorTag Swapped(ArmorTag armor, int axis)
        {
            if (armor == null) return null;
            ArmorTag copy = new ArmorTag
            {
                Thickness = armor.Thickness,
                Hardness = armor.Hardness,
                Brittleness = armor.Brittleness,
                Faces = armor.Faces != null ? (float[])armor.Faces.Clone() : null,
            };
            if (copy.Faces == null || axis < 0) return copy;

            int a, b;
            switch (axis)
            {
                case 0: a = (int)BlockFace.Left; b = (int)BlockFace.Right; break;
                case 1: a = (int)BlockFace.Top; b = (int)BlockFace.Bottom; break;
                default: a = (int)BlockFace.Front; b = (int)BlockFace.Back; break;
            }
            float keep = copy.Faces[a];
            copy.Faces[a] = copy.Faces[b];
            copy.Faces[b] = keep;
            return copy;
        }

        // Gives the parts linked to 'part' by symmetry the same armor and hole wall armor as 'tags'. Returns how
        // many were changed.
        public static int SyncPartners(PartScript part, PartTags tags)
        {
            if (part == null || part.Part == null || part.Part.SymmetryId == 0 || part.Aircraft == null ||
                part.Aircraft.Aircraft == null || part.Aircraft.Aircraft.Assembly == null) return 0;

            // With symmetry off the designer edits only the selected part; so does this.
            Designer designer = Designer.Instance;
            if (designer == null || designer.Symmetry == null || designer.Symmetry.Mode == SymmetryMode.Disabled) return 0;
            bool mirrorMode = designer.Symmetry.Mode == SymmetryMode.Mirrored;

            Partners.Clear();
            part.Aircraft.Aircraft.Assembly.GetOtherSymmetricParts(part.Part, Partners);
            int changed = 0;
            foreach (PartData partner in Partners)
            {
                if (partner == null || partner.PartScript == null || partner == part.Part) continue;
                bool mirrored = MirroredByShape(part.Part, partner) ?? mirrorMode;
                PartTags theirs = TagCodec.ParseCopy(partner.Name);
                theirs.Armor = Swapped(tags.Armor, mirrored ? 0 : -1);
                theirs.Hole = tags.Hole != null ? new HoleTag { Thickness = tags.Hole.Thickness } : null;
                if (theirs.Armor != null)
                {
                    // Armor can't also be a damage module, ammo rack or cook-off vent (as on the edited part).
                    theirs.Module = null;
                    theirs.AmmoRack = null;
                    theirs.Vent = null;
                }

                string name = TagCodec.Compose(theirs, partner.PartType.Name);
                if (name == partner.Name) continue;
                Rename(partner, name);
                changed++;
            }
            Partners.Clear();
            return changed;
        }

        // Gives the cannons linked to 'part' by symmetry the same cannon settings as 'tags' (shell hardness, fuse,
        // filler, brittleness, the Cannon mark itself) and the matching racks: a rack with symmetry partners of its
        // own becomes, for each partner gun, the copy of that rack nearest to it (left gun, left rack); a rack
        // without partners stays shared. Returns how many were changed.
        public static int SyncCannonPartners(PartScript part, PartTags tags)
        {
            if (part == null || part.Part == null || part.Part.SymmetryId == 0 || part.Aircraft == null ||
                part.Aircraft.Aircraft == null || part.Aircraft.Aircraft.Assembly == null) return 0;
            Designer designer = Designer.Instance;
            if (designer == null || designer.Symmetry == null || designer.Symmetry.Mode == SymmetryMode.Disabled) return 0;

            Assembly assembly = part.Aircraft.Aircraft.Assembly;
            List<PartData> partners = new List<PartData>();
            assembly.GetOtherSymmetricParts(part.Part, partners);
            int changed = 0;
            foreach (PartData partner in partners)
            {
                if (partner == null || partner.PartScript == null || partner == part.Part || !IsCannon(partner)) continue;
                PartTags theirs = TagCodec.ParseCopy(partner.Name);
                if (tags.Cannon == null)
                {
                    theirs.Cannon = null;
                }
                else
                {
                    CannonTag cannon = new CannonTag
                    {
                        ShellHardness = tags.Cannon.ShellHardness,
                        Fuse = tags.Cannon.Fuse,
                        FillerPercent = tags.Cannon.FillerPercent,
                        Brittleness = tags.Cannon.Brittleness,
                    };
                    cannon.Racks.AddRange(MapRacks(assembly, tags.Cannon.Racks, partner.PartScript.transform.position));
                    theirs.Cannon = cannon;
                }

                string name = TagCodec.Compose(theirs, partner.PartType.Name);
                if (name == partner.Name) continue;
                Rename(partner, name);
                changed++;
            }
            return changed;
        }

        // Each of a cannon's racks that has symmetry copies becomes the copy nearest to the cannon (left gun, left
        // rack). A cannon listing several racks of one symmetric set keeps them all: it draws from each, nearest first.
        public static void MatchRacks(PartData cannonPart)
        {
            if (cannonPart == null || cannonPart.PartScript == null || cannonPart.PartScript.Aircraft == null) return;
            CannonTag cannon = TagCodec.Parse(cannonPart.Name).Cannon;
            if (cannon == null || cannon.Racks.Count == 0) return;
            Assembly assembly = cannonPart.PartScript.Aircraft.Aircraft != null ? cannonPart.PartScript.Aircraft.Aircraft.Assembly : null;
            if (assembly == null) return;

            List<int> ids = MapRacks(assembly, cannon.Racks, cannonPart.PartScript.transform.position);
            string name = TagCodec.WithCannonRacks(cannonPart.Name, ids);
            if (name != cannonPart.Name) Rename(cannonPart, name);
        }

        // A part dropped or connected in the designer: a marked cannon (and its symmetry copies) matches its racks; an
        // ammo rack makes the cannons that use it or its copies match theirs. Any other part: nothing.
        public static void RematchAround(PartData part)
        {
            if (part == null || part.PartScript == null || part.PartScript.Aircraft == null ||
                part.PartScript.Aircraft.Aircraft == null) return;
            PartTags tags = TagCodec.Parse(part.Name);
            if (tags.Cannon != null)
            {
                MatchRacks(part);
                if (part.SymmetryId != 0)
                {
                    List<PartData> copies = new List<PartData>();
                    part.PartScript.Aircraft.Aircraft.Assembly.GetOtherSymmetricParts(part, copies);
                    foreach (PartData copy in copies)
                        if (copy != part) MatchRacks(copy);
                }
            }
            else if (tags.AmmoRack != null && tags.Armor == null)
            {
                MatchCannonsOfRack(part);
            }
        }

        // The cannons whose racks include this rack or one of its symmetry copies: their racks are matched again.
        public static void MatchCannonsOfRack(PartData rackPart)
        {
            if (rackPart == null || rackPart.PartScript == null || rackPart.PartScript.Aircraft == null ||
                rackPart.PartScript.Aircraft.Aircraft == null) return;
            Assembly assembly = rackPart.PartScript.Aircraft.Aircraft.Assembly;
            if (assembly == null) return;
            List<PartData> set = new List<PartData>();
            if (rackPart.SymmetryId != 0) assembly.GetOtherSymmetricParts(rackPart, set);
            if (!set.Contains(rackPart)) set.Add(rackPart);

            foreach (PartData data in assembly.Parts)
            {
                if (data == null) continue;
                CannonTag cannon = TagCodec.Parse(data.Name).Cannon;
                if (cannon == null) continue;
                foreach (PartData rack in set)
                {
                    if (!cannon.Racks.Contains(rack.Id)) continue;
                    MatchRacks(data);
                    break;
                }
            }
        }

        // The racks for a cannon at 'near': a rack with symmetry copies becomes the copy nearest to it, unless the
        // list holds more than one rack of that symmetric set (then they are all kept).
        private static List<int> MapRacks(Assembly assembly, List<int> racks, Vector3 near)
        {
            Dictionary<uint, int> perSet = new Dictionary<uint, int>();   // symmetry id -> racks of that set listed
            Dictionary<int, PartData> byId = new Dictionary<int, PartData>();
            foreach (PartData data in assembly.Parts)
                if (data != null && racks.Contains(data.Id)) byId[data.Id] = data;
            foreach (int id in racks)
            {
                PartData rack;
                if (!byId.TryGetValue(id, out rack) || rack.SymmetryId == 0) continue;
                int count;
                perSet.TryGetValue(rack.SymmetryId, out count);
                perSet[rack.SymmetryId] = count + 1;
            }

            List<int> ids = new List<int>();
            foreach (int id in racks)
            {
                PartData rack;
                bool alone = byId.TryGetValue(id, out rack) && rack.SymmetryId != 0 && perSet[rack.SymmetryId] == 1;
                int mapped = alone ? MatchingRack(assembly, id, near) : id;
                if (!ids.Contains(mapped)) ids.Add(mapped);
            }
            return ids;
        }

        private static bool IsCannon(PartData part)
        {
            foreach (var modifier in part.Modifiers)
                if (modifier is Assets.Scripts.Craft.Parts.Modifiers.Weapons.CannonData) return true;
            return false;
        }

        // The rack (by id) among 'rackId' and its symmetry partners nearest to 'near'.
        private static int MatchingRack(Assembly assembly, int rackId, Vector3 near)
        {
            PartData rack = null;
            foreach (PartData data in assembly.Parts)
                if (data != null && data.Id == rackId) { rack = data; break; }
            if (rack == null || rack.PartScript == null || rack.SymmetryId == 0) return rackId;

            List<PartData> group = new List<PartData>();
            assembly.GetOtherSymmetricParts(rack, group);
            if (!group.Contains(rack)) group.Add(rack);
            PartData best = rack;
            float bestDistance = float.MaxValue;
            foreach (PartData candidate in group)
            {
                if (candidate == null || candidate.PartScript == null) continue;
                float distance = (candidate.PartScript.transform.position - near).sqrMagnitude;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = candidate;
                }
            }
            return best.Id;
        }

        // Whether 'other' is a mirrored copy of 'part', from their fuselage shapes: the game negates a mirrored copy's
        // sideways offset. Null when that can't tell (no sideways offset, or not fuselages): the designer's symmetry
        // mode decides then.
        private static bool? MirroredByShape(PartData part, PartData other)
        {
            JFuselageData a = FuselageData(part);
            JFuselageData b = FuselageData(other);
            if (a == null || b == null) return null;
            float x = a.Offset.x;
            float y = b.Offset.x;
            if (Mathf.Abs(x) < 0.0001f) return null;
            if (Mathf.Abs(x + y) < 0.001f) return true;
            if (Mathf.Abs(x - y) < 0.001f) return false;
            return null;
        }

        private static JFuselageData FuselageData(PartData part)
        {
            foreach (var modifier in part.Modifiers)
            {
                JFuselageData data = modifier as JFuselageData;
                if (data != null) return data;
            }
            return null;
        }

        internal static void Rename(PartData part, string name)
        {
            part.Name = name;
            foreach (var modifier in part.Modifiers) modifier.OnPartNameChanged(name);
        }
    }

    // A part copied by mirror symmetry: its per-face armor's Left and Right swap, as its shape is flipped along its
    // own left-right axis.
    [HarmonyPatch(typeof(SymmetryUtility), "DuplicatePart", new Type[] { typeof(PartData), typeof(bool) })]
    internal static class MirroredArmorPatch
    {
        private static void Postfix(PartData __result, bool mirrored)
        {
            if (!mirrored || __result == null) return;
            try
            {
                PartTags tags = TagCodec.ParseCopy(__result.Name);
                if (tags.Armor == null || !tags.Armor.PerFace) return;
                tags.Armor = ArmorSymmetry.Swapped(tags.Armor, 0);
                ArmorSymmetry.Rename(__result, TagCodec.Compose(tags, __result.PartType.Name));
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogWarning("Couldn't mirror the copy's armor: " + e.Message);
            }
        }
    }
}
