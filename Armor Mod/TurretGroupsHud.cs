using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Assets.Scripts.Flight;
using Assets.Scripts.Flight.UI;
using Jundroo.Juicy.Widgets;
using UnityEngine;

namespace ArmorFragments
{
    // The turret groups, on the flight screen (bottom right, above the game's row of buttons: recenter view, enter/exit, change view): one bar for
    // each group, made like the game's own pills (the same classes and the same way the ammo bars are made, see AmmoHud.cs). A lit bar is a group
    // that follows the mouse aim now, a dark one is a group that holds where it is; a click on a bar switches it (several groups can be controlled
    // together). Only while the mouse aim is on (with one group too: it shows how many of its turrets are on target or blocked). The cursor is locked in mouse aim: hold Alt to click.
    internal static class TurretGroupsHud
    {
        private const string IdPrefix = "af-groups-";
        private const float RightMargin = 15f;     // like the game's row of buttons (position "-15 15")
        private const float FirstBottom = 95f;     // above the row (50 high; 75 for the buttons that only show on a catapult or an elevator)
        private const float Spacing = 40f;         // a bar is 32 high
        private const float PillWidth = 230f;

        private sealed class Pill
        {
            public Widget Root;
            public ImageWidget Fill;
            public TextWidget Name, State;
            public int Group;
            public int Controlled = -1, Live = -1, OnTarget = -1, Total = -1, Blocked = -1;
        }

        private static readonly List<Pill> pills = new List<Pill>();
        private static readonly List<TurretAim.GroupHud> groups = new List<TurretAim.GroupHud>();
        private static Widget parent;
        private static string signature = "";
        private static float nextAttempt;

        private static readonly Color Controlled = new Color(0.89f, 0.62f, 0.05f, 1f);   // the amber of the ammo bars
        private static readonly Color Held = new Color(0.20f, 0.23f, 0.29f, 1f);
        private static readonly Color Gone = new Color(0.86f, 0.21f, 0.27f, 1f);         // the game's "danger" red

        public static void Tick()
        {
            if (!ArmorFragmentsPlugin.TurretGroupsHudEnabled.Value || !TurretAim.Active)
            {
                if (pills.Count > 0) Clear();
                return;
            }
            try
            {
                TurretAim.GetGroups(groups);
                if (groups.Count < 1)
                {
                    if (pills.Count > 0) Clear();
                    return;
                }

                if (NeedsRebuild())
                {
                    if (Time.unscaledTime < nextAttempt) return;
                    nextAttempt = Time.unscaledTime + 0.5f;
                    Rebuild();
                }
                Refresh();
            }
            catch (Exception e)
            {
                ArmorFragmentsPlugin.Log.LogError("Turret groups display failed, it is switched off: " + e);
                ArmorFragmentsPlugin.TurretGroupsHudEnabled.Value = false;
                Clear();
            }
        }

        private static string SignatureOf()
        {
            StringBuilder text = new StringBuilder();
            foreach (TurretAim.GroupHud group in groups) text.Append(group.Id).Append(':').Append(group.Total).Append(',');
            return text.ToString();
        }

        private static bool NeedsRebuild()
        {
            if (parent == null || pills.Count != groups.Count || signature != SignatureOf()) return true;
            foreach (Pill pill in pills) if (pill.Root == null) return true;
            return false;
        }

        private static void Rebuild()
        {
            Clear();
            FlightSceneScript scene = FlightSceneScript.Instance;
            FlightUIScript ui = scene != null ? scene.FlightUI : null;
            Widget root = ui != null ? ui.RootWidget : null;
            Widget mainUi = root != null ? root.FindWidget("main-ui") : null;
            if (mainUi == null) return;

            RemoveLeftovers(mainUi);
            for (int i = 0; i < groups.Count; i++)
            {
                TurretAim.GroupHud group = groups[i];
                Widget widget = mainUi.Context.CreateWidget(PillXml(i, group.Id), mainUi, mainUi.Stylesheet);
                if (widget == null) continue;
                Pill pill = new Pill
                {
                    Root = widget,
                    Fill = widget.FindWidget<ImageWidget>(IdPrefix + "fill-" + i),
                    Name = widget.FindWidget<TextWidget>(IdPrefix + "name-" + i),
                    State = widget.FindWidget<TextWidget>(IdPrefix + "state-" + i),
                    Group = group.Id,
                };
                if (pill.Name != null) pill.Name.Text = "Group " + group.Id;
                int id = group.Id;
                widget.Clicked += w => TurretAim.ToggleGroup(id);
                pills.Add(pill);
            }
            parent = mainUi;
            signature = SignatureOf();
            ArmorFragmentsPlugin.Log.LogInfo("Turret groups display: " + pills.Count + " bar(s) above the camera buttons");
        }

