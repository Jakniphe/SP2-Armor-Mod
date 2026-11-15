using System;

// Stand-ins for the few Unity things the mod's simulation and energy-model code touch.
namespace UnityEngine
{
    public struct Vector2
    {
        public float x, y;
        public Vector2(float x, float y) { this.x = x; this.y = y; }
        public float sqrMagnitude { get { return x * x + y * y; } }
        public void Normalize() { float m = (float)Math.Sqrt(sqrMagnitude); if (m > 1e-9f) { x /= m; y /= m; } }
    }

    public static class Mathf
    {
        public const float PI = (float)Math.PI;
        public const float Deg2Rad = 0.0174532924f;
        public static float Sqrt(float v) { return (float)Math.Sqrt(v); }
        public static float Exp(float v) { return (float)Math.Exp(v); }
        public static float Sin(float v) { return (float)Math.Sin(v); }
        public static float Cos(float v) { return (float)Math.Cos(v); }
        public static float Tan(float v) { return (float)Math.Tan(v); }
        public static float Atan2(float y, float x) { return (float)Math.Atan2(y, x); }
        public static float Log(float v) { return (float)Math.Log(v); }
        public static float Log(float v, float b) { return (float)Math.Log(v, b); }
        public static float Pow(float v, float p) { return (float)Math.Pow(v, p); }
        public static float Abs(float v) { return Math.Abs(v); }
        public static float Lerp(float a, float b, float t) { return a + (b - a) * Clamp01(t); }
        public static float Clamp(float v, float a, float b) { return v < a ? a : (v > b ? b : v); }
        public static int Clamp(int v, int a, int b) { return v < a ? a : (v > b ? b : v); }
        public static float Clamp01(float v) { return Clamp(v, 0f, 1f); }
        public static float Min(float a, float b) { return a < b ? a : b; }
        public static float Max(float a, float b) { return a > b ? a : b; }
        public static int Min(int a, int b) { return a < b ? a : b; }
        public static int Max(int a, int b) { return a > b ? a : b; }
        public static int FloorToInt(float v) { return (int)Math.Floor(v); }
        public static int CeilToInt(float v) { return (int)Math.Ceiling(v); }
        public static int RoundToInt(float v) { return (int)Math.Round(v); }
    }

    public static class Random
    {
        private static readonly System.Random Rng = new System.Random(1);
        public static float value { get { return (float)Rng.NextDouble(); } }
        public static float Range(float min, float max) { return min + (max - min) * (float)Rng.NextDouble(); }
    }
}

namespace BepInEx.Configuration
{
    public class ConfigEntry<T>
    {
        public T Value;
        public ConfigEntry(T value) { Value = value; }
    }
}

namespace ArmorFragments
{
    // The parts of the plugin the lab does not have (the stamps only need these types to exist).
    internal sealed class VehicleMaps { }
    internal sealed class PlateVisual { }
}
