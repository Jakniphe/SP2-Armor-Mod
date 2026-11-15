using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using BepInEx.Configuration;

namespace ArmorFragments
{
    // The settings the energy-model code reads, with the defaults the mod itself has: they are read from the mod's own
    // source (Config.Bind lines in ArmorFragmentsPlugin.cs) when the lab starts, so they cannot drift from the mod.
    internal static class ArmorFragmentsPlugin
    {
        public static string Source = "(not found: built-in defaults)";
        private static readonly Dictionary<string, string> Literals = Read();

        public static readonly ConfigEntry<float> ArmorResistance = F("ArmorResistance", 600f);
        public static readonly ConfigEntry<float> ArmorRicochetAngle = F("ArmorRicochetAngle", 70f);
        public static readonly ConfigEntry<float> ArmorRicochetMultiplier = F("ArmorRicochetMultiplier", 1f);
        public static readonly ConfigEntry<float> RicochetRandomness = F("RicochetRandomness", 50f);
        public static readonly ConfigEntry<float> RicochetFriction = F("RicochetFriction", 0.3f);
        public static readonly ConfigEntry<float> MaxDeflection = F("MaxDeflection", 6f);
        public static readonly ConfigEntry<int> MaxRicochets = I("MaxRicochets", 2);
        public static readonly ConfigEntry<float> FillerHE = F("FillerHE", 10f);
        public static readonly ConfigEntry<float> FillerAPHE = F("FillerAPHE", 3f);
        public static readonly ConfigEntry<float> DefaultShellBrittleness = F("DefaultShellBrittleness", 50f);

        private static Dictionary<string, string> Read()
        {
            Dictionary<string, string> map = new Dictionary<string, string>();
            foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
            {
                for (DirectoryInfo dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
                {
                    string path = Path.Combine(dir.FullName, "Armor Mod", "ArmorFragmentsPlugin.cs");
                    if (!File.Exists(path)) continue;
                    Source = path;
                    string text = File.ReadAllText(path);
                    foreach (Match m in Regex.Matches(text, @"(\w+)\s*=\s*Config\.Bind\(\s*""[^""]+""\s*,\s*""[^""]+""\s*,\s*([^,\r\n]+?)\s*,"))
                        map[m.Groups[1].Value] = m.Groups[2].Value;
                    return map;
                }
            }
            return map;
        }

        private static ConfigEntry<float> F(string name, float fallback)
        {
            string literal;
            float value;
            if (Literals.TryGetValue(name, out literal) &&
                float.TryParse(literal.TrimEnd('f', 'F'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value))
                return new ConfigEntry<float>(value);
            return new ConfigEntry<float>(fallback);
        }

        private static ConfigEntry<int> I(string name, int fallback)
        {
            string literal;
            int value;
            if (Literals.TryGetValue(name, out literal) && int.TryParse(literal, out value)) return new ConfigEntry<int>(value);
            return new ConfigEntry<int>(fallback);
        }
    }
}
