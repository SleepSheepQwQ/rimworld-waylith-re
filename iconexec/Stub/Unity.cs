using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

// Minimal UnityEngine for iconexec/.
//
// The shipped dlls/UnityStubs.dll is the real port, but it cannot allocate anything outside the
// game: Texture2D goes through Waylith.Rendering.Texture2D.UploadCpuData -> UploadLevels ->
// Brutal.Vulkan.Abstractions (the native Vulkan layer, not shipped), and Shader.Find goes through
// Waylith.Rendering.ShaderRegistry, whose static ctor needs the GLSL source tree on disk. Both
// failures are reproduced, with their exact stack traces, by iconexec/UnityProbe before this
// harness runs.
//
// So this file stands in for the engine - and only for the engine. Everything above it
// (Verse.Graphic_Collection.Init, Verse.Graphic_Random.SubGraphicFor, Verse.Widgets.GetIconFor,
// Verse.MaterialPool, ContentFinder, GraphicDatabase) is the game's own compiled code.
//
// Where a stub differs from Unity the difference is called out, because it is the difference that
// could make a result wrong:
//   * Texture2D allocates a managed RGBA32 buffer of the requested size, so width/height/name are
//     the real ones; there is no GPU upload, so pixels are never sampled (nothing here samples).
//   * Material records every Set* call and maps SetTexture(_MainTex) onto .mainTexture, which is
//     what Widgets.GetIconFor reads back.
//   * Shader declares _MaskTex, so Verse.ShaderUtility.SupportsMaskTex agrees with a real
//     CutoutComplex shader instead of silently dropping the mask.
namespace UnityEngine
{
    public class Object
    {
        private string _name = "";

        public virtual string name { get { return _name; } set { _name = value; } }

        public int GetInstanceID() { return RuntimeHelpers.GetHashCode(this); }

        // Unity's == on a live object is reference identity; Verse compares shaders and textures
        // with it (MaterialRequest.Equals, GetIconFor's UIIconOverride test).
        public static bool operator ==(Object a, Object b) { return ReferenceEquals(a, b); }
        public static bool operator !=(Object a, Object b) { return !ReferenceEquals(a, b); }

        // `if (tex)` in the shipped IL compiles to this conversion.
        public static implicit operator bool(Object o) { return !ReferenceEquals(o, null); }

        public override bool Equals(object o) { return ReferenceEquals(this, o); }
        public override int GetHashCode() { return RuntimeHelpers.GetHashCode(this); }
        public override string ToString() { return _name; }
    }

    public class Texture : Object
    {
        public int width { get; set; }
        public int height { get; set; }
        public FilterMode filterMode { get; set; }
        public TextureWrapMode wrapMode { get; set; }

        public static bool operator ==(Texture a, Texture b) { return ReferenceEquals(a, b); }
        public static bool operator !=(Texture a, Texture b) { return !ReferenceEquals(a, b); }
    }

    public class Texture2D : Texture
    {
        // How many Texture2D objects this process allocated, and at which sizes. The census at the
        // end of the run compares it with the number of files the content finder was seeded with.
        public static int Allocated;
        public static readonly List<string> AllocatedNames = new List<string>();

        public byte[] pixels;

        public Texture2D() { }
        public Texture2D(int w, int h) : this(w, h, TextureFormat.RGBA32, false) { }
        public Texture2D(int w, int h, TextureFormat format, bool mipChain)
        {
            width = w;
            height = h;
            this.format = format;
            // RGBA32, one level: enough for the name/size contract, no GPU behind it.
            pixels = new byte[w * h * 4];
            Allocated++;
        }

        public TextureFormat format { get; set; }

