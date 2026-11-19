using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

// Minimal UnityEngine, hand-written for mat/. Only what Verse.MaterialPool.MatFrom and its
// dependencies actually touch. Every deviation from Unity is called out where it is made.
namespace UnityEngine
{
    public class Object
    {
        private string _name = "";

        public virtual string name
        {
            get { return _name; }
            set { _name = value; }
        }

        public int GetInstanceID() { return RuntimeHelpers.GetHashCode(this); }

        // Unity's Object == is reference identity for a live object, and that is what
        // Verse.MaterialRequest.Equals compares shader / mainTex / maskTex with.
        public static bool operator ==(Object a, Object b) { return ReferenceEquals(a, b); }
        public static bool operator !=(Object a, Object b) { return !ReferenceEquals(a, b); }

        // `if (mat)` in the shipped IL (ldsfld Material, brtrue) is this conversion.
        public static implicit operator bool(Object o) { return !ReferenceEquals(o, null); }

        public override bool Equals(object o) { return ReferenceEquals(this, o); }
        public override int GetHashCode() { return RuntimeHelpers.GetHashCode(this); }
        public override string ToString() { return _name; }
    }

    public class Texture : Object
    {
        public int width { get; set; }
        public int height { get; set; }

        // Declared again because the IL calls Texture::op_Equality / op_Inequality by name.
        public static bool operator ==(Texture a, Texture b) { return ReferenceEquals(a, b); }
        public static bool operator !=(Texture a, Texture b) { return !ReferenceEquals(a, b); }
    }

    public class Texture2D : Texture
    {
        public Texture2D() { }
        public Texture2D(int w, int h) { width = w; height = h; }
    }

    public class RenderTexture : Texture
    {
        public RenderTexture(int w, int h) { width = w; height = h; }
    }

    public class Shader : Object
    {
        // The property table a real Map/CutoutComplex shader exposes. _MaskTex is present, which is
        // exactly what Verse.ShaderUtility.SupportsMaskTex probes for; without it MatFrom would null
        // the request's maskTex (IL_008E) and the mask case would collapse into the no-mask case.
        public static readonly string[] DefaultLayout =
        {
            "_MainTex", "_Color", "_ColorTwo", "_MaskTex", "_SecondaryTex", "_Cutout"
        };

        private readonly List<string> _props = new List<string>();

        private static readonly Dictionary<int, string> ById = new Dictionary<int, string>();

        public Shader() { }

        public static Shader Find(string path)
        {
            var s = new Shader { name = path ?? "" };
            foreach (string p in DefaultLayout) s._props.Add(p);
            return s;
        }

        public static Shader CreateLateBound(string name) { return Find(name); }

        // Real Unity hands out a stable hash per property name; here it only has to be stable and
        // collide-free across the six names above, because SupportsMaskTex matches ids, not strings.
        public static int PropertyToID(string n)
        {
            int id = Id(n);
            lock (ById) { if (!ById.ContainsKey(id)) ById[id] = n; }
            return id;
        }

        // Reverse lookup, so a recorded SetTexture(int, …) can be printed as its property name.
        public static string PropertyToIDName(int id)
        {
            lock (ById) { string n; return ById.TryGetValue(id, out n) ? n : "#" + id.ToString("X8"); }
        }

        public int GetPropertyCount() { return _props.Count; }
        public int GetPropertyNameId(int i) { return Id(_props[i]); }
        public bool HasProperty(string n) { return _props.Contains(n); }

        internal static int Id(string n)
        {
            unchecked
            {
                uint h = 2166136261u;               // FNV-1a
                foreach (char c in n) { h ^= c; h *= 16777619u; }
                return (int)(h | 1u);               // never 0: 0 is Unity's "no property"
            }
        }
    }

    public struct Color
    {
        public float r, g, b, a;

        public Color(float r, float g, float b, float a = 1f) { this.r = r; this.g = g; this.b = b; this.a = a; }

        public static Color white { get { return new Color(1f, 1f, 1f, 1f); } }
        public static Color black { get { return new Color(0f, 0f, 0f, 1f); } }
        public static Color clear { get { return new Color(0f, 0f, 0f, 0f); } }

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

    // MatFrom routes req.color / req.colorTwo through Color32 before it builds the dictionary key,
    // so the key is 8-bit per channel. Modelled here with round-half-up, which is what Unity's
    // Clamp01*255+round conversion does for the values used in this harness.
    public struct Color32
    {
        public byte r, g, b, a;

        public Color32(byte r, byte g, byte b, byte a) { this.r = r; this.g = g; this.b = b; this.a = a; }

        public static implicit operator Color32(Color c)
        {
            return new Color32(Byte(c.r), Byte(c.g), Byte(c.b), Byte(c.a));
        }

        public static implicit operator Color(Color32 c)
        {
            return new Color(c.r / 255f, c.g / 255f, c.b / 255f, c.a / 255f);
        }

        private static byte Byte(float f)
        {
            if (f <= 0f) return 0;
            if (f >= 1f) return 255;
            return (byte)Math.Round(f * 255f, MidpointRounding.AwayFromZero);
        }

        public override string ToString() { return "RGBA32(" + r + ", " + g + ", " + b + ", " + a + ")"; }
    }

    public struct Vector4
    {
        public float x, y, z, w;
        public Vector4(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }
        public override string ToString() { return "(" + x + ", " + y + ", " + z + ", " + w + ")"; }
    }

    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public override string ToString() { return "(" + x + ", " + y + ", " + z + ")"; }
    }

    // Verse.UnityData has a field of this type, so the type loader needs it even though nothing in
    // the material path reads it. Values are Unity's.
    public enum RuntimePlatform
    {
        OSXEditor = 0, OSXPlayer = 1, WindowsPlayer = 2, WindowsEditor = 7, IPhonePlayer = 8,
        Android = 11, LinuxPlayer = 13, LinuxEditor = 16, WebGLPlayer = 17,
    }

    public class MaterialPropertyBlock
    {
        public void SetFloat(string n, float v) { }
        public void SetTexture(string n, Texture t) { }
        public void SetVector(string n, Vector4 v) { }
        public void SetColor(string n, Color c) { }
    }

    // Verse.ContentFinder<Shader>.TryFindAssetInModBundles mentions these in its body, so the
    // JIT needs them named even on the path that returns null (no mod bundles in the harness).
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
        // Every applied property, in order. Two materials can then be compared property by property
        // instead of "they are different object instances, therefore different".
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

        // MaterialAllocator.Create takes this branch when no ShaderDef is loaded; the real one defers
        // until the engine registry is up, which never happens head-less. Jump straight to the factory.
        public static Material CreateDeferred(Func<Material> factory) { return factory(); }

        public void SetTexture(int id, Texture t) { Applied.Add("tex:" + Shader.PropertyToIDName(id) + "=" + (t == null ? "null" : t.name)); }
        public void SetTexture(string n, Texture t) { Applied.Add("tex:" + n + "=" + (t == null ? "null" : t.name)); }
        public void SetColor(int id, Color c) { Applied.Add("col:" + Shader.PropertyToIDName(id) + "=" + c); }
        public void SetColor(string n, Color c) { Applied.Add("col:" + n + "=" + c); }
        public void SetFloat(string n, float v) { Applied.Add("flt:" + n + "=" + v.ToString("0.###")); }
        public void SetVector(string n, Vector4 v) { Applied.Add("vec:" + n + "=" + v); }
    }
}
