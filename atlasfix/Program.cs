// AtlasFix - does StaticTextureAtlas.Bake keep a tile for a texture that never reached the page,
// and does the GlyphFix atlasbake postfix remove it?
//
// This probe runs the SHIPPED managed assemblies (dlls/*.dll) - no re-implementation of Bake, no
// re-implementation of the residency gate. Three layers of evidence:
//
//   [FACT]  a statement read out of the decompiled/IL form of the shipped code, with file:line
//   [REAL]  a value produced by executing a shipped method (reflection)
//   [GPU]   a value read back from a real Vulkan device (llvmpipe in CI); absent without a device
//
// The default no-device run uses a Priority.First harness postfix to install sentinel GpuResources
// on the atlas page and live source after the shipped Bake returns. GlyphFix's Low-priority postfix
// then faces a discriminating input: live resident, dead not. With --gpu the same run additionally
// performs real Vulkan blits, page readback, and per-cell pixel counts.
//
// usage: AtlasFix --dlls <dir> [--mod <GlyphFix.dll>] [--gpu] [--out <summary.md>]

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace AtlasFix
{
    static class P
    {
        public static int Pass, Fail, Skipped;
        public static readonly List<string> Lines = new List<string>();

        public static void L(string s = "")
        {
            Console.WriteLine(s);
            Lines.Add(s);
        }

        public static void Fact(string s) => L("[FACT] " + s);
        public static void Real(string s) => L("[REAL] " + s);
        public static void Gpu(string s) => L("[GPU] " + s);
        public static void Note(string s) => L("[NOTE] " + s);

        public static void Ok(string what, bool cond, string detail = "")
        {
            string suffix = string.IsNullOrEmpty(detail) ? "" : " (" + detail + ")";
            if (cond) { Pass++; L("[PASS] " + what + suffix); }
            else { Fail++; L("[FAIL] " + what + suffix); }
        }

        public static void Skip(string what, string why)
        {
            Skipped++;
            L("[SKIP] " + what + ": " + why);
        }
    }

    static class Engine
    {
        public static string Dir;
        public static Assembly Waylith, CSharp, Unity, Harmony;
        public static readonly Dictionary<string, Assembly> ByName =
            new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);

        public static void Load(string dir)
        {
            Dir = dir;
            AssemblyLoadContext.Default.Resolving += (ctx, name) =>
            {
                if (ByName.TryGetValue(name.Name, out var a)) return a;
                string p = Path.Combine(dir, name.Name + ".dll");
                if (File.Exists(p)) { var asm = LoadFile(p); ByName[name.Name] = asm; return asm; }
                return null;
            };
            foreach (var f in Directory.GetFiles(dir, "*.dll").OrderBy(x => x, StringComparer.Ordinal))
            {
                try { var a = LoadFile(f); ByName[a.GetName().Name] = a; }
                catch (Exception e) { Console.Error.WriteLine("[load] " + Path.GetFileName(f) + ": " + e.Message); }
            }
            Waylith = Find("Waylith");
            CSharp = Find("Assembly-CSharp");
            Unity = Find("UnityStubs");
            Harmony = Find("0Harmony");
        }

        static Assembly LoadFile(string p) => AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(p));
        static Assembly Find(string n) => ByName.TryGetValue(n, out var a) ? a : null;

        public static Type T(string fullName)
        {
            foreach (var a in ByName.Values)
            {
                var t = a.GetType(fullName, throwOnError: false);
                if (t != null) return t;
            }
            return null;
        }

        static readonly Dictionary<string, Type> Simple = new Dictionary<string, Type>(StringComparer.Ordinal);
        public static Type TSimple(string simple)
        {
            if (Simple.TryGetValue(simple, out var hit)) return hit;
            Type best = null;
            foreach (var a in ByName.Values)
            {
                Type[] types;
                try { types = a.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types.Where(x => x != null).ToArray(); }
                foreach (var t in types)
                    if (t.Name == simple && !t.IsGenericTypeDefinition) { best = t; break; }
                if (best != null) break;
            }
            Simple[simple] = best;
            return best;
        }
    }

    /// <summary>Reflection plumbing: one cached lookup per (type, member) and optional-argument filling.</summary>
    static class R
    {
        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        public static MethodInfo M(Type t, string name, int argc = -1)
        {
            if (t == null) return null;
            foreach (var m in t.GetMethods(Any | BindingFlags.Static))
            {
                if (m.Name != name) continue;
                if (argc >= 0 && m.GetParameters().Length != argc) continue;
                return m;
            }
            return null;
        }

        public static MethodInfo MExact(Type t, string name, params string[] parameterTypeNames)
        {
            if (t == null) return null;
            foreach (var m in t.GetMethods(Any | BindingFlags.Static))
            {
                if (m.Name != name) continue;
                var ps = m.GetParameters();
                if (ps.Length != parameterTypeNames.Length) continue;
                bool ok = true;
                for (int i = 0; i < ps.Length; i++)
                {
                    string want = parameterTypeNames[i];
                    if (ps[i].ParameterType.Name != want && ps[i].ParameterType.FullName != want) { ok = false; break; }
                }
                if (ok) return m;
            }
            return null;
        }

        public static object Default(Type t) => t.IsValueType ? Activator.CreateInstance(t) : null;

        public static object Coerce(Type t, object v)
        {
            if (v == null) return Default(t);
            if (t.IsInstanceOfType(v)) return v;
            if (t == typeof(nint)) return (nint)Convert.ToInt64(v, CultureInfo.InvariantCulture);
            if (t == typeof(ulong)) return Convert.ToUInt64(v, CultureInfo.InvariantCulture);
            if (t == typeof(long)) return Convert.ToInt64(v, CultureInfo.InvariantCulture);
            if (t == typeof(int)) return Convert.ToInt32(v, CultureInfo.InvariantCulture);
            if (t.IsEnum) return Enum.ToObject(t, v);
            return v;
        }

        public static object Invoke(MethodInfo m, object target, params object[] args)
        {
            if (m == null) throw new MissingMethodException("method not resolved");
            var ps = m.GetParameters();
            var final = new object[ps.Length];
            for (int i = 0; i < ps.Length; i++)
            {
                object v = i < args.Length ? args[i] : null;
                final[i] = v == null ? Default(ps[i].ParameterType) : Coerce(ps[i].ParameterType, v);
            }
            return m.Invoke(m.IsStatic ? null : target, final);
        }

        public static object Call(object target, string name, params object[] args)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            var m = M(target.GetType(), name, args.Length) ?? M(target.GetType(), name);
            return Invoke(m, target, args);
        }

        public static object CallS(Type t, string name, params object[] args)
        {
            var m = M(t, name, args.Length) ?? M(t, name);
            return Invoke(m, null, args);
        }

        public static object Get(object target, string member)
        {
            if (target == null) return null;
            Type t = target.GetType();
            var f = t.GetFields(Any).FirstOrDefault(x => x.Name == member);
            if (f != null) return f.GetValue(target);
            var p = t.GetProperties(Any).FirstOrDefault(x => x.Name == member && x.GetIndexParameters().Length == 0);
            return p?.GetValue(target, null);
        }

        public static object GetS(Type t, string member)
        {
            if (t == null) return null;
            var f = t.GetFields(AnyStatic).FirstOrDefault(x => x.Name == member);
            if (f != null) return f.GetValue(null);
            var p = t.GetProperties(AnyStatic).FirstOrDefault(x => x.Name == member && x.GetIndexParameters().Length == 0);
            return p?.GetValue(null, null);
        }

        public static void Set(object target, string member, object value)
        {
            Type t = target.GetType();
            var f = t.GetFields(Any).FirstOrDefault(x => x.Name == member);
            if (f != null) { f.SetValue(target, value); return; }
            t.GetProperties(Any).FirstOrDefault(x => x.Name == member && x.GetIndexParameters().Length == 0)
                ?.SetValue(target, value, null);
        }

        public static void SetS(Type t, string member, object value)
        {
            if (t == null) return;
            var f = t.GetFields(AnyStatic).FirstOrDefault(x => x.Name == member);
            if (f != null) { f.SetValue(null, value); return; }
            t.GetProperties(AnyStatic).FirstOrDefault(x => x.Name == member && x.GetIndexParameters().Length == 0)
                ?.SetValue(null, value, null);
        }

        public static bool Has(Type t, string member)
        {
            if (t == null) return false;
            return t.GetField(member, Any) != null || t.GetProperty(member, Any) != null
                   || t.GetField(member, AnyStatic) != null || t.GetProperty(member, AnyStatic) != null;
        }
    }

    static class Program
    {
        static Type _tex2d, _atlas, _groupMut, _keyMut, _groupKey, _gpuSvc, _graphic, _matPool, _matReq;
        static bool _sentinelInstalled, _sentinelPageResident;
        static int Main(string[] argv)
        {
            string dlls = Argv(argv, "--dlls") ?? "dlls";
            string mod = Argv(argv, "--mod");
            string outPath = Argv(argv, "--out");
            bool gpu = argv.Contains("--gpu");

            P.L("# AtlasFix — StaticTextureAtlas.Bake keeps tiles for textures that never reached the page");
            P.L();
            P.L("- dlls: `" + Path.GetFullPath(dlls) + "`");
            foreach (var f in new[] { "Assembly-CSharp.dll", "UnityStubs.dll", "Waylith.dll" })
            {
                string p = Path.Combine(dlls, f);
                if (File.Exists(p))
                    P.L("- " + f + ": " + new FileInfo(p).Length + " bytes, md5 " + Md5(p));
            }
            P.L("- mode: " + (gpu ? "real Vulkan device requested" : "no device (negative control only)"));
            P.L();

            Engine.Load(dlls);
            _tex2d = Engine.T("UnityEngine.Texture2D");
            _atlas = Engine.T("Verse.StaticTextureAtlas");
            _keyMut = Engine.T("Verse.TextureAtlasGroupKey");
            _groupMut = Engine.T("Verse.TextureAtlasGroup");
            _gpuSvc = Engine.T("Waylith.Rendering.GpuTextureService");
            _graphic = Engine.T("Verse.Graphic");
            _matPool = Engine.T("Verse.MaterialPool");
            _matReq = Engine.T("Verse.MaterialRequest");
            if (_tex2d == null || _atlas == null || _keyMut == null || _groupMut == null)
            {
                P.L("[FAIL] required types missing (tex2d=" + (_tex2d != null) + " atlas=" + (_atlas != null)
                    + " key=" + (_keyMut != null) + " group=" + (_groupMut != null) + ")");
                return 2;
            }

            bool gpuReady = false;
            if (gpu) gpuReady = BringUpDevice();
            if (gpu) P.Ok("device came up (--gpu was requested)", gpuReady, gpuReady ? "lavapipe" : "see the [SKIP] line above");

            Environment();
            InstallMipNoOp();
            var facts = Measure(gpuReady);
            _facts = facts;
            if (!gpuReady)
            {
                bool sentinel = InstallSentinelPostfix();
                P.Ok("no-device sentinel postfix installed", sentinel,
                     sentinel ? "Priority.First page + live residency" : "patch refused");
            }

            // ---------------------------------------------------------------- control: no patch
            P.L();
            P.L("## 1. Control — the shipped Bake, no patch installed");
            P.L();
            var a1 = BakeAtlas("control", facts, gpuReady);
            if (!gpuReady)
            {
                P.Ok("the fixture is discriminating after Bake (sentinel live resident, dead not)",
                    facts.LiveResident && !facts.DeadResident,
                    "page=" + _sentinelPageResident + " live=" + facts.LiveResident + " dead=" + facts.DeadResident);
            }

            P.L();
            P.L("## 2. Control — what the graphic does with that tile");
            P.L();
            var harm = ReplacementInfo(a1, facts);

            // ---------------------------------------------------------------- patched: GlyphFix atlasbake
            P.L();
            P.L("## 3. GlyphFix atlasbake installed (real Harmony patch on the shipped method)");
            P.L();
            bool patched = InstallMod(mod);

            P.L();
            P.L("## 4. Patched — same two textures, same shipped Bake");
            P.L();
            var a2 = patched ? BakeAtlas("patched", facts, gpuReady) : null;

            if (patched && a2 != null)
            {
                string after = ModuleState(patched);
                P.Real("module state after the patched bake: " + after);
                P.Ok("the module's own counters show it handled the bake", Num(after, "bakes") >= 1, after);
                P.Ok("the module unregistered at least one tile", Num(after, "dropped") >= 1, after);
            }

            P.L();
            P.L("## 5. Patched — replacement info");
            P.L();
            var harm2 = a2 != null ? ReplacementInfo(a2, facts) : null;

            // ---------------------------------------------------------------- verdicts
            P.L();
            P.L("## 6. Verdicts");
            P.L();

            P.Ok("control: Bake registers a tile for the non-resident texture",
                a1 != null && a1.DeadInTiles, a1 == null ? "bake failed" : "tiles=" + a1.Tiles + " of " + a1.Textures);
            P.Ok("control: Bake did not throw (the failure is silent)",
                a1 != null && a1.Threw == null, a1?.Threw ?? (a1 == null ? "no bake" : "no exception"));
            if (gpuReady)
            {
                P.Ok("control: the non-resident texture's cell has no pixels (page stays clear)",
                    a1 != null && a1.DeadCellOpaque == 0, a1?.PixelNote ?? "not measured");
                P.Ok("control: a resident texture's cell does have pixels",
                    a1 != null && a1.LiveCellOpaque > 0, a1?.PixelNote ?? "not measured");
            }
            else
            {
                P.Skip("control: page pixels", "no device: the page has no GPU resource at all, so every cell is trivially empty and the measurement cannot discriminate");
            }
            P.Ok("control: TryGetTextureAtlasReplacementInfo(true) — the source texture is swapped for that cell",
                harm != null && harm.Dead, harm?.Note ?? "not evaluated");
            P.Ok("control: the live texture is replaced the same way (the mechanism works)",
                harm != null && harm.Live, harm?.Note ?? "not evaluated");

            if (!patched)
            {
                P.Skip("patched: tile removal", mod == null ? "no --mod given" : "the module did not install");
            }
            else
            {
                P.Ok("patched: the non-resident texture is no longer registered in tiles",
                    a2 != null && !a2.DeadInTiles, a2 == null ? "bake failed" : "tiles=" + a2.Tiles + " of " + a2.Textures);
                P.Ok("patched: tiles and textures still agree in count (no dangling entries)",
                    a2 != null && a2.Tiles == a2.Textures, a2 == null ? "bake failed" : a2.Tiles + "/" + a2.Textures);
                P.Ok("patched: neither cache key is left behind (a page with a hole is never cached)",
                    a2 != null && a2.ColorKey == null && a2.MaskKey == null,
                    a2 == null ? "bake failed" : "color=" + (a2.ColorKey ?? "null") + " mask=" + (a2.MaskKey ?? "null"));
                P.Ok("patched: TryGetTextureAtlasReplacementInfo(false) — the graphic keeps its own texture",
                    harm2 != null && !harm2.Dead, harm2?.Note ?? "not evaluated");
                if (gpuReady)
                {
                    P.Ok("patched: the resident texture is still registered and still hits",
                        a2 != null && a2.LiveInTiles && harm2 != null && harm2.Live,
                        a2 == null ? "bake failed" : "inTiles=" + a2.LiveInTiles + " hit=" + (harm2?.Live ?? false));
                    P.Ok("patched: the resident texture's cell still has pixels",
                        a2 != null && a2.LiveCellOpaque > 0, a2?.PixelNote ?? "not measured");
                }
                else
                {
                    P.Ok("patched: sentinel-resident texture is still registered and still hits",
                        _sentinelPageResident && a2 != null && a2.LiveInTiles && harm2 != null && harm2.Live,
                        a2 == null ? "bake failed" : "page=" + _sentinelPageResident
                        + " inTiles=" + a2.LiveInTiles + " hit=" + (harm2?.Live ?? false));
                    P.Skip("patched: resident texture pixels",
                           "sentinel verifies atlas bookkeeping, not the Vulkan blit");
                }
            }

            P.L();
            P.L("RESULT: " + (P.Fail == 0 ? "PASS" : "FAIL") + " — assertions=" + (P.Pass + P.Fail)
                + " passed=" + P.Pass + " failed=" + P.Fail + " skipped=" + P.Skipped);
            if (outPath != null)
            {
                try { File.WriteAllLines(outPath, P.Lines); } catch (Exception e) { Console.Error.WriteLine("summary: " + e.Message); }
            }
            return P.Fail == 0 ? 0 : 1;
        }

        // ------------------------------------------------------------------ environment

        static void Environment()
        {
            P.L("## 0. Environment facts that make Bake reachable headless");
            P.L();
            try
            {
                var dp = Engine.T("Verse.DeepProfiler");
                var en = dp?.GetField("enabled", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (en != null) { en.SetValue(null, false); P.Real("Verse.DeepProfiler.enabled = false (Bake opens a DeepProfilerScope, which otherwise reads Prefs.LogVerbose)"); }
            }
            catch (Exception e) { P.Note("DeepProfiler.enabled: " + e.Message); }

            try
            {
                var tcs = Engine.T("Waylith.Graphics.Textures.TextureCompressionSettings")
                          ?? Engine.TSimple("TextureCompressionSettings");
                object target = R.GetS(tcs, "Target");
                P.Real("TextureCompressionSettings.Target = " + target + " -> atlas compression is "
                       + (target != null && target.ToString() != "None" ? "ON (Bake would try to compress)" : "off (Bake takes the blit path)"));
            }
            catch (Exception e) { P.Note("TextureCompressionSettings: " + e.Message); }

            try
            {
                var gta = Engine.T("Verse.GlobalTextureAtlasManager");
                object enabled = R.GetS(gta, "AtlasCompressionEnabled");
                P.Real("GlobalTextureAtlasManager.AtlasCompressionEnabled = " + enabled);
            }
            catch (Exception e) { P.Note("AtlasCompressionEnabled: " + e.Message); }

            try
            {
                // MaterialAllocator.Create() refuses to run "off main thread" and returns null, which
                // would turn Bake into a NullReferenceException before the blit loop. The game sets
                // this during boot; a console host has to do it itself. Nothing else depends on it.
                var t = Engine.T("Verse.UnityData");
                if (t != null)
                {
                    R.SetS(t, "initialized", true);
                    R.SetS(t, "mainThreadId", System.Threading.Thread.CurrentThread.ManagedThreadId);
                    R.SetS(t, "dataPath", Path.GetFullPath(Engine.Dir));
                    P.Real("UnityData: mainThreadId=" + R.GetS(t, "mainThreadId")
                           + " initialized=" + R.GetS(t, "initialized")
                           + " -> IsInMainThread=" + R.GetS(t, "IsInMainThread"));
                }
            }
            catch (Exception e) { P.Note("UnityData: " + e.Message); }

            try
            {
                // Bake resolves Custom/BlitExact through Shader.Find -> Resources -> ShaderRegistry,
                // whose static ctor loads <AppContext.BaseDirectory>/Rendering/Shaders. Warm it here so
                // a missing shader tree shows up as this line and not as a Bake exception.
                var shaderT = Engine.T("UnityEngine.Shader");
                object sh = R.Invoke(R.M(shaderT, "Find", 1), null, new object[] { "Custom/BlitExact" });
                P.Real("Shader.Find(\"Custom/BlitExact\") = " + (sh == null ? "null (Bake's material is deferred; the blit path is vkCmdBlitImage and never compiles it)" : "resolved"));
            }
            catch (Exception e) { P.Note("Shader.Find warm-up: " + Ex(e)); }

            P.Fact("Bake() runs BuildMeshesForUvs (tiles.Add for every texture) before any blit — static.cs:164-171");
            P.Fact("BlitTexturesToColorAtlas clears the page to Color.clear and calls Graphics.DrawTexture per tile — static.cs:474-492");
            P.Fact("DrawTexture's source gate is `GpuTexture != null` after EnsureGpuResident(); on failure it calls StubWarner.LogStub and returns — UnityStubs Graphics.cs:560-596");
            P.Fact("Graphic.TryGetTextureAtlasReplacementInfo swaps mainTexture for atlas.ColorTexture and the uvRect for the tile — Graphic.cs:172633-172659");
        }

        // ------------------------------------------------------------------ textures

        sealed class Facts
        {
            public object Live, Dead;
            public bool LiveResident, DeadResident;
        }

        static Facts Measure(bool gpuReady)
        {
            var f = new Facts();
            P.L();
            P.L("## 0b. The two textures");
            P.L();

            // live: a real 128x128 RGBA texture with opaque pixels, the way a decoded icon looks.
            f.Live = MakeLive(128);
            // dead: exactly the deferred-loading shape - metadata only, no CPU levels, SourcePath set
            // to a file that is not there, so nothing can ever hydrate it.
            f.Dead = MakeUnhydratable(128);

            f.LiveResident = Resident(f.Live);
            f.DeadResident = Resident(f.Dead);
            P.Real("live = '" + Name(f.Live) + "' " + Size(f.Live) + " CpuData=" + (R.Get(f.Live, "CoreTexture") != null)
                   + " SourcePath=" + (SourcePath(f.Live) ?? "null") + " GpuResource=" + Gpu(f.Live)
                   + " -> EnsureGpuResident()=" + f.LiveResident);
            P.Real("dead = '" + Name(f.Dead) + "' " + Size(f.Dead) + " CpuData=" + (R.Get(f.Dead, "CoreTexture") != null)
                   + " SourcePath=" + (SourcePath(f.Dead) ?? "null") + " GpuResource=" + Gpu(f.Dead)
                   + " -> EnsureGpuResident()=" + f.DeadResident);
            if (f.LiveResident && !f.DeadResident)
                P.Ok("the fixture is discriminating before Bake", true, "live=true dead=false");
            else if (!gpuReady)
                P.Note("pre-Bake residency is not discriminating without a device; the sentinel postfix runs after Bake");
            else
                P.Ok("the fixture is discriminating (live resident, dead not)", false,
                     "live=" + f.LiveResident + " dead=" + f.DeadResident);
            return f;
        }

        static object MakeLive(int size)
        {
            // Pick the ctor by shape and take the TextureFormat enum from its own parameter type: a
            // second type called TextureFormat elsewhere in the closure makes Enum.Parse produce a
            // value the binder will not accept.
            var ctor = _tex2d.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .First(c =>
                {
                    var ps = c.GetParameters();
                    return ps.Length == 4 && ps[0].ParameterType == typeof(int) && ps[1].ParameterType == typeof(int)
                           && ps[2].ParameterType.Name == "TextureFormat" && ps[3].ParameterType == typeof(bool);
                });
            object fmtValue = Enum.Parse(ctor.GetParameters()[2].ParameterType, "RGBA32");
            var tex = ctor.Invoke(new object[] { size, size, fmtValue, false });
            R.Set(tex, "name", "atlasfix_live");

            var set = R.MExact(_tex2d, "SetPixels32", "Color32[]");
            Type c32 = set.GetParameters()[0].ParameterType.GetElementType();
            var arr = Array.CreateInstance(c32, size * size);
            object px = Activator.CreateInstance(c32, new object[] { (byte)230, (byte)140, (byte)40, (byte)255 });
            for (int i = 0; i < arr.Length; i++) arr.SetValue(px, i);
            R.Invoke(set, tex, new object[] { arr });
            R.Invoke(R.M(_tex2d, "Apply", 2) ?? R.M(_tex2d, "Apply"), tex, new object[0]);
            return tex;
        }

        static object MakeUnhydratable(int size)
        {
            // Waylith.Rendering.Texture2D.NewFileTexture(path, fullPath, w, h, format, mipCount, opaque,
            // premultiply, swizzle) is what the deferred loader uses: CpuData allocated but empty,
            // SourcePath recorded, no GPU resource. Pointing it at a missing file makes it the shape
            // the deferred window produces for content that cannot be hydrated.
            var core = Engine.T("Waylith.Rendering.Texture2D");
            var m = core?.GetMethod("NewFileTexture", BindingFlags.NonPublic | BindingFlags.Static);
            string missing = Path.Combine(Path.GetTempPath(), "atlasfix_missing_" + Guid.NewGuid().ToString("N") + ".png");
            var ps = m?.GetParameters();
            Type fmtType = ps != null && ps.Length > 4 ? ps[4].ParameterType : null;
            Type swType = ps != null && ps.Length > 8 ? ps[8].ParameterType : null;
            object coreTex;
            if (m != null && fmtType != null && fmtType.IsEnum)
            {
                coreTex = R.Invoke(m, null, new object[]
                {
                    missing, missing, size, size, EnumOf(fmtType, "Rgba32", "RGBA32", "Rgba"), 1, false, false,
                    swType != null ? Activator.CreateInstance(swType) : null
                });
            }
            else
            {
                // fallback shape: uninitialised CPU storage, no source path
                var ctor2 = core.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .First(c => c.GetParameters().Length == 6 && c.GetParameters()[2].ParameterType.IsEnum);
                coreTex = ctor2.Invoke(new object[]
                    { size, size, EnumOf(ctor2.GetParameters()[2].ParameterType, "Rgba32", "RGBA32", "Rgba"), false, 1, true });
            }
            var ctor = _tex2d.GetConstructors(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
                             .First(c => c.GetParameters().Length == 1
                                         && c.GetParameters()[0].ParameterType == core);
            var tex = ctor.Invoke(new[] { coreTex });
            R.Set(tex, "name", "atlasfix_dead");
            return tex;
        }

        static bool Resident(object tex)
        {
            try
            {
                var m = R.M(_tex2d, "EnsureGpuResident", 0);
                return m != null && m.Invoke(tex, null) is bool b && b;
            }
            catch (Exception e)
            {
                P.Note("EnsureGpuResident('" + Name(tex) + "'): " + Ex(e));
                return false;
            }
        }

        static string Gpu(object tex)
        {
            try
            {
                object core = R.Get(tex, "CoreTexture");
                object g = core == null ? null : R.Get(core, "GpuResource");
                return g == null ? "null" : "present";
            }
            catch { return "?"; }
        }

        static string SourcePath(object tex)
        {
            try
            {
                object core = R.Get(tex, "CoreTexture");
                return core == null ? null : R.Get(core, "SourcePath") as string;
            }
            catch { return null; }
        }

        static string Name(object tex)
        {
            try { return R.Get(tex, "name") as string ?? "?"; } catch { return "?"; }
        }

        static string Size(object tex)
        {
            try { return R.Get(tex, "width") + "x" + R.Get(tex, "height"); } catch { return "?x?"; }
        }

        // ------------------------------------------------------------------ bake

        sealed class BakeResult
        {
            public object Atlas;
            public int Textures, Tiles;
            public bool DeadInTiles, LiveInTiles, Adopted;
            public string ColorKey, MaskKey;
            public string Threw;
            public int DeadCellOpaque = -1, LiveCellOpaque = -1, PageOpaque = -1;
            public string PixelNote;
            public string Group;
        }

        static Facts _facts;

        static BakeResult BakeAtlas(string label, Facts facts, bool gpuReady)
        {
            var r = new BakeResult();
            try
            {
                var key = Activator.CreateInstance(_keyMut);
                R.Set(key, "group", Enum.Parse(_groupMut, "Misc"));
                R.Set(key, "hasMask", false);
                r.Group = key.ToString();
                var atlas = Activator.CreateInstance(_atlas, new[] { key });
                r.Atlas = atlas;

                var insert = R.M(_atlas, "Insert", 2);
                R.Invoke(insert, atlas, new object[] { facts.Live, null });
                R.Invoke(insert, atlas, new object[] { facts.Dead, null });

                var bake = _atlas.GetMethod("Bake", BindingFlags.Public | BindingFlags.Instance,
                                            null, new[] { typeof(bool) }, null);
                P.Fact(label + ": calling the shipped StaticTextureAtlas.Bake(bool) on 1 resident + 1 unhydratable texture");
                try
                {
                    bake.Invoke(atlas, new object[] { false });
                }
                catch (Exception e)
                {
                    r.Threw = Ex(e);
                    P.Note(label + ": Bake threw " + r.Threw);
                }

                var textures = R.Get(atlas, "textures") as IList;
                var tiles = R.Get(atlas, "tiles") as IDictionary;
                r.Textures = textures?.Count ?? -1;
                r.Tiles = tiles?.Count ?? -1;
                r.DeadInTiles = tiles != null && tiles.Contains(facts.Dead);
                r.LiveInTiles = tiles != null && tiles.Contains(facts.Live);
                r.Adopted = R.Get(atlas, "AdoptedFromCache") is bool b && b;
                r.ColorKey = R.Get(atlas, "colorCacheKey") as string;
                r.MaskKey = R.Get(atlas, "maskCacheKey") as string;

                P.Real(label + ": AdoptedFromCache=" + r.Adopted + " textures=" + r.Textures + " tiles=" + r.Tiles
                       + " deadInTiles=" + r.DeadInTiles + " liveInTiles=" + r.LiveInTiles
                       + " colorCacheKey=" + (r.ColorKey ?? "null"));

                object page = R.Get(atlas, "ColorTexture");
                object pageGpu = page == null ? null : R.Get(R.Get(page, "CoreTexture"), "GpuResource");
                P.Real(label + ": page " + (page == null ? "null" : R.Get(page, "width") + "x" + R.Get(page, "height"))
                       + " GpuResource=" + (pageGpu == null ? "null" : "present"));

                if (gpuReady && pageGpu != null)
                    MeasureCells(r, atlas, facts, page);
                else if (gpuReady)
                    r.PixelNote = "page has no GPU resource even with a device";
                else
                    r.PixelNote = "no device: page GpuResource=null";

                if (r.PixelNote != null) P.Real(label + ": " + r.PixelNote);
            }
            catch (Exception e)
            {
                r.Threw ??= e.GetType().Name + ": " + e.Message;
                P.Note(label + ": " + e.GetType().Name + ": " + e.Message);
            }
            return r;
        }

        /// <summary>Read mip0 back from the page and count opaque texels in each tile's cell.</summary>
        static void MeasureCells(BakeResult r, object atlas, Facts facts, object page)
        {
            try
            {
                var tiles = R.Get(atlas, "tiles") as IDictionary;
                int w = Convert.ToInt32(R.Get(page, "width"));
                int h = Convert.ToInt32(R.Get(page, "height"));
                object gpu = R.Get(R.Get(page, "CoreTexture"), "GpuResource");
                var m = _gpuSvc?.GetMethod("ReadbackTexture2DLevels", BindingFlags.Public | BindingFlags.Static);
                if (m == null) { r.PixelNote = "ReadbackTexture2DLevels not found"; return; }
                var levels = m.Invoke(null, new object[] { gpu, w, h, 1, 4, null }) as byte[][];
                if (levels == null || levels.Length == 0 || levels[0] == null)
                {
                    r.PixelNote = "readback returned nothing";
                    return;
                }
                byte[] px = levels[0];
                int Count(RectI rect)
                {
                    int n = 0;
                    for (int y = rect.Y; y < rect.Y + rect.H; y++)
                    {
                        if (y < 0 || y >= h) continue;
                        for (int x = rect.X; x < rect.X + rect.W; x++)
                        {
                            if (x < 0 || x >= w) continue;
                            int i = (y * w + x) * 4;
                            if (i + 3 < px.Length && px[i + 3] != 0) n++;
                        }
                    }
                    return n;
                }

                int total = 0;
                for (int i = 3; i < px.Length; i += 4) if (px[i] != 0) total++;
                r.PageOpaque = total;

                if (tiles != null)
                {
                    if (tiles.Contains(facts.Dead)) r.DeadCellOpaque = Count(CellOf(tiles[facts.Dead], w, h));
                    if (tiles.Contains(facts.Live)) r.LiveCellOpaque = Count(CellOf(tiles[facts.Live], w, h));
                }
                r.PixelNote = "page=" + w + "x" + h + " opaqueTexels=" + total
                              + " deadCellOpaque=" + r.DeadCellOpaque + " liveCellOpaque=" + r.LiveCellOpaque
                              + (r.DeadCellOpaque < 0 ? " (dead tile absent)" : "");
            }
            catch (Exception e)
            {
                r.PixelNote = "readback failed: " + Ex(e);
            }
        }

        struct RectI { public int X, Y, W, H; }

        /// <summary>The same four lines BlitTexturesToColorAtlas uses to place a tile (static.cs:486-490).</summary>
        static RectI CellOf(object tile, int pageW, int pageH)
        {
            object uv = R.Get(tile, "uvRect");
            float x = Convert.ToSingle(R.Get(uv, "x"), CultureInfo.InvariantCulture);
            float y = Convert.ToSingle(R.Get(uv, "y"), CultureInfo.InvariantCulture);
            float w = Convert.ToSingle(R.Get(uv, "width"), CultureInfo.InvariantCulture);
            float h = Convert.ToSingle(R.Get(uv, "height"), CultureInfo.InvariantCulture);
            int px = (int)Math.Round(x * pageW);
            int py = (int)Math.Round((1f - y) * pageH);
            int pw = (int)Math.Round(w * pageW);
            int ph = (int)Math.Round(h * pageH);
            py -= ph;
            return new RectI { X = px, Y = py, W = pw, H = ph };
        }

        // ------------------------------------------------------------------ replacement info

        sealed class Harm
        {
            public bool Dead, Live;
            public string Note;
        }

        static Harm ReplacementInfo(BakeResult r, Facts facts)
        {
            var h = new Harm();
            if (r?.Atlas == null) { h.Note = "no atlas"; return h; }
            try
            {
                var gta = Engine.T("Verse.GlobalTextureAtlasManager");
                var atlases = R.GetS(gta, "staticTextureAtlases") as IList;
                atlases?.Add(r.Atlas);
                try
                {
                    h.Dead = Try(r.Atlas, facts.Dead, "dead");
                    h.Live = Try(r.Atlas, facts.Live, "live");
                }
                finally
                {
                    atlases?.Remove(r.Atlas);
                    ClearReplacementCache();
                }
                h.Note = "dead=" + h.Dead + " live=" + h.Live;
            }
            catch (Exception e)
            {
                h.Note = Ex(e);
                P.Note("replacement info: " + h.Note);
            }
            return h;
        }

        static bool _printedMatError;

        static bool Try(object atlas, object texture, string label)
        {
            if (texture == null) return false;
            try
            {
                var matFrom = R.MExact(_matPool, "MatFrom", "Texture2D") ?? R.M(_matPool, "MatFrom", 1);
                object mat = R.Invoke(matFrom, null, new object[] { texture });
                if (mat == null)
                {
                    if (!_printedMatError) { _printedMatError = true; P.Note("MaterialPool.MatFrom returned null for " + label); }
                    return false;
                }
                var m = R.M(_graphic, "TryGetTextureAtlasReplacementInfo", 7);
                if (m == null) { P.Note("TryGetTextureAtlasReplacementInfo not found"); return false; }
                var ps = m.GetParameters();
                object groupVal = Enum.Parse(_groupMut, "Misc");
                object[] args =
                {
                    mat, groupVal, false, false,
                    R.Default(ps[4].ParameterType), R.Default(ps[5].ParameterType), R.Default(ps[6].ParameterType)
                };
                object ok = m.Invoke(null, args);
                bool hit = ok is bool b && b;
                object outMat = args[4];
                object page = outMat == null ? null : R.Get(outMat, "mainTexture");
                P.Real("TryGetTextureAtlasReplacementInfo(" + label + ") = " + hit
                       + (hit ? " -> out material mainTexture=" + Name(page) : "") + " uvRects=" + (args[5] is Array ua ? ua.Length : 0));
                return hit;
            }
            catch (Exception e)
            {
                if (!_printedMatError)
                {
                    _printedMatError = true;
                    P.Note("replacement info(" + label + "): " + Ex(e));
                }
                return false;
            }
        }

        static void ClearReplacementCache()
        {
            try
            {
                var f = _graphic?.GetField("replacementInfoCache", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (f?.GetValue(null) is IDictionary d) d.Clear();
            }
            catch { }
        }

        // ------------------------------------------------------------------ the mod

        static bool InstallMod(string modPath)
        {
            if (string.IsNullOrEmpty(modPath) || !File.Exists(modPath))
            {
                P.Skip("install GlyphFix", modPath == null ? "no --mod" : "not found: " + modPath);
                return false;
            }
            try
            {
                var asm = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(modPath));
                P.Real("loaded " + asm.GetName().Name + " " + asm.GetName().Version + " from " + Path.GetFileName(modPath));
                var t = asm.GetType("AgentTouchFix.AtlasBakeFix", false);
                if (t == null) { P.L("[FAIL] AgentTouchFix.AtlasBakeFix not present in the assembly"); return false; }
                _txAtlasType = t;
                // The module installs itself from its static ctor ([StaticConstructorOnStartup]); running
                // the ctor is exactly what StaticConstructorOnStartupUtility.CallAll() does.
                RuntimeHelpers.RunClassConstructor(t.TypeHandle);
                P.Real("ran AgentTouchFix.AtlasBakeFix's static constructor ([StaticConstructorOnStartup])");

                var bake = _atlas.GetMethod("Bake", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(bool) }, null);
                var harm = Engine.Harmony;
                if (harm != null)
                {
                    var htype = harm.GetType("HarmonyLib.Harmony");
                    object info = htype.GetMethod("GetPatchInfo", new[] { typeof(MethodBase) })
                                       ?.Invoke(null, new object[] { bake });
                    int postfixes = 0;
                    string owners = "";
                    if (info != null)
                    {
                        object listObj = null;
                        var f = info.GetType().GetField("postfixes", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (f != null) listObj = f.GetValue(info);
                        if (listObj == null)
                        {
                            var p = info.GetType().GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                                             .FirstOrDefault(x => x.Name == "Postfixes");
                            listObj = p?.GetValue(info);
                        }
                        if (listObj is IEnumerable en)
                        {
                            foreach (object x in en)
                            {
                                postfixes++;
                                owners += (R.Get(x, "owner") as string ?? "?") + ",";
                            }
                        }
                    }
                    P.Real("Harmony.GetPatchInfo(StaticTextureAtlas.Bake) -> info=" + (info != null)
                           + " postfixes=" + postfixes + " owners=[" + owners + "]");
                    if (info != null)
                    {
                        P.Note("PatchInfo members: "
                               + string.Join(" ", info.GetType()
                                   .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                                   .Select(x => x.FieldType.Name + " " + x.Name))
                               + " | " + string.Join(" ", info.GetType()
                                   .GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                                   .Select(x => x.PropertyType.Name + " " + x.Name)));
                    }
                }
                else P.Note("0Harmony not loaded; cannot read patch info");

                string state = t.GetMethod("Summary", BindingFlags.NonPublic | BindingFlags.Static)?.Invoke(null, null) as string;
                if (state != null) P.Real("module state: " + state);
                return true;
            }
            catch (Exception e)
            {
                P.L("[FAIL] install GlyphFix: " + Ex(e));
                return false;
            }
        }

        // ------------------------------------------------------------------ device

        /// <summary>
        /// A real device without the engine's window/render loop: GLFW (hidden window, needs an X
        /// display - xvfb in CI) provides the surface, Waylith's own GraphicsDevice does the rest, and
        /// GpuTextureService is configured exactly the way EngineApplication.ConfigureGpuServices
        /// does it. Only the pieces the blit path touches are configured.
        /// </summary>
        static bool BringUpDevice()
        {
            var glfw = Engine.T("Brutal.GlfwApi.Glfw") ?? Engine.TSimple("Glfw");
            var hint = Engine.TSimple("GlfwWindowHint");
            var winType = Engine.TSimple("GlfwWindow");
            var gd = Engine.T("Waylith.Graphics.GraphicsDevice");
            if (glfw == null || winType == null || gd == null || _gpuSvc == null)
            {
                P.Skip("device", "types missing (glfw=" + (glfw != null) + " window=" + (winType != null)
                                 + " graphicsDevice=" + (gd != null) + " gpuService=" + (_gpuSvc != null) + ")");
                return false;
            }
            P.Gpu("glfw type = " + glfw.FullName);
            P.Gpu("glfw methods: " + string.Join(" ",
                glfw.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Where(x => x.Name.Contains("Create") || x.Name.Contains("WindowHint") || x.Name == "Init")
                    .Select(x => x.Name + "/" + x.GetParameters().Length).Distinct().OrderBy(x => x)));

            object win = null, inst = null, device = null;

            if (!Step("Glfw.Init", () => R.Invoke(R.M(glfw, "Init", 0), null) is bool b && b)) return false;
            P.Gpu("Glfw.Init() = true");

            object visible = hint != null ? Enum.Parse(hint, "Visible", ignoreCase: true) : (object)0;
            if (!Step("Glfw.WindowHint(Visible,0)",
                      () => { R.Invoke(R.MExact(glfw, "WindowHint", "GlfwWindowHint", "Int32") ?? R.M(glfw, "WindowHint", 2),
                                     null, new object[] { visible, 0 }); return true; })) return false;

            if (!Step("Glfw.CreateWindow", () =>
                {
                    var m = glfw.GetMethods(BindingFlags.Public | BindingFlags.Static)
                                .First(x => x.Name == "CreateWindow" && x.GetParameters().Length == 1);
                    var pt = m.GetParameters()[0].ParameterType;
                    if (pt.IsByRef) pt = pt.GetElementType();   // `in CreateInfo` shows up as CreateInfo&
                    win = R.Invoke(m, null, new object[] { WinCreateInfo(pt) });
                    return win != null;
                })) return false;
            P.Gpu("hidden GLFW window created");

            if (!Step("GraphicsDevice.CreateInstance", () => { inst = R.Invoke(R.M(gd, "CreateInstance", 3), null,
                                                                              new object[] { "AtlasFix", "Waylith", false }); return inst != null; })) return false;
            P.Gpu("Vulkan instance created");

            ulong surface = 0;
            if (!Step("glfwCreateWindowSurface", () =>
                {
                    var m = glfw.GetMethods(BindingFlags.Public | BindingFlags.Static)
                                .First(x => x.Name == "CreateWindowSurface" && x.GetParameters().Length == 3);
                    object[] a = { R.Get(inst, "VkHandle"), win, null };
                    R.Invoke(m, null, a);
                    surface = a[2] == null ? 0UL : Convert.ToUInt64(a[2], CultureInfo.InvariantCulture);
                    return surface != 0;
                })) return false;
            P.Gpu("window surface = 0x" + surface.ToString("x"));

            if (!Step("GraphicsDevice.Create", () =>
                {
                    var vkSurf = Engine.T("Brutal.VulkanApi.VkSurfaceKHR") ?? Engine.TSimple("VkSurfaceKHR");
                    var gpuPref = Engine.T("Waylith.Graphics.GpuPreference") ?? Engine.TSimple("GpuPreference");
                    var ctor = vkSurf.GetConstructors().First(c => c.GetParameters().Length == 1);
                    object surfaceObj = ctor.Invoke(new object[] { R.Coerce(ctor.GetParameters()[0].ParameterType, surface) });
                    device = R.Invoke(R.M(gd, "Create", 6), null, new object[]
                    {
                        inst, surfaceObj, 2, gpuPref != null ? Activator.CreateInstance(gpuPref) : null, false, false
                    });
                    return device != null;
                })) return false;
            P.Gpu("GraphicsDevice created");

            if (!Step("GpuTextureService.Configure", () =>
                { R.Invoke(R.M(_gpuSvc, "Configure", 2) ?? R.M(_gpuSvc, "Configure"), null, new object[] { device, 2 }); return true; })) return false;
            P.Gpu("GpuTextureService.Configure(device, 2) done");

            foreach (var name in new[] { "GpuMemoryTracker", "GpuMemoryReport", "RenderTexture", "TextureBlockEncoder", "ComputeKernelCache" })
            {
                Step(name + ".Configure", () =>
                {
                    var t = Engine.T("Waylith.Rendering." + name) ?? Engine.TSimple(name);
                    var m = t == null ? null : t.GetMethods(BindingFlags.Public | BindingFlags.Static)
                                              .FirstOrDefault(x => x.Name == "Configure");
                    if (m == null) return true;
                    R.Invoke(m, null, m.GetParameters().Length == 2 ? new object[] { device, 2 } : new object[] { device });
                    P.Gpu("  " + name + ".Configure ok");
                    return true;
                });
            }
            return true;
        }

        /// <summary>One device-bring-up step; a failure names the step and its exception chain.</summary>
        static bool Step(string name, Func<bool> body)
        {
            try
            {
                bool ok = body();
                if (!ok) P.Skip("device step " + name, "returned false");
                return ok;
            }
            catch (Exception e)
            {
                P.Skip("device step " + name, Ex(e));
                return false;
            }
        }

        /// <summary>GlfwWindow.CreateInfo { required string Title; required int2 Size; Monitor; Share }.</summary>
        static object WinCreateInfo(Type infoType)
        {
            object info = Activator.CreateInstance(infoType);
            var title = infoType.GetFields(BindingFlags.Public | BindingFlags.Instance).First(f => f.Name == "Title");
            title.SetValue(info, "atlasfix");
            var size = infoType.GetFields(BindingFlags.Public | BindingFlags.Instance).First(f => f.Name == "Size");
            var int2 = size.FieldType;
            var ctor = int2.GetConstructors().First(c => c.GetParameters().Length == 2);
            size.SetValue(info, ctor.Invoke(new object[] { 64, 64 }));
            return info;
        }

        // ------------------------------------------------------------------ misc

        /// <summary>
        /// No-device positive control. The shipped Bake still runs unchanged. This harness postfix
        /// runs first (Priority.First), after the page has been created and before GlyphFix's Low
        /// postfix: it gives the atlas page and live source a sentinel GpuResource, while the dead
        /// source stays non-resident. GlyphFix must therefore keep live and drop only dead.
        /// </summary>
        static bool InstallSentinelPostfix()
        {
            if (_sentinelInstalled) return true;
            try
            {
                if (Engine.Harmony == null)
                {
                    P.Note("sentinel postfix: 0Harmony not loaded");
                    return false;
                }

                var bake = _atlas.GetMethod("Bake", BindingFlags.Public | BindingFlags.Instance,
                                            null, new[] { typeof(bool) }, null);
                var htype = Engine.Harmony.GetType("HarmonyLib.Harmony");
                var hmType = Engine.Harmony.GetType("HarmonyLib.HarmonyMethod");
                var prioType = Engine.Harmony.GetType("HarmonyLib.Priority");
                if (bake == null || htype == null || hmType == null || prioType == null) return false;

                object h = Activator.CreateInstance(htype, new object[] { "atlasfix.harness.sentinel" });
                object hm = Activator.CreateInstance(hmType,
                    new object[] { typeof(Program).GetMethod(nameof(AfterBakeSentinel),
                        BindingFlags.Static | BindingFlags.NonPublic) });
                R.Set(hm, "priority", Enum.Parse(prioType, "First"));

                var patch = htype.GetMethods().First(x =>
                {
                    if (x.Name != "Patch") return false;
                    var ps = x.GetParameters();
                    return ps.Length >= 2 && typeof(MethodBase).IsAssignableFrom(ps[0].ParameterType)
                           && ps.Any(p => p.Name == "postfix");
                });
                var parameters = patch.GetParameters();
                var args = new object[parameters.Length];
                args[0] = bake;
                for (int i = 1; i < parameters.Length; i++)
                    if (parameters[i].Name == "postfix") args[i] = hm;
                patch.Invoke(h, args);
                _sentinelInstalled = true;
                P.Fact("harness instrumentation: no-device sentinel postfix is Priority.First");
                return true;
            }
            catch (Exception e)
            {
                P.Note("sentinel postfix install: " + Ex(e));
                return false;
            }
        }

        static void AfterBakeSentinel(object __instance)
        {
            if (_facts == null) return;
            try
            {
                object page = R.Get(__instance, "ColorTexture");
                _sentinelPageResident = ForceResident(page, "atlas page");
                _facts.LiveResident = ForceResident(_facts.Live, "live source");
                _facts.DeadResident = Resident(_facts.Dead);
                P.Real("sentinel after Bake: page=" + _sentinelPageResident
                       + " live=" + _facts.LiveResident + " dead=" + _facts.DeadResident);
            }
            catch (Exception e) { P.Note("sentinel after Bake: " + Ex(e)); }
        }

        static bool ForceResident(object texture, string label)
        {
            try
            {
                object core = R.Get(texture, "CoreTexture");
                if (core == null) return false;
                var member = core.GetType().GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                                  .FirstOrDefault(p => p.Name == "GpuResource" && p.GetIndexParameters().Length == 0);
                Type resourceType = member?.PropertyType;
                if (resourceType == null)
                {
                    var field = core.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                                       .FirstOrDefault(f => f.Name == "GpuResource");
                    resourceType = field?.FieldType;
                }
                if (resourceType == null) return false;

                object sentinel = resourceType.IsValueType
                    ? Activator.CreateInstance(resourceType)
                    : RuntimeHelpers.GetUninitializedObject(resourceType);
                R.Set(core, "GpuResource", sentinel);
                bool ok = Resident(texture);
                P.Real("sentinel " + label + ": GpuResource=present EnsureGpuResident=" + ok);
                return ok;
            }
            catch (Exception e)
            {
                P.Note("sentinel " + label + ": " + Ex(e));
                return false;
            }
        }

        /// <summary>
        /// Harness instrumentation, not part of the fix: BlitTexturesToColorAtlas ends with
        /// GenTexture.GenerateMipmaps (a compute pass over the finished page). It runs AFTER
        /// CopyTexture and after every blit, so it cannot change mip0 or the tile bookkeeping this
        /// probe measures - and a compute kernel is exactly the kind of thing a bare device without
        /// the engine's shader/render context cannot run. It is no-op'd here so that a failure in
        /// that unrelated layer cannot be mistaken for a bake failure.
        /// </summary>
        static void InstallMipNoOp()
        {
            try
            {
                var gen = Engine.T("Verse.GenTexture") ?? Engine.TSimple("GenTexture");
                var m = gen == null ? null : R.M(gen, "GenerateMipmaps", 4);
                if (m == null || Engine.Harmony == null)
                {
                    P.Note("mip no-op not installed (genTexture=" + (gen != null) + " method=" + (m != null)
                           + " harmony=" + (Engine.Harmony != null) + ")");
                    return;
                }
                var htype = Engine.Harmony.GetType("HarmonyLib.Harmony");
                var hmType = Engine.Harmony.GetType("HarmonyLib.HarmonyMethod");
                object h = Activator.CreateInstance(htype, new object[] { "atlasfix.harness.mipnoop" });
                object hm = Activator.CreateInstance(hmType, new object[] { typeof(Program).GetMethod(nameof(SkipMip), BindingFlags.Static | BindingFlags.NonPublic) });
                var patch = htype.GetMethods().First(x => x.Name == "Patch" && x.GetParameters().Length >= 2
                                                       && typeof(MethodBase).IsAssignableFrom(x.GetParameters()[0].ParameterType));
                var ps = patch.GetParameters();
                var args = new object[ps.Length];
                args[0] = m;
                for (int i = 1; i < ps.Length; i++) args[i] = i == 1 ? hm : null;
                patch.Invoke(h, args);
                P.Fact("harness instrumentation: Verse.GenTexture.GenerateMipmaps is no-op'd for this run"
                       + " (compute pass over the page, runs after CopyTexture; mip0 and tiles are unaffected)");
            }
            catch (Exception e)
            {
                P.Note("mip no-op failed: " + Ex(e));
            }
        }

        static bool SkipMip() => false;

        static object EnumOf(Type t, params string[] names)
        {
            foreach (var n in names)
            {
                try { return Enum.Parse(t, n, ignoreCase: true); } catch { }
            }
            throw new ArgumentException("none of [" + string.Join(",", names) + "] exist on " + t.FullName);
        }

        internal static string Ex(Exception e)
        {
            var sb = new System.Text.StringBuilder();
            for (Exception x = e; x != null; x = x.InnerException)
            {
                if (sb.Length > 0) sb.Append(" <- ");
                sb.Append(x.GetType().Name).Append(": ").Append(x.Message);
            }
            return sb.ToString();
        }

        static string ModuleState(bool patched)
        {
            if (!patched) return "(not installed)";
            try
            {
                var t = _txAtlasType;
                var m = t?.GetMethod("Summary", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
                return m?.Invoke(null, null) as string ?? "(no Summary)";
            }
            catch (Exception e) { return "(Summary threw " + e.GetType().Name + ")"; }
        }

        static Type _txAtlasType;

        static int Num(string text, string key)
        {
            if (string.IsNullOrEmpty(text)) return -1;
            int i = text.IndexOf(key + "=", StringComparison.Ordinal);
            if (i < 0) return -1;
            int j = i + key.Length + 1, k = j;
            while (k < text.Length && char.IsDigit(text[k])) k++;
            return k > j && int.TryParse(text.Substring(j, k - j), out int v) ? v : -1;
        }

        static string Argv(string[] argv, string name)
        {
            for (int i = 0; i < argv.Length - 1; i++) if (argv[i] == name) return argv[i + 1];
            return null;
        }

        static string Md5(string path)
        {
            using var md5 = System.Security.Cryptography.MD5.Create();
            using var fs = File.OpenRead(path);
            return Convert.ToHexString(md5.ComputeHash(fs)).ToLowerInvariant();
        }
    }
}
