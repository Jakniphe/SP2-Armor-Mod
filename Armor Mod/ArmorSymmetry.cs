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
