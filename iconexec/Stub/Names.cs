using System;
using System.Collections.Generic;

// Name-resolution placeholders, and the few engine helpers that Verse calls as real behaviour.
//
// Assembly-CSharp names many Unity types inside method bodies the harness never executes (the whole
// Verse assembly is one compilation unit, so a reached method can mention unrelated engine APIs in
// branches that are not taken). The CLR needs the *name* to resolve before it will JIT such a
// method, so they exist here as skeletons. If one of these starts being *called*, the harness fails
// loudly with MissingMethodException instead of quietly returning a made-up value.
//
// Mathf.Abs is the exception: Verse.Rand.Range really calls it, so it has to compute.
namespace UnityEngine
{
    public class AudioClip : Object { }
    public class AudioSource : Object { public AudioClip clip { get; set; } }
    public class GameObject : Object { }
    public class Component : Object { }
    public class Behaviour : Component { }
    public class Transform : Component { }
    public class Mesh : Object { }
    public class Camera : Behaviour { }
    public class Sprite : Object { }
    public class Font : Object { }
    public class TextAsset : Object { public string text { get { return ""; } } }
    public class ComputeShader : Object { }
    public class Cubemap : Texture { }
    public class Texture3D : Texture { }
    public class Texture2DArray : Texture { }
    public class AnimationCurve { }
    public class Gradient { }
    // GUI style surface: Verse.Widgets' static ctor builds styles (padding, fonts, colours), so these
    // have to exist with real settable members even though nothing renders.
    public enum FontStyle { Normal = 0, Bold = 1, Italic = 2, BoldAndItalic = 3 }
    public enum TextAnchor { UpperLeft = 0, UpperCenter = 1, UpperRight = 2, MiddleLeft = 3, MiddleCenter = 4, MiddleRight = 5, LowerLeft = 6, LowerCenter = 7, LowerRight = 8 }

    public class RectOffset
    {
        public int left, right, top, bottom;
        public RectOffset() { }
        public RectOffset(int l, int r, int t, int b) { left = l; right = r; top = t; bottom = b; }
        public int horizontal { get { return left + right; } }
        public int vertical { get { return top + bottom; } }
    }

    public class GUIStyleState
    {
        public Color textColor { get; set; }
        public Texture2D background { get; set; }
    }

    public class GUIStyle
    {
        public string name { get; set; }
        public int fontSize { get; set; }
        public FontStyle fontStyle { get; set; }
        public TextAnchor alignment { get; set; }
        public bool wordWrap { get; set; }
        public bool richText { get; set; }
        public bool clipping { get; set; }
        public float fixedWidth { get; set; }
        public float fixedHeight { get; set; }
        public Font font { get; set; }
        public RectOffset padding { get; set; }
        public RectOffset margin { get; set; }
        public RectOffset border { get; set; }
        public RectOffset overflow { get; set; }
        public GUIStyleState normal { get; set; }
        public GUIStyleState hover { get; set; }
        public GUIStyleState active { get; set; }
        public GUIStyleState onNormal { get; set; }
        public GUIStyleState focused { get; set; }

        public GUIStyle() { padding = new RectOffset(); margin = new RectOffset(); border = new RectOffset(); overflow = new RectOffset(); normal = new GUIStyleState(); hover = new GUIStyleState(); active = new GUIStyleState(); onNormal = new GUIStyleState(); focused = new GUIStyleState(); }
        public GUIStyle(GUIStyle other) { }

        public Vector2 CalcSize(GUIContent c) { return new Vector2(0f, 0f); }
        public float CalcHeight(GUIContent c, float w) { return 0f; }
    }

    public class GUIContent
    {
        public string text { get; set; }
        public Texture image { get; set; }
        public string tooltip { get; set; }
        public GUIContent() { }
        public GUIContent(string text) { this.text = text; }
        public GUIContent(Texture image) { this.image = image; }
        public GUIContent(string text, string tooltip) { this.text = text; this.tooltip = tooltip; }
        public GUIContent(Texture image, string tooltip) { this.image = image; this.tooltip = tooltip; }
    }

    public static class GUI
    {
        public static Color color { get; set; }
        public static Color contentColor { get; set; }
        public static Color backgroundColor { get; set; }
        public static GUISkin skin { get { return null; } }
        public static void Label(Rect r, string t) { }
        public static void Box(Rect r, string t) { }
        public static void DrawTexture(Rect r, Texture t) { }
        public static bool Button(Rect r, string t) { return false; }
    }
    public class GUISkin : Object { }
    public class GUILayoutOption { }
    public class ShaderVariantCollection : Object { }

    public static class Mathf
    {
        public static float Clamp01(float v) { return v < 0f ? 0f : (v > 1f ? 1f : v); }
        public static int RoundToInt(float v) { return (int)Math.Round(v, MidpointRounding.AwayFromZero); }
        public static int FloorToInt(float v) { return (int)Math.Floor(v); }
        public static int CeilToInt(float v) { return (int)Math.Ceiling(v); }
        public static float Lerp(float a, float b, float t) { return a + (b - a) * Clamp01(t); }
        public static int Abs(int v) { return v < 0 ? -v : v; }
        public static float Abs(float v) { return v < 0f ? -v : v; }
        public static float Min(float a, float b) { return a < b ? a : b; }
        public static float Max(float a, float b) { return a > b ? a : b; }
        public static int Min(int a, int b) { return a < b ? a : b; }
        public static int Max(int a, int b) { return a > b ? a : b; }
    }

    public static class Application
    {
        public static string dataPath { get { return ""; } }
        public static string persistentDataPath { get { return ""; } }
        public static string streamingAssetsPath { get { return ""; } }
        public static bool isEditor { get { return false; } }
        public static bool isPlaying { get { return false; } }
        public static RuntimePlatform platform { get { return RuntimePlatform.LinuxPlayer; } }

        // Nested, exactly as the shipped stub declares it: Assembly-CSharp's TypeRef is
        // Application+LogCallback (empty namespace, ResolutionScope = the Application TypeRef).
        public delegate void LogCallback(string condition, string stackTrace, LogType type);

        public static void add_logMessageReceivedThreaded(LogCallback cb) { }
        public static void remove_logMessageReceivedThreaded(LogCallback cb) { }
    }

    public static class Time
    {
        public static float realtimeSinceStartup { get { return 0f; } }
        public static float deltaTime { get { return 0f; } }
    }

    public static class SystemInfo
    {
        public static string graphicsDeviceName { get { return "iconexec/"; } }
        public static int graphicsMemorySize { get { return 0; } }
    }

    public static class Screen
    {
        public static int width { get { return 1; } }
        public static int height { get { return 1; } }
    }

    public enum LogType { Error = 0, Assert = 1, Warning = 2, Log = 3, Exception = 4 }

    public static class Debug
    {
        public static void Log(object o) { Console.WriteLine("[UnityEngine.Debug] " + o); }
        public static void LogWarning(object o) { Console.WriteLine("[UnityEngine.Debug] WARN " + o); }
        public static void LogError(object o) { Console.WriteLine("[UnityEngine.Debug] ERROR " + o); }
    }

    // Deterministic, seeded - nothing on the icon path samples it (Verse.Rand is MurmurHash over its
    // own seed), but PortraitCache and friends name it, so it has to exist.
    public static class Random
    {
        static readonly System.Random Rng = new System.Random(12345);

        public static void InitState(int seed) { }
        public static float value { get { return (float)Rng.NextDouble(); } }
        public static float Range(float min, float max) { return min + (float)Rng.NextDouble() * (max - min); }
        public static int Range(int min, int max) { return min >= max ? min : Rng.Next(min, max); }
    }
}
