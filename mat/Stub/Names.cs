using System;
using System.Collections.Generic;

// Name-resolution placeholders.
//
// Assembly-CSharp names these types inside method bodies that mat/ never executes (the whole Verse
// assembly is one compilation unit, so a reached method can mention unrelated Unity APIs in branches
// the harness never takes). The CLR needs the *name* to resolve before it will JIT such a method,
// so they exist here as skeletons. Nothing in this file is on the material path; the semantics that
// matter live in Unity.cs. If one of these starts being *called*, the harness fails loudly with
// MissingMethodException instead of quietly returning a made-up value.
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
    public class GUIStyle { }
    public class GUISkin : Object { }
    public class GUILayoutOption { }
    public class ShaderVariantCollection : Object { }

    public static class Mathf
    {
        public static float Clamp01(float v) { return v < 0f ? 0f : (v > 1f ? 1f : v); }
        public static int RoundToInt(float v) { return (int)Math.Round(v, MidpointRounding.AwayFromZero); }
        public static float Lerp(float a, float b, float t) { return a + (b - a) * Clamp01(t); }
    }

    public static class Application
    {
        public static string dataPath { get { return ""; } }
        public static string persistentDataPath { get { return ""; } }
        public static string streamingAssetsPath { get { return ""; } }
        public static bool isEditor { get { return false; } }
        public static bool isPlaying { get { return false; } }
        public static RuntimePlatform platform { get { return RuntimePlatform.LinuxPlayer; } }
    }

    public static class Time
    {
        public static float realtimeSinceStartup { get { return 0f; } }
        public static float deltaTime { get { return 0f; } }
    }

    public static class SystemInfo
    {
        public static string graphicsDeviceName { get { return "mat/"; } }
        public static int graphicsMemorySize { get { return 0; } }
    }

    public static class Screen
    {
        public static int width { get { return 1; } }
        public static int height { get { return 1; } }
    }

    public static class Debug
    {
        public static void Log(object o) { Console.WriteLine("[UnityEngine.Debug] " + o); }
        public static void LogWarning(object o) { Console.WriteLine("[UnityEngine.Debug] WARN " + o); }
        public static void LogError(object o) { Console.WriteLine("[UnityEngine.Debug] ERROR " + o); }
    }

    public struct Vector2
    {
        public float x, y;
        public Vector2(float x, float y) { this.x = x; this.y = y; }
        public override string ToString() { return "(" + x + ", " + y + ")"; }
    }

    public struct Vector2Int
    {
        public int x, y;
        public Vector2Int(int x, int y) { this.x = x; this.y = y; }
    }

    public struct Quaternion
    {
        public float x, y, z, w;
    }

    public struct Rect
    {
        public float x, y, width, height;
        public Rect(float x, float y, float w, float h) { this.x = x; this.y = y; width = w; height = h; }
    }

    public struct Bounds
    {
        public Vector3 center, size;
    }

    public struct Matrix4x4
    {
        public float m00;
    }

    public struct LayerMask
    {
        public int value;
    }

    public enum FilterMode { Point = 0, Bilinear = 1, Trilinear = 2 }
    public enum TextureWrapMode { Repeat = 0, Clamp = 1, Mirror = 2, MirrorOnce = 3 }
    public enum TextureFormat { Alpha8 = 1, RGB24 = 3, RGBA32 = 4, ARGB32 = 5, DXT5 = 12, BC7 = 25 }
    public enum RenderTextureFormat { ARGB32 = 0, Depth = 1, ARGBHalf = 2 }
    public enum ColorSpace { Uninitialized = -1, Gamma = 0, Linear = 1 }
    public enum GraphicsFormat { None = 0, R8G8B8A8_UNorm = 8, R8G8B8A8_SRGB = 9 }
    public enum GraphicsDeviceType { Null = 0, OpenGLES3 = 8, Vulkan = 21 }
}
