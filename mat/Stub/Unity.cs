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
        private bool _destroyed;

        public virtual string name
        {
            get { return _name; }
            set { _name = value; }
        }

        public int GetInstanceID() { return RuntimeHelpers.GetHashCode(this); }

        public bool IsDestroyed { get { return _destroyed; } }

        // Exact shape of the shipped UnityStubs operator: reference equality, then the
        // destroyed flag decides comparisons against null. A destroyed object is "fake null"
        // to `if (obj == null)` but still a live managed reference.
        public static bool operator ==(Object a, Object b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (ReferenceEquals(b, null)) return a._destroyed;
            if (ReferenceEquals(a, null)) return b._destroyed;
            return false;
        }
        public static bool operator !=(Object a, Object b) { return !(a == b); }

        // `if (mat)` in the shipped IL (ldsfld Material, brtrue) is this conversion.
        public static implicit operator bool(Object o) { return !ReferenceEquals(o, null) && !o._destroyed; }

        // Object.Destroy's no-runtime branch (the one a headless host and an unregistered
        // asset take): Dispose() then mark destroyed, in that order.
        public static void Destroy(Object o)
        {
            if (o == null) return;
            if (o is IDisposable d) d.Dispose();
            o._destroyed = true;
        }

        public static void DestroyImmediate(Object o) { Destroy(o); }
        public static void DestroyImmediate(Object o, bool allowDestroyingAssets) { Destroy(o); }

        public override bool Equals(object o) { return ReferenceEquals(this, o); }
        public override int GetHashCode() { return RuntimeHelpers.GetHashCode(this); }
        public override string ToString() { return _name; }
    }

    public class Texture : Object, IDisposable
    {
        public int width { get; set; }
        public int height { get; set; }

        // The two pieces Waylith's Texture.Dispose actually touches: the GPU allocation is
        // released and nulled, the CPU pixel data is left in place. Modelled as opaque handles;
        // only null / non-null is asserted.
        public object GpuResource { get; set; }
        public object CpuData { get; set; }

        private bool _disposed;

        public bool IsDisposed { get { return _disposed; } }

        public void Dispose()
        {
            GpuResource = null;
            _disposed = true;
        }

        // Exact shape of the shipped UnityStubs Texture.operator==: not Object's _destroyed
        // flag, but _disposed. This is why a texture that was Destroy()ed (Dispose + mark)
        // reads as null to MaterialPool's `req.mainTex == null` guard.
        public static bool operator ==(Texture a, Texture b)
        {
            if (ReferenceEquals(a, b)) return true;
            bool da = ReferenceEquals(a, null) ? true : a._disposed;
            bool db = ReferenceEquals(b, null) ? true : b._disposed;
            if (da | db) return da == db;
            return false;
        }
        public static bool operator !=(Texture a, Texture b) { return !(a == b); }

        public override bool Equals(object o) { return o is Texture t && this == t; }
        public override int GetHashCode() { return RuntimeHelpers.GetHashCode(this); }
    }

    public class Texture2D : Texture
    {
        public static Texture2D whiteTexture { get { return new Texture2D(1, 1) { name = "whiteTexture" }; } }
        public static Texture2D blackTexture { get { return new Texture2D(1, 1) { name = "blackTexture" }; } }
        public static Texture2D redTexture { get { return new Texture2D(1, 1) { name = "redTexture" }; } }
        public static Texture2D greyTexture { get { return new Texture2D(1, 1) { name = "greyTexture" }; } }

        public FilterMode filterMode { get; set; }
        public TextureWrapMode wrapMode { get; set; }
        public int anisoLevel { get; set; }

        public Texture2D() { }
        public Texture2D(int w, int h)
        {
            width = w;
            height = h;
            // A loaded texture owns both a GPU allocation and CPU pixels. Destroy() releases
            // only the former (Waylith.Rendering.Texture.Dispose), which is the state G asserts.
            GpuResource = new object();
            CpuData = new object();
        }
        public Texture2D(int w, int h, TextureFormat fmt, bool mipChain) : this(w, h) { }
        public Texture2D(int w, int h, TextureFormat fmt, bool mipChain, bool linear) : this(w, h) { }

        public void SetPixel(int x, int y, Color c) { }
        public void SetPixels(Color[] c) { }
        public void SetPixels32(Color32[] c) { }
        public Color[] GetPixels() { return new Color[0]; }
        public Color32[] GetPixels32() { return new Color32[0]; }
        public Color GetPixel(int x, int y) { return Color.white; }
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
        public static Color grey { get { return new Color(0.5f, 0.5f, 0.5f, 1f); } }
        public static Color gray { get { return grey; } }
        public static Color yellow { get { return new Color(1f, 0.92f, 0.016f, 1f); } }
        public static Color red { get { return new Color(1f, 0f, 0f, 1f); } }
        public static Color green { get { return new Color(0f, 1f, 0f, 1f); } }
        public static Color blue { get { return new Color(0f, 0f, 1f, 1f); } }
        public static Color magenta { get { return new Color(1f, 0f, 1f, 1f); } }
        public static Color cyan { get { return new Color(0f, 1f, 1f, 1f); } }

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

    // ContentFinder<Texture2D>.Get falls through to Resources.Load when no mod holder has the
    // path. The harness needs exactly one such fixture - UI/Misc/BadTexture - so BaseContent's
    // static initialiser can finish and MatFrom's null-source path returns the real BadMat
    // instead of tripping over a missing asset (a harness artefact, not a game fact).
    public static class Resources
    {
        private static readonly Dictionary<string, Object> Fixtures = new Dictionary<string, Object>();

        public static void Register(string pathSuffix, Object obj)
        {
            lock (Fixtures) Fixtures[pathSuffix] = obj;
        }

        public static T Load<T>(string path) where T : Object
        {
            lock (Fixtures)
            {
                foreach (var kv in Fixtures)
                    if (path != null && path.EndsWith(kv.Key, StringComparison.Ordinal)) return kv.Value as T;
            }
            return null;
        }

        public static T[] LoadAll<T>(string path) where T : Object { return new T[0]; }
        public static void UnloadAsset(Object obj) { }
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

        // _MainTex_ST lives here, shared by every caller that got this material from the pool.
        // mainTextureScale/Offset and SetVector("_MainTex_ST") address the same two entries,
        // exactly as the shipped UnityStubs Material routes both to MaterialTextureTransform.
        private readonly Dictionary<int, Vector2> _scales = new Dictionary<int, Vector2>();
        private readonly Dictionary<int, Vector2> _offsets = new Dictionary<int, Vector2>();
        private readonly Dictionary<int, Vector4> _vectors = new Dictionary<int, Vector4>();

        public Vector2 mainTextureScale
        {
            get { return GetTextureScale(Shader.PropertyToID("_MainTex")); }
            set { SetTextureScale(Shader.PropertyToID("_MainTex"), value); }
        }

        public Vector2 mainTextureOffset
        {
            get { return GetTextureOffset(Shader.PropertyToID("_MainTex")); }
            set { SetTextureOffset(Shader.PropertyToID("_MainTex"), value); }
        }

        public Material(Shader s) { shader = s; }

        public Material(Material src)
        {
            shader = src.shader;
            mainTexture = src.mainTexture;
            color = src.color;
            renderQueue = src.renderQueue;
            Applied.AddRange(src.Applied);
            foreach (var kv in src._scales) _scales[kv.Key] = kv.Value;
            foreach (var kv in src._offsets) _offsets[kv.Key] = kv.Value;
            foreach (var kv in src._vectors) _vectors[kv.Key] = kv.Value;
        }

        // MaterialAllocator.Create takes this branch when no ShaderDef is loaded; the real one defers
        // until the engine registry is up, which never happens head-less. Jump straight to the factory.
        public static Material CreateDeferred(Func<Material> factory) { return factory(); }

        public void SetTexture(int id, Texture t) { Applied.Add("tex:" + Shader.PropertyToIDName(id) + "=" + (t == null ? "null" : t.name)); }
        public void SetTexture(string n, Texture t) { Applied.Add("tex:" + n + "=" + (t == null ? "null" : t.name)); }
        public void SetColor(int id, Color c) { Applied.Add("col:" + Shader.PropertyToIDName(id) + "=" + c); }
        public void SetColor(string n, Color c) { Applied.Add("col:" + n + "=" + c); }
        public void SetFloat(string n, float v) { Applied.Add("flt:" + n + "=" + v.ToString("0.###")); }

        public void SetTextureScale(int id, Vector2 v)
        {
            _scales[id] = v;
            Applied.Add("st-scale:" + Shader.PropertyToIDName(id) + "=" + v);
        }

        public void SetTextureScale(string n, Vector2 v) { SetTextureScale(Shader.PropertyToID(n), v); }

        public void SetTextureOffset(int id, Vector2 v)
        {
            _offsets[id] = v;
            Applied.Add("st-offset:" + Shader.PropertyToIDName(id) + "=" + v);
        }

        public void SetTextureOffset(string n, Vector2 v) { SetTextureOffset(Shader.PropertyToID(n), v); }

        public Vector2 GetTextureScale(int id)
        {
            Vector2 v;
            return _scales.TryGetValue(id, out v) ? v : new Vector2(1f, 1f);
        }

        public Vector2 GetTextureOffset(int id)
        {
            Vector2 v;
            return _offsets.TryGetValue(id, out v) ? v : new Vector2(0f, 0f);
        }

        public void SetVector(int id, Vector4 v)
        {
            string n = Shader.PropertyToIDName(id);
            if (n.EndsWith("_ST", StringComparison.Ordinal))
            {
                // TextureST.Reorder + TrySetTextureST: property (offset.x, offset.y, scale.x, scale.y)
                // is stored on the texture property (id without the _ST suffix), as offset=(x,y),
                // scale=(z,w).
                int texId = Shader.PropertyToID(n.Substring(0, n.Length - 3));
                _offsets[texId] = new Vector2(v.x, v.y);
                _scales[texId] = new Vector2(v.z, v.w);
            }
            else
            {
                _vectors[id] = v;
            }
            Applied.Add("vec:" + n + "=" + v);
        }

        public void SetVector(string n, Vector4 v) { SetVector(Shader.PropertyToID(n), v); }

        public Vector4 GetVector(string n)
        {
            int id = Shader.PropertyToID(n);
            if (n.EndsWith("_ST", StringComparison.Ordinal))
            {
                int texId = Shader.PropertyToID(n.Substring(0, n.Length - 3));
                Vector2 o = GetTextureOffset(texId);
                Vector2 s = GetTextureScale(texId);
                return new Vector4(o.x, o.y, s.x, s.y);
            }
            Vector4 v;
            return _vectors.TryGetValue(id, out v) ? v : new Vector4(0f, 0f, 0f, 0f);
        }
    }
}
