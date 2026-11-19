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
    // see AmmoHud.cs) and, right under it, three small buttons: Ground / Air / Both. They are made like the other pills of the flight screen:
    // dark and half transparent with rounded corners (the game's "btn btn-flight" look, clipped by the button's mask), and blue (the game's
    // primary color) when lit; there is no fill of their own while they are off.
    //   The bar: blue = the group follows the mouse aim now (or is on an automatic mode), dark = it holds. A click switches it (several groups
    //   can be controlled together, at least one stays).
    //   The three buttons: the group's automatic aim (ground targets, air targets or both). They are put out (darker) while the group is
    //   controlled by hand; a click on one takes the group off the manual control and sets it to that mode, and a click on the lit one turns it
    //   off. Only the setting exists so far: the aiming itself comes later.
    // Everything sits in ONE vertical layout, so the groups stack by themselves (their heights never have to be guessed). Only while the mouse aim
    // is on. The cursor is locked in mouse aim: hold Alt to click.
    internal static class TurretGroupsHud
    {
        private const string IdPrefix = "af-groups-";
        private const float RightMargin = 15f;     // like the game's row of buttons (position "-15 15")
        private const float FirstBottom = 95f;     // above the row (50 high; 75 for the buttons that only show on a catapult or an elevator)
        private const float OptionHeight = 24f;
        private const float Gap = 4f;              // between the bar and its options, and between the three options
        private const float BarHeight = 32f;
        private const float BlockGap = 10f;        // between one group and the next
        private const float PillWidth = 230f;
        private const float OptionWidth = (PillWidth - 2f * Gap) / 3f;

        private static readonly string[] OptionNames = { "Ground", "Air", "Both" };
        private static readonly TurretAim.GroupAuto[] OptionModes = { TurretAim.GroupAuto.Ground, TurretAim.GroupAuto.Air, TurretAim.GroupAuto.Both };

        private sealed class Pill
        {
            public ImageWidget Fill;
            public TextWidget Name, State;
            public ImageWidget[] OptionFill = new ImageWidget[3];
            public int Group;
            public int Controlled = -1, Live = -1, OnTarget = -1, Total = -1, Blocked = -1, Auto = -1;
        }

        private static readonly List<Pill> pills = new List<Pill>();
        private static readonly List<TurretAim.GroupHud> groups = new List<TurretAim.GroupHud>();
        private static Widget parent;
        private static Widget layoutRoot;   // the one vertical layout that holds every bar and every row of options
        private static string signature = "";
        private static float nextAttempt;

        // The game's colors (its "ColorPrimary" #28599E and "ColorDanger" #DC3545): a lit bar or option is filled with the blue, an off one has no fill
        // (so the button's own dark half-transparent look shows), a put-out option is darkened with half-transparent black.
        private static readonly Color Blue = new Color(0x28 / 255f, 0x59 / 255f, 0x9E / 255f, 1f);
        private static readonly Color Gone = new Color(0xDC / 255f, 0x35 / 255f, 0x45 / 255f, 1f);
        private static readonly Color PutOut = new Color(0f, 0f, 0f, 0.5f);

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
            return layoutRoot == null;
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
            Widget layout = mainUi.Context.CreateWidget(LayoutXml(), mainUi, mainUi.Stylesheet);
            if (layout == null) return;
            for (int i = 0; i < groups.Count; i++)
            {
                TurretAim.GroupHud group = groups[i];
                Pill pill = new Pill
                {
                    Fill = layout.FindWidget<ImageWidget>(IdPrefix + "fill-" + i),
                    Name = layout.FindWidget<TextWidget>(IdPrefix + "name-" + i),
                    State = layout.FindWidget<TextWidget>(IdPrefix + "state-" + i),
                    Group = group.Id,
                };
                if (pill.Name != null) pill.Name.Text = group.Name;
                int id = group.Id;
                Widget bar = layout.FindWidget(IdPrefix + i);
                if (bar != null) bar.Clicked += w => TurretAim.ToggleGroup(id);
                for (int k = 0; k < 3; k++)
                {
                    pill.OptionFill[k] = layout.FindWidget<ImageWidget>(IdPrefix + "optfill-" + i + "-" + k);
                    Widget button = layout.FindWidget(IdPrefix + "opt-" + i + "-" + k);
                    TurretAim.GroupAuto mode = OptionModes[k];
                    if (button != null) button.Clicked += w => TurretAim.SetAuto(id, mode);
                }
                pills.Add(pill);
            }
            layoutRoot = layout;
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

        // The vertical layout, anchored at the bottom right above the game's row of buttons, with the groups from the last (top) to the first (bottom):
        // for each a bar like the game's own pills (the rounded button with its mask, a fill that is only shown while lit, and two texts) and under it
        // the row of three options. The pills of the game are made the same way (see the fuel pill in Xml/Flight/FlightUI).
        private static XElement LayoutXml()
        {
            StringBuilder xml = new StringBuilder();
            xml.Append("<VerticalLayout id='" + IdPrefix + "root' anchor='BottomRight' pivot='1 0' position='-" + Number(RightMargin) + " " + Number(FirstBottom) + "' width='" + Number(PillWidth) +
                       "' sizeFitter='Vertical' spacing='" + Number(Gap) + "' childAlignment='LowerRight' childControlHeight='false' childControlWidth='false' childForceExpandHeight='false' childForceExpandWidth='false'>");
            for (int i = groups.Count - 1; i >= 0; i--)
            {
                TurretAim.GroupHud group = groups[i];
                xml.Append("<Button id='" + IdPrefix + i + "' class='btn btn-flight' type='Simple' width='" + Number(PillWidth) + "' height='" + Number(BarHeight) + "' mask='EnabledAndShown' tooltipPosition='Left' " +
                           "tooltip='Turret group " + Escape(group.Name) + "\\nClick: control it with the mouse aim, or let it hold. Several groups can be controlled together.\\nHold Alt to free the cursor.'>" +
                           "<Image id='" + IdPrefix + "fill-" + i + "' sprite='@SpriteWhite' color='#28599E' type='Filled' fillMethod='Horizontal' fillAmount='0' />" +
                           "<Text id='" + IdPrefix + "name-" + i + "' fontSize='18' alignment='Left' color='White' margin-left='12' />" +
                           "<Text id='" + IdPrefix + "state-" + i + "' alignment='Right' color='White' />" +
                           "</Button>");
                xml.Append("<HorizontalLayout width='" + Number(PillWidth) + "' height='" + Number(OptionHeight) + "' spacing='" + Number(Gap) +
                           "' childAlignment='MiddleLeft' childControlHeight='false' childControlWidth='false' childForceExpandHeight='false' childForceExpandWidth='false'>");
                for (int k = 0; k < 3; k++)
                {
                    xml.Append("<Button id='" + IdPrefix + "opt-" + i + "-" + k + "' class='btn btn-flight' type='Simple' width='" + Number(OptionWidth) + "' height='" + Number(OptionHeight) + "' mask='EnabledAndShown' " +
                               "tooltipPosition='Left' tooltip='Automatic aim: " + OptionNames[k].ToLowerInvariant() + " targets\\nA click takes the group off the manual control and sets this mode (the aiming itself comes later); a click on the lit one turns it off.'>" +
                               "<Image id='" + IdPrefix + "optfill-" + i + "-" + k + "' sprite='@SpriteWhite' color='#28599E' type='Filled' fillMethod='Horizontal' fillAmount='0' />" +
                               "<Text fontSize='14' alignment='Center' color='White' text='" + OptionNames[k] + "' />" +
                               "</Button>");
                }
                xml.Append("</HorizontalLayout>");
                if (i > 0) xml.Append("<Empty width='" + Number(PillWidth) + "' height='" + Number(BlockGap - Gap) + "' />");
            }
            xml.Append("</VerticalLayout>");
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
                bool lit = group.Controlled || automatic;
                if (pill.Fill != null)
                {
                    Color color = group.Live == 0 ? Gone : Blue;
                    pill.Fill.Color.Base = color;
                    pill.Fill.Image.color = color;
                    pill.Fill.Image.fillAmount = group.Live == 0 || lit ? 1f : 0f;
                }
                if (pill.State != null)
                {
                    pill.State.Text = group.Live == 0 ? "destroyed"
                        : group.Controlled ? group.OnTarget.ToString(CultureInfo.InvariantCulture) + "/" + group.Live.ToString(CultureInfo.InvariantCulture) + " aimed" +
                                             (group.Blocked > 0 ? " (" + group.Blocked.ToString(CultureInfo.InvariantCulture) + " blocked)" : "")
                        : automatic ? "auto " + group.Auto.ToString().ToLowerInvariant()
                        : group.Live.ToString(CultureInfo.InvariantCulture) + " held";
                }

                // The options: the one the group has is blue; while the group is controlled by hand all three are put out (darkened); otherwise
                // they have no fill (the button's own dark half-transparent look).
                for (int k = 0; k < 3; k++)
                {
                    ImageWidget fill = pill.OptionFill[k];
                    if (fill == null) continue;
                    bool active = !group.Controlled && group.Auto == OptionModes[k];
                    Color option = active ? Blue : PutOut;
                    fill.Color.Base = option;
                    fill.Image.color = option;
                    fill.Image.fillAmount = active || group.Controlled ? 1f : 0f;
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
            try
            {
                if (layoutRoot != null) layoutRoot.Destroy();
            }
            catch (Exception) { }
            layoutRoot = null;
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