        // A bar like the game's own (the throttle and fuel pills, as in the ammo bars): the rounded button, the bar that fills, and two texts.
        private static XElement PillXml(int index, int group)
        {
            string xml =
                "<Button id='" + IdPrefix + index + "' class='btn btn-flight' anchor='BottomRight' position='-" +
                RightMargin.ToString("F0", CultureInfo.InvariantCulture) + " " +
                (FirstBottom + index * Spacing).ToString("F0", CultureInfo.InvariantCulture) + "' width='" +
                PillWidth.ToString("F0", CultureInfo.InvariantCulture) + "' mask='EnabledAndShown' tooltipPosition='Left' " +
                "tooltip='Turret group " + group.ToString(CultureInfo.InvariantCulture) + "\\nClick: control it with the mouse aim (or let it hold). Several groups can be controlled together.\\nHold Alt to free the cursor.'>" +
                "<Image id='" + IdPrefix + "fill-" + index + "' sprite='@SpriteWhite' color='#E39E0D' type='Filled' fillMethod='Horizontal' fillAmount='1' />" +
                "<Text id='" + IdPrefix + "name-" + index + "' fontSize='18' alignment='Left' color='White' margin-left='12' />" +
                "<Text id='" + IdPrefix + "state-" + index + "' alignment='Right' color='White' />" +
                "</Button>";
            return XElement.Parse(xml);
        }

        // Each frame: the colors and texts, only touching the widgets when something changed.
        private static void Refresh()
        {
            for (int i = 0; i < pills.Count && i < groups.Count; i++)
            {
                Pill pill = pills[i];
                TurretAim.GroupHud group = groups[i];
                int controlled = group.Controlled ? 1 : 0;
                if (pill.Controlled == controlled && pill.Live == group.Live && pill.OnTarget == group.OnTarget && pill.Total == group.Total && pill.Blocked == group.Blocked) continue;
                pill.Controlled = controlled;
                pill.Live = group.Live;
                pill.OnTarget = group.OnTarget;
                pill.Total = group.Total;
                pill.Blocked = group.Blocked;

                Color color = group.Live == 0 ? Gone : group.Controlled ? Controlled : Held;
                if (pill.Fill != null)
                {
                    pill.Fill.Color.Base = color;
                    pill.Fill.Image.color = color;
                }
                if (pill.State != null)
                {
                    pill.State.Text = group.Live == 0 ? "destroyed"
                        : group.Controlled ? group.OnTarget.ToString(CultureInfo.InvariantCulture) + "/" + group.Live.ToString(CultureInfo.InvariantCulture) + " aimed" +
                                             (group.Blocked > 0 ? " (" + group.Blocked.ToString(CultureInfo.InvariantCulture) + " blocked)" : "")
                        : group.Live.ToString(CultureInfo.InvariantCulture) + " held";
                }
            }
        }

        private static void RemoveLeftovers(Widget mainUi)
        {
            for (int guard = 0; guard < 64; guard++)
            {
                Widget old = null;
                foreach (Widget child in mainUi.Widgets)
                {
                    if (child != null && child.Id != null && child.Id.StartsWith(IdPrefix, StringComparison.Ordinal)) { old = child; break; }
                }
                if (old == null) return;
                old.Destroy();
            }
        }

        private static void Clear()
        {
            foreach (Pill pill in pills)
            {
                try
                {
                    if (pill.Root != null) pill.Root.Destroy();
                }
                catch (Exception) { }
            }
            pills.Clear();
            parent = null;
            signature = "";
        }

        // RELEASE-STRIP-BEGIN
        public static void Shutdown()
        {
            Clear();
        }
        // RELEASE-STRIP-END
    }
}