        // UnityEngine.Texture2D's engine-provided singletons (Verse.TexUI reads whiteTexture).
        static Texture2D _white, _black, _red, _gray;
        public static Texture2D whiteTexture { get { return _white ??= new Texture2D(1, 1) { name = "whiteTexture" }; } }
        public static Texture2D blackTexture { get { return _black ??= new Texture2D(1, 1) { name = "blackTexture" }; } }
        public static Texture2D redTexture { get { return _red ??= new Texture2D(1, 1) { name = "redTexture" }; } }
        public static Texture2D grayTexture { get { return _gray ??= new Texture2D(1, 1) { name = "grayTexture" }; } }
        public static Texture2D linearGrayTexture { get { return grayTexture; } }
        public static Texture2D normalTexture { get { return _white ??= new Texture2D(1, 1) { name = "normalTexture" }; } }
        public int mipmapCount { get { return 1; } }

        public void SetPixel(int x, int y, Color c) { }
        public void SetPixel(int x, int y, Color c, int mipLevel) { }
        public void SetPixel32(int x, int y, Color32 c) { }
        public Color GetPixel(int x, int y) { return Color.white; }
        public Color GetPixelBilinear(float u, float v) { return Color.white; }
        public byte[] GetRawTextureData() { return pixels; }
        public void LoadImage(byte[] data) { }
        public byte[] EncodeToPNG() { return new byte[0]; }
        public void Resize(int w, int h) { width = w; height = h; pixels = new byte[w * h * 4]; }
        public void SetPixels(int x, int y, int w, int h, Color[] colors) { }
        public Color[] GetPixels(int mipLevel) { return new Color[width * height]; }
        public Color32[] GetPixels32() { return new Color32[width * height]; }
        public void SetPixels(Color[] colors) { }
        public void SetPixels32(Color32[] colors) { }
        public Color[] GetPixels() { return new Color[width * height]; }
        public void Apply() { }
        public void Apply(bool updateMipmaps) { }
        public void Apply(bool updateMipmaps, bool makeNoLongerReadable) { }
    }

    public class RenderTexture : Texture
    {
        public RenderTexture(int w, int h) { width = w; height = h; }
    }

    public class Shader : Object
    {
        // The property table a real CutoutComplex shader exposes. _MaskTex is present, which is
        // exactly what Verse.ShaderUtility.SupportsMaskTex probes for by id.
        public static readonly string[] DefaultLayout =
        {
            "_MainTex", "_Color", "_ColorTwo", "_MaskTex", "_SecondaryTex", "_Cutout"
        };

        private readonly List<string> _props = new List<string>();

        private static readonly Dictionary<int, string> ById = new Dictionary<int, string>();

        // Every name this stub was asked to resolve, so the run can list the engine surface it
        // actually stood in for.
        public static readonly List<string> Resolved = new List<string>();

        public Shader() { }

        public static Shader Find(string path)
        {
            Resolved.Add("Shader.Find(" + path + ")");
            var s = new Shader { name = path ?? "" };
            foreach (string p in DefaultLayout) s._props.Add(p);
            return s;
        }

        public static Shader CreateLateBound(string name)
        {
            Resolved.Add("Shader.CreateLateBound(" + name + ")");
            return Find(name);
        }

        public static int PropertyToID(string n)
        {
            int id = Id(n);
            lock (ById) { if (!ById.ContainsKey(id)) ById[id] = n; }
            return id;
        }

        public static string PropertyToIDName(int id)
        {
            lock (ById) { string n; return ById.TryGetValue(id, out n) ? n : "#" + id.ToString("X8"); }
        }

        public int GetPropertyCount() { return _props.Count; }
        public int GetPropertyNameId(int i) { return Id(_props[i]); }
        public bool HasProperty(string n) { return _props.Contains(n); }
        public int GetPropertyNameId(string n) { return HasProperty(n) ? Id(n) : 0; }

        internal static int Id(string n)
        {
            unchecked
            {
                uint h = 2166136261u;               // FNV-1a, stable and collision-free here
                foreach (char c in n) { h ^= c; h *= 16777619u; }
                return (int)(h | 1u);               // never 0: 0 is Unity's "no property"
            }
        }
    }

