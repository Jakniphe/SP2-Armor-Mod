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
    // The turret groups, on the flight screen (bottom right, above the game's row of buttons: recenter view, enter/exit, change view). For each
    // group two things, one above the other: a bar made like the game's own pills (the same classes and the same way the ammo bars are made,
    // see AmmoHud.cs) and, right under it, three small buttons: Ground / Air / Both.
    //   The bar: lit = the group follows the mouse aim now, dark = it does not. A click switches it (several groups can be controlled together,
    //   at least one stays).
    //   The three buttons: the group's automatic aim (ground targets, air targets or both). They are dimmed while the group is controlled by
    //   hand; a click on one takes the group off the manual control and sets it to that mode, and a click on the lit one turns it off. Only the
    //   setting exists so far: the aiming itself comes later.
    // Only while the mouse aim is on. The cursor is locked in mouse aim: hold Alt to click.
    internal static class TurretGroupsHud
    {
        private const string IdPrefix = "af-groups-";
        private const float RightMargin = 15f;     // like the game's row of buttons (position "-15 15")
        private const float FirstBottom = 95f;     // above the row (50 high; 75 for the buttons that only show on a catapult or an elevator)
        private const float OptionHeight = 24f;
        private const float OptionGap = 4f;        // between the options and the bar above them, and between the three options
        private const float BarHeight = 32f;
        private const float BlockGap = 10f;        // between one group and the next
        private const float BlockHeight = OptionHeight + OptionGap + BarHeight + BlockGap;
        private const float PillWidth = 230f;
        private const float OptionWidth = (PillWidth - 2f * OptionGap) / 3f;

        private static readonly string[] OptionNames = { "Ground", "Air", "Both" };
        private static readonly TurretAim.GroupAuto[] OptionModes = { TurretAim.GroupAuto.Ground, TurretAim.GroupAuto.Air, TurretAim.GroupAuto.Both };

        private sealed class Pill
        {
            public Widget Root;
            public ImageWidget Fill;
            public TextWidget Name, State;
            public ImageWidget[] OptionFill = new ImageWidget[3];
            public Widget Options;
            public int Group;
            public int Controlled = -1, Live = -1, OnTarget = -1, Total = -1, Blocked = -1, Auto = -1;
        }

        private static readonly List<Pill> pills = new List<Pill>();
        private static readonly List<TurretAim.GroupHud> groups = new List<TurretAim.GroupHud>();
        private static Widget parent;
        private static string signature = "";
        private static float nextAttempt;

        private static readonly Color Controlled = new Color(0.89f, 0.62f, 0.05f, 1f);   // the amber of the ammo bars
        private static readonly Color Held = new Color(0.20f, 0.23f, 0.29f, 1f);
        private static readonly Color Gone = new Color(0.86f, 0.21f, 0.27f, 1f);         // the game's "danger" red
        private static readonly Color OptionOff = new Color(0.20f, 0.23f, 0.29f, 1f);
        private static readonly Color OptionDimmed = new Color(0.11f, 0.12f, 0.15f, 1f); // the group is controlled by hand: the options are put out

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
            foreach (TurretAim.GroupHud group in groups) text.Append(group.Id).Append(':').Append(group.Total).Append(':').Append(group.Name).Append(',');
            return text.ToString();
        }

        private static bool NeedsRebuild()
        {
            if (parent == null || pills.Count != groups.Count || signature != SignatureOf()) return true;
            foreach (Pill pill in pills) if (pill.Root == null || pill.Options == null) return true;
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
                Widget widget = mainUi.Context.CreateWidget(PillXml(i, group), mainUi, mainUi.Stylesheet);
                Widget options = mainUi.Context.CreateWidget(OptionsXml(i), mainUi, mainUi.Stylesheet);
                if (widget == null || options == null) continue;
                Pill pill = new Pill
                {
                    Root = widget,
                    Options = options,
                    Fill = widget.FindWidget<ImageWidget>(IdPrefix + "fill-" + i),
                    Name = widget.FindWidget<TextWidget>(IdPrefix + "name-" + i),
                    State = widget.FindWidget<TextWidget>(IdPrefix + "state-" + i),
                    Group = group.Id,
                };
                if (pill.Name != null) pill.Name.Text = group.Name;
                int id = group.Id;
                widget.Clicked += w => TurretAim.ToggleGroup(id);
                for (int k = 0; k < 3; k++)
                {
                    pill.OptionFill[k] = options.FindWidget<ImageWidget>(IdPrefix + "optfill-" + i + "-" + k);
                    Widget button = options.FindWidget(IdPrefix + "opt-" + i + "-" + k);
                    TurretAim.GroupAuto mode = OptionModes[k];
                    if (button != null) button.Clicked += w => TurretAim.SetAuto(id, mode);
                }
                pills.Add(pill);
            }
            parent = mainUi;
            signature = SignatureOf();
            ArmorFragmentsPlugin.Log.LogInfo("Turret groups display: " + pills.Count + " group(s) above the camera buttons");
        }

        private static string Number(float value)
        {
            return value.ToString("F0", CultureInfo.InvariantCulture);
        }

        private static string Escape(string text)
        {
            return System.Security.SecurityElement.Escape(text ?? "");
        }

        // A bar like the game's own (the throttle and fuel pills, as in the ammo bars): the rounded button, the bar that fills, and two texts.
        private static XElement PillXml(int index, TurretAim.GroupHud group)
        {
            string xml =
                "<Button id='" + IdPrefix + index + "' class='btn btn-flight' anchor='BottomRight' position='-" + Number(RightMargin) + " " +
                Number(FirstBottom + index * BlockHeight + OptionHeight + OptionGap) + "' width='" + Number(PillWidth) + "' mask='EnabledAndShown' tooltipPosition='Left' " +
                "tooltip='Turret group " + Escape(group.Name) + "\\nClick: control it with the mouse aim, or let it hold. Several groups can be controlled together.\\nHold Alt to free the cursor.'>" +
                "<Image id='" + IdPrefix + "fill-" + index + "' sprite='@SpriteWhite' color='#E39E0D' type='Filled' fillMethod='Horizontal' fillAmount='1' />" +
                "<Text id='" + IdPrefix + "name-" + index + "' fontSize='18' alignment='Left' color='White' margin-left='12' />" +
                "<Text id='" + IdPrefix + "state-" + index + "' alignment='Right' color='White' />" +
                "</Button>";
            return XElement.Parse(xml);
        }

        // The three options under the bar, side by side (a horizontal layout like the game's row of buttons).
        private static XElement OptionsXml(int index)
        {
            StringBuilder xml = new StringBuilder();
            xml.Append("<HorizontalLayout id='" + IdPrefix + "opts-" + index + "' anchor='BottomRight' position='-" + Number(RightMargin) + " " + Number(FirstBottom + index * BlockHeight) +
                       "' sizeFitter='Horizontal' spacing='" + Number(OptionGap) + "' childControlHeight='false' childControlWidth='false' childForceExpandHeight='false' childForceExpandWidth='false'>");
            for (int k = 0; k < 3; k++)
            {
                xml.Append("<Button id='" + IdPrefix + "opt-" + index + "-" + k + "' class='btn btn-flight' anchor='BottomRight' type='Simple' width='" + Number(OptionWidth) + "' height='" + Number(OptionHeight) +
                           "' tooltipPosition='Left' tooltip='Automatic aim: " + OptionNames[k].ToLowerInvariant() + " targets\\nA click takes the group off the manual control and sets this mode (the aiming itself comes later); a click on the lit one turns it off.'>" +
                           "<Image id='" + IdPrefix + "optfill-" + index + "-" + k + "' sprite='@SpriteWhite' color='#33393F' type='Filled' fillMethod='Horizontal' fillAmount='1' />" +
                           "<Text fontSize='14' alignment='Center' color='White' text='" + OptionNames[k] + "' />" +
                           "</Button>");
            }
            xml.Append("</HorizontalLayout>");
            return XElement.Parse(xml.ToString());
        }

        // Each frame: the colors and texts, only touching the widgets when something changed.
        private static void Refresh()
        {
            for (int i = 0; i < pills.Count && i < groups.Count; i++)
            {
                Pill pill = pills[i];
                TurretAim.GroupHud group = groups[i];
                int controlled = group.Controlled ? 1 : 0;
                int auto = (int)group.Auto;
                if (pill.Controlled == controlled && pill.Live == group.Live && pill.OnTarget == group.OnTarget && pill.Total == group.Total && pill.Blocked == group.Blocked && pill.Auto == auto) continue;
                pill.Controlled = controlled;
                pill.Live = group.Live;
                pill.OnTarget = group.OnTarget;
                pill.Total = group.Total;
                pill.Blocked = group.Blocked;
                pill.Auto = auto;

                bool automatic = !group.Controlled && group.Auto != TurretAim.GroupAuto.Off;
                Color color = group.Live == 0 ? Gone : group.Controlled || automatic ? Controlled : Held;
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
                        : automatic ? "auto " + group.Auto.ToString().ToLowerInvariant()
                        : group.Live.ToString(CultureInfo.InvariantCulture) + " held";
                }

                // The options: the one the group has is lit; while the group is controlled by hand all three are put out.
                for (int k = 0; k < 3; k++)
                {
                    ImageWidget fill = pill.OptionFill[k];
                    if (fill == null) continue;
                    Color option = group.Controlled ? OptionDimmed : group.Auto == OptionModes[k] ? Controlled : OptionOff;
                    fill.Color.Base = option;
                    fill.Image.color = option;
                }
            }
        }

        private static void RemoveLeftovers(Widget mainUi)
        {
            for (int guard = 0; guard < 128; guard++)
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
                    if (pill.Options != null) pill.Options.Destroy();
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