    public struct Color
    {
        public float r, g, b, a;

        // Unity has both arities as real constructors (the IL calls ..ctor(Single,Single,Single) a lot).
        public Color(float r, float g, float b) { this.r = r; this.g = g; this.b = b; this.a = 1f; }
        public Color(float r, float g, float b, float a) { this.r = r; this.g = g; this.b = b; this.a = a; }

        public static Color white { get { return new Color(1f, 1f, 1f, 1f); } }
        public static Color black { get { return new Color(0f, 0f, 0f, 1f); } }
        public static Color clear { get { return new Color(0f, 0f, 0f, 0f); } }
        public static Color yellow { get { return new Color(1f, 0.92f, 0.016f, 1f); } }
        public static Color red { get { return new Color(1f, 0f, 0f, 1f); } }
        public static Color green { get { return new Color(0f, 1f, 0f, 1f); } }
        public static Color blue { get { return new Color(0f, 0f, 1f, 1f); } }
        public static Color grey { get { return new Color(0.5f, 0.5f, 0.5f, 1f); } }
        public static Color gray { get { return new Color(0.5f, 0.5f, 0.5f, 1f); } }
        public static Color magenta { get { return new Color(1f, 0f, 1f, 1f); } }
        public static Color cyan { get { return new Color(0f, 1f, 1f, 1f); } }

        public static Color operator *(Color c, float f) { return new Color(c.r * f, c.g * f, c.b * f, c.a * f); }
        public static Color operator *(float f, Color c) { return c * f; }
        public static Color operator +(Color a, Color b) { return new Color(a.r + b.r, a.g + b.g, a.b + b.b, a.a + b.a); }

        public static bool operator ==(Color x, Color y) { return x.r == y.r && x.g == y.g && x.b == y.b && x.a == y.a; }
        public static bool operator !=(Color x, Color y) { return !(x == y); }

        public override bool Equals(object o) { return o is Color && this == (Color)o; }

        public override int GetHashCode()
        {
            unchecked { return ((int)(r * 1000f) * 397 ^ (int)(g * 1000f)) * 397 ^ (int)(b * 1000f) * 31 ^ (int)(a * 1000f); }
        }

        public override string ToString()
        {
            return "RGBA(" + r.ToString("0.###") + ", " + g.ToString("0.###") + ", " + b.ToString("0.###") + ", " + a.ToString("0.###") + ")";
        }
    }

    // Verse.MaterialPool.MatFrom routes colours through Color32 before keying, so colour changes
    // below 1/255 collapse. Same round-half-up conversion Unity uses.
    public struct Color32
    {
        public byte r, g, b, a;

        public Color32(byte r, byte g, byte b, byte a) { this.r = r; this.g = g; this.b = b; this.a = a; }

        public static implicit operator Color32(Color c) { return new Color32(Byte(c.r), Byte(c.g), Byte(c.b), Byte(c.a)); }
        public static implicit operator Color(Color32 c) { return new Color(c.r / 255f, c.g / 255f, c.b / 255f, c.a / 255f); }

        private static byte Byte(float f)
        {
            if (f <= 0f) return 0;
            if (f >= 1f) return 255;
            return (byte)Math.Round(f * 255f, MidpointRounding.AwayFromZero);
        }

        public override string ToString() { return "RGBA32(" + r + ", " + g + ", " + b + ", " + a + ")"; }
    }

    public struct Vector2
    {
        public float x, y;
        public Vector2(float x, float y) { this.x = x; this.y = y; }

        public static Vector2 one { get { return new Vector2(1f, 1f); } }
        public static Vector2 zero { get { return new Vector2(0f, 0f); } }

        public static Vector2 operator +(Vector2 a, Vector2 b) { return new Vector2(a.x + b.x, a.y + b.y); }
        public static Vector2 operator -(Vector2 a, Vector2 b) { return new Vector2(a.x - b.x, a.y - b.y); }
        public static Vector2 operator *(Vector2 a, float f) { return new Vector2(a.x * f, a.y * f); }

        public override string ToString() { return "(" + x + ", " + y + ")"; }
    }

    public struct Vector2Int
    {
        public int x, y;
        public Vector2Int(int x, int y) { this.x = x; this.y = y; }
    }

    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 zero { get { return new Vector3(0f, 0f, 0f); } }
        public static Vector3 one { get { return new Vector3(1f, 1f, 1f); } }
    }

    public struct Vector4
    {
        public float x, y, z, w;
        public Vector4(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }
        public override string ToString() { return "(" + x + ", " + y + ", " + z + ", " + w + ")"; }
    }

    public struct Quaternion { public float x, y, z, w; }

    // Rect is a property-carrying struct in Unity (the IL calls get_width / set_y), not a bag of
    // fields, and Widgets.GetIconFor passes it through by value.
    public struct Rect
    {
        private float _x, _y, _w, _h;

        public Rect(float x, float y, float width, float height) { _x = x; _y = y; _w = width; _h = height; }
        public Rect(Vector2 pos, Vector2 size) { _x = pos.x; _y = pos.y; _w = size.x; _h = size.y; }

        public float x { get { return _x; } set { _x = value; } }
        public float y { get { return _y; } set { _y = value; } }
        public float width { get { return _w; } set { _w = value; } }
        public float height { get { return _h; } set { _h = value; } }

        public float xMin { get { return _x; } set { _x = value; } }
        public float yMin { get { return _y; } set { _y = value; } }
        public float xMax { get { return _x + _w; } set { _w = value - _x; } }
        public float yMax { get { return _y + _h; } set { _h = value - _y; } }
        public Vector2 position { get { return new Vector2(_x, _y); } set { _x = value.x; _y = value.y; } }
        public Vector2 size { get { return new Vector2(_w, _h); } set { _w = value.x; _h = value.y; } }
        public Vector2 center { get { return new Vector2(_x + _w / 2f, _y + _h / 2f); } }

        public bool Contains(Vector2 p) { return p.x >= _x && p.x < _x + _w && p.y >= _y && p.y < _y + _h; }

        public override string ToString() { return "Rect(" + _x + ", " + _y + ", " + _w + ", " + _h + ")"; }
    }

    public struct Bounds { public Vector3 center, size; }
    public struct Matrix4x4 { public float m00; }
    public struct LayerMask { public int value; }

    public enum RuntimePlatform
    {
        OSXEditor = 0, OSXPlayer = 1, WindowsPlayer = 2, WindowsEditor = 7, IPhonePlayer = 8,
        Android = 11, LinuxPlayer = 13, LinuxEditor = 16, WebGLPlayer = 17,
    }

    public enum FilterMode { Point = 0, Bilinear = 1, Trilinear = 2 }
    public enum TextureWrapMode { Repeat = 0, Clamp = 1, Mirror = 2, MirrorOnce = 3 }
    public enum TextureFormat { Alpha8 = 1, RGB24 = 3, RGBA32 = 4, ARGB32 = 5, DXT5 = 12, BC7 = 25 }
    public enum RenderTextureFormat { ARGB32 = 0, Depth = 1, ARGBHalf = 2 }
    public enum ColorSpace { Uninitialized = -1, Gamma = 0, Linear = 1 }
    public enum GraphicsFormat { None = 0, R8G8B8A8_UNorm = 8, R8G8B8A8_SRGB = 9 }
    public enum GraphicsDeviceType { Null = 0, OpenGLES3 = 8, Vulkan = 21 }

    public class MaterialPropertyBlock
    {
        public void SetFloat(string n, float v) { }
        public void SetTexture(string n, Texture t) { }
        public void SetVector(string n, Vector4 v) { }
        public void SetColor(string n, Color c) { }
    }

    // Verse.ContentFinder<T> tries Resources before it walks the mods. In the game that finds the
    // built-in resources shipped with the player; here there are none, and this returns null so
    // the finder falls through to the seeded ModContentHolder - which is exactly the path under test.
    public static class Resources
    {
        public static Object Load(string path) { return null; }
        public static Object Load(string path, Type t) { return null; }
        public static T Load<T>(string path) where T : Object { return null; }
        public static Object[] LoadAll(string path) { return new Object[0]; }
        public static Object[] LoadAll(string path, Type t) { return new Object[0]; }
        public static T[] LoadAll<T>(string path) where T : Object { return new T[0]; }
        public static void UnloadAsset(Object o) { }
        public static AsyncOperation UnloadUnusedAssets() { return null; }
        public static Object GetBuiltinResource(Type t, string path) { return null; }
        public static T GetBuiltinResource<T>(string path) where T : Object { return null; }
        public static Object[] FindObjectsOfTypeAll(Type t) { return new Object[0]; }
        public static T[] FindObjectsOfTypeAll<T>() where T : Object { return new T[0]; }
    }

    public class AssetBundle : Object
    {
        public Object LoadAsset(string n) { return null; }
        public Object LoadAsset(string n, Type t) { return null; }
        public Object[] LoadAllAssets() { return new Object[0]; }
        public Object[] LoadAllAssets(Type t) { return new Object[0]; }
        public AssetBundleRequest LoadAssetAsync(string n) { return null; }
        public AssetBundleRequest LoadAssetAsync(string n, Type t) { return null; }
        public string[] GetAllAssetNames() { return new string[0]; }
        public void Unload(bool unloadAllLoadedObjects) { }
    }

    public class AsyncOperation
    {
        public bool isDone { get { return true; } }
        public float progress { get { return 1f; } }
    }

    public class AssetBundleRequest : AsyncOperation
    {
        public Object asset { get { return null; } }
    }

    public class Material : Object
    {
        // Every applied property, in order: the harness prints the ones on _MainTex/_MaskTex so the
        // reading of Graphic_Single.Init can be checked against what the material actually received.
        public readonly List<string> Applied = new List<string>();

        public Shader shader { get; set; }
        public Texture mainTexture { get; set; }
        public Color color { get; set; }
        public int renderQueue { get; set; }

        public Material(Shader s) { shader = s; }

        public Material(Material src)
        {
            shader = src.shader;
            mainTexture = src.mainTexture;
            color = src.color;
            renderQueue = src.renderQueue;
            Applied.AddRange(src.Applied);
        }

        // MaterialAllocator.Create takes this branch when no ShaderDef is loaded; the real one
        // defers until the engine registry is up, which never happens head-less.
        public static Material CreateDeferred(Func<Material> factory) { return factory(); }

        public void SetTexture(int id, Texture t)
        {
            string n = Shader.PropertyToIDName(id);
            Applied.Add("tex:" + n + "=" + (t == null ? "null" : t.name));
            if (n == "_MainTex") mainTexture = t;
        }

        public void SetTexture(string n, Texture t)
        {
            Applied.Add("tex:" + n + "=" + (t == null ? "null" : t.name));
            if (n == "_MainTex") mainTexture = t;
        }

        public void SetColor(int id, Color c) { Applied.Add("col:" + Shader.PropertyToIDName(id) + "=" + c); if (Shader.PropertyToIDName(id) == "_Color") color = c; }
        public void SetColor(string n, Color c) { Applied.Add("col:" + n + "=" + c); if (n == "_Color") color = c; }
        public void SetFloat(string n, float v) { Applied.Add("flt:" + n + "=" + v.ToString("0.###")); }
        public void SetVector(string n, Vector4 v) { Applied.Add("vec:" + n + "=" + v); }
        public Texture GetTexture(string n) { return n == "_MainTex" ? mainTexture : null; }
        public bool HasProperty(string n) { return true; }
    }
}
