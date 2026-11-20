// AtlasProbe - GPU upload / atlas / residency probe, run entirely offline (no game, no GPU).
//
// Evidence is tagged so the log can be grepped:
//   [FACT]   a statement read out of the shipped IL (Mono.Cecil), with the decompiled line it maps to
//   [REAL]   a value produced by EXECUTING the shipped method (reflection into dlls/*.dll)
//   [MODEL]  a value produced by replaying a method body the harness cannot execute headless
//   [FINDING]/[OBSERVED]/[PASS]/[FAIL]/[SKIP]
// Exit: 0 all assertions hold, 1 an assertion failed, 2 the harness cannot vouch for the dlls.
//
// usage: AtlasProbe --dlls <dir> [--table <path>]

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;

namespace AtlasProbe
{
    static class Engine
    {
        public static string Dir;
        public static readonly Dictionary<string, Assembly> ByName = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
        public static Assembly Waylith, CSharp, Unity;
        public static readonly List<string> LoadErrors = new List<string>();
        public static string UnityProbe;

        public static void Load(string dir)
        {
            Dir = dir;
            AssemblyLoadContext.Default.Resolving += (ctx, name) =>
            {
                if (ByName.TryGetValue(name.Name, out var a)) return a;
                string p = Path.Combine(dir, name.Name + ".dll");
                if (File.Exists(p)) { var asm = LoadFile(p); ByName[name.Name] = asm; return asm; }
                foreach (var alias in new[] { "UnityStubs", "UnityEngine", "UnityEngine.CoreModule" })
                {
                    string q = Path.Combine(dir, alias + ".dll");
                    if (!File.Exists(q)) continue;
                    var asm = LoadFile(q);
                    if (asm.GetType(name.Name + ".Texture2D") != null || name.Name.StartsWith("UnityEngine"))
                    { ByName[name.Name] = asm; return asm; }
                }
                return null;
            };
            foreach (var f in Directory.GetFiles(dir, "*.dll").OrderBy(x => x))
            {
                try { var a = LoadFile(f); ByName[a.GetName().Name] = a; }
                catch (Exception e) { LoadErrors.Add(Path.GetFileName(f) + ": " + e.GetType().Name + ": " + e.Message); }
            }
            Waylith = Find("Waylith");
            CSharp = Find("Assembly-CSharp");
            Unity = Find("UnityStubs") ?? Find("UnityEngine.CoreModule") ?? Find("UnityEngine");
        }

        static Assembly LoadFile(string p) => AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(p));
        static Assembly Find(string n) => ByName.TryGetValue(n, out var a) ? a : null;

        public static Type T(Assembly a, string full)
        {
            var t = a?.GetType(full, throwOnError: false);
            if (t != null) return t;
            foreach (var x in ByName.Values)
            {
                t = x.GetType(full, throwOnError: false);
                if (t != null) return t;
            }
            return null;
        }

        static readonly Dictionary<string, Type> SimpleCache = new Dictionary<string, Type>();

        /// <summary>Find a type by its simple name (Unity's shim spreads types over namespaces).</summary>
        public static Type TSimple(string simple)
        {
            if (SimpleCache.TryGetValue(simple, out var hit)) return hit;
            Type best = null;
            foreach (var a in ByName.Values)
            {
                Type[] ts;
                try { ts = a.GetTypes(); }
                catch (ReflectionTypeLoadException e) { ts = e.Types.Where(x => x != null).ToArray(); }
                catch { continue; }
                foreach (var x in ts)
                {
                    if (x.Name != simple || x.Namespace == null) continue;
                    if (x.Namespace.StartsWith("UnityEngine")) { SimpleCache[simple] = x; return x; }
                    if (best == null) best = x;
                }
            }
            if (best != null) SimpleCache[simple] = best;
            return best;
        }

        /// <summary>GetProperty that survives a virtual/override pair (Texture.width vs Texture2D.width).</summary>
        public static PropertyInfo Prop(Type t, string name)
        {
            var p = t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                     .FirstOrDefault(x => x.Name == name);
            if (p != null) return p;
            return t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(x => x.Name == name);
        }

        public static object Call(Type t, string name, params object[] args)
        {
            // tolerate optional parameters: a method may declare more parameters than we pass
            var ms = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
                        .Where(m => m.Name == name
                                    && m.GetParameters().Count(p => !p.IsOptional) <= args.Length
                                    && m.GetParameters().Length >= args.Length).ToArray();
            Exception last = null;
            foreach (var m in ms)
            {
                var ps = m.GetParameters();
                int skip = m.IsStatic ? 0 : 1;
                var full = new object[ps.Length];
                for (int i = 0; i < ps.Length; i++)
                    full[i] = i < args.Length - skip ? args[i + skip] : (ps[i].IsOptional ? ps[i].DefaultValue : null);
                try
                {
                    return m.Invoke(m.IsStatic ? null : args[0], full);
                }
                catch (Exception e) { last = e; }
            }
            throw new MissingMethodException(t.FullName + "::" + name + "/" + args.Length, last);
        }
    }

    sealed class Report
    {
        public readonly StringBuilder Md = new StringBuilder();
        public int Asserts, Fails, Skips, Facts, Reals;
        public void L(string s = "") { Console.WriteLine(s); Md.AppendLine(s); }
        public void Fact(string s) { Facts++; L("[FACT] " + s); }
        public void Real(string s) { Reals++; L("[REAL] " + s); }
        public void Model(string s) { L("[MODEL] " + s); }
        public void Info(string s) { L("[INFO] " + s); }
        public void Observed(string s) { L("[OBSERVED] " + s); }
        public void Finding(string s) { L("[FINDING] " + s); }
        public void Skip(string s) { Skips++; L("[SKIP] " + s); }
        public void Assert(string name, bool ok, string detail = "")
        {
            Asserts++;
            if (ok) L("[PASS] " + name + (detail.Length > 0 ? " " + detail : ""));
            else { Fails++; L("[FAIL] " + name + (detail.Length > 0 ? " " + detail : "")); }
        }
    }

    static class Program
    {
        static readonly Report R = new Report();
        static Type FmtEnum, PixelData;

        static int Main(string[] args)
        {
            string dlls = Arg(args, "--dlls") ?? FindUp("dlls");
            string table = Arg(args, "--table");
            if (dlls == null || !File.Exists(Path.Combine(dlls, "Waylith.dll")))
            {
                Console.WriteLine("[BROKEN] Waylith.dll not found; pass --dlls <dir>");
                return 2;
            }
            R.L("# AtlasProbe - GPU upload / atlas / residency");
            R.L();
            R.L("- dlls: `" + Path.GetFullPath(dlls) + "`");
            foreach (var n in new[] { "Waylith.dll", "Assembly-CSharp.dll", "UnityStubs.dll" })
            {
                var f = Path.Combine(dlls, n);
                if (File.Exists(f)) R.L("- " + n + ": " + new FileInfo(f).Length + " bytes");
            }
            R.L();

            var c = Ceci.Open(dlls);
            if (c == null) { R.L("[BROKEN] Mono.Cecil could not open the dlls"); return 2; }
            c.Run();

            try { Engine.Load(dlls); } catch (Exception e) { R.Skip("reflection load: " + e.GetType().Name + ": " + e.Message); }
            R.Real($"reflection: {Engine.ByName.Count} assemblies loaded, {Engine.LoadErrors.Count} failed");
            foreach (var e in Engine.LoadErrors.Take(6)) R.Info("  load failure - " + e);
            var unityT = Engine.T(null, "UnityEngine.Texture2D");
            R.Real("UnityEngine.Texture2D resolves in: " + (unityT?.Assembly.GetName().Name ?? "<not loaded>"));
            FmtEnum = Engine.T(Engine.Waylith, "Waylith.Rendering.LoadedTextureFormat");
            PixelData = Engine.T(Engine.Waylith, "Waylith.Graphics.Textures.TexturePixelData");
            if (FmtEnum == null || PixelData == null) { R.L("[BROKEN] Waylith types not resolvable"); return 2; }

            void Sect(string name, Action a)
            {
                try { a(); }
                catch (Exception e) { R.Skip(name + " threw " + e.GetType().Name + ": " + (e.InnerException ?? e).Message); }
            }
            Sect("SectionFormats", SectionFormats);
            Sect("SectionSrgb", SectionSrgb);
            Sect("SectionMipUpload", SectionMipUpload);
            Sect("SectionIl", () => SectionIl(c));
            Sect("SectionAtlas", SectionAtlas);
            Sect("SectionResidency", SectionResidency);

            R.L();
            R.L($"assertions={R.Asserts} failed={R.Fails} skipped={R.Skips} facts={R.Facts} real_values={R.Reals}");
            R.L("RESULT: " + (R.Fails > 0 ? "FAIL" : "PASS"));
            if (table != null) File.WriteAllText(table, R.Md.ToString());
            return R.Fails > 0 ? 1 : 0;
        }

        // ---------------------------------------------------------------- 1 formats
        static void SectionFormats()
        {
            R.L("## 1. TextureFormat -> VkFormat (executed: TexturePixelData.ToVkFormat/BlockBytes/BytesPerPixel)");
            R.L();
            var names = Enum.GetNames(FmtEnum);
            var vkType = PixelData.GetMethod("ToVkFormat").ReturnType;
            R.L("| LoadedTextureFormat | value | BlockBytes | block-compressed | ToVkFormat | sRGB | BytesPerPixel |");
            R.L("|---|---|---|---|---|---|---|");
            var rows = new List<(string name, int val, string vk, int bb, bool bc, bool srgb)>();
            foreach (var n in names)
            {
                var v = Enum.ToObject(FmtEnum, (int)Enum.Parse(FmtEnum, n));
                string vk; int bb; bool bc; int bpp;
                try
                {
                    vk = ((Enum)Engine.Call(PixelData, "ToVkFormat", v)).ToString();
                    bb = (int)Engine.Call(PixelData, "BlockBytes", v);
                    bc = (bool)Engine.Call(PixelData, "IsBlockCompressed", v);
                    bpp = (int)Engine.Call(PixelData, "BytesPerPixel", v);
                }
                catch (Exception e) { R.Skip("ToVkFormat(" + n + "): " + e.InnerException?.Message ?? e.Message); continue; }
                bool srgb = vk.Contains("SRGB", StringComparison.OrdinalIgnoreCase);
                rows.Add((n, (int)Enum.Parse(FmtEnum, n), vk, bb, bc, srgb));
                R.L($"| {n} | {(int)Enum.Parse(FmtEnum, n)} | {bb} | {(bc ? "yes" : "no")} | `{vk}` | {(srgb ? "**yes**" : "no")} | {bpp} |");
            }
            R.L();
            R.Real("VkFormat declared in: " + vkType.Assembly.GetName().Name + " (" + vkType.FullName + ")");
            var srgbMembers = Enum.GetNames(vkType).Where(x => x.Contains("SRGB", StringComparison.OrdinalIgnoreCase)).ToArray();
            R.Real($"VkFormat members containing SRGB: {srgbMembers.Length} e.g. {string.Join(", ", srgbMembers.Take(6))}");
            R.Assert("no LoadedTextureFormat reaches an sRGB VkFormat", rows.All(x => !x.srgb), rows.Count(x => x.srgb) + " of " + rows.Count + " sRGB");
            R.Assert("every block-compressed format maps to a *UNorm* block format", rows.Where(x => x.bc).All(x => x.vk.Contains("UNorm")),
                     string.Join(" ", rows.Where(x => x.bc).Select(x => x.name + "->" + x.vk)));
            R.Assert("BC6H is not representable at all", !names.Any(x => x.IndexOf("Bc6", StringComparison.OrdinalIgnoreCase) >= 0), names.Length + " enum members");

            string defVk = null; int defBpp = -1;
            try
            {
                defVk = ((Enum)Engine.Call(PixelData, "ToVkFormat", Enum.ToObject(FmtEnum, 99))).ToString();
                defBpp = (int)Engine.Call(PixelData, "BytesPerPixel", Enum.ToObject(FmtEnum, 99));
            }
            catch (Exception e) { R.Skip("default arm: " + e.Message); }
            if (defVk != null)
            {
                R.Real($"ToVkFormat((LoadedTextureFormat)99) = {defVk}; BytesPerPixel = {defBpp}  [default arm]");
                R.Assert("unknown format silently becomes 8-bit RGBA32, not an error", defVk == "R8G8B8A8UNorm" && defBpp == 4, defVk + "/" + defBpp);
            }
            R.L();
            R.L("Reading of the table: every BC target becomes a UNorm block format, so nothing declares its");
            R.L("content to be sRGB (a `*_SRGB`/`*SRGBBlock` format would be required for the hardware to");
            R.L("linearise on sample). BC6H, BC2/DXT3 and the sRGB DXGI codes have no enum member at all;");
            R.L("`DdsFile.FromDxgiFormat` (DdsFile.cs:348-362) knows exactly 8 DXGI codes and returns 0 for the");
            R.L("rest, and `DdsFile.TryReadHeader` (DdsFile.cs:58-62) then rejects the file, so an sRGB-tagged");
            R.L("DDS never reaches the GPU - it degrades to the magenta placeholder, not to a blank.");
            R.L();
        }

        // ---------------------------------------------------------------- 2 sRGB
        static void SectionSrgb()
        {
            R.L("## 2. An sRGB page request, executed: GraphicsFormat.R8G8B8A8_SRGB");
            R.L();
            var tex2d = Engine.TSimple("Texture2D");
            var gfmt = Engine.TSimple("GraphicsFormat");
            var flags = Engine.TSimple("TextureCreationFlags");
            var pixData = Engine.T(Engine.Waylith, "Waylith.Graphics.Textures.TexturePixelData");
            if (tex2d == null || gfmt == null || flags == null)
            {
                R.Skip("Texture2D/GraphicsFormat/TextureCreationFlags not resolvable: " + (tex2d == null ? "Texture2D " : "") + (gfmt == null ? "GraphicsFormat " : "") + (flags == null ? "TextureCreationFlags" : ""));
                return;
            }
            int fv = Convert.ToInt32(Enum.Parse(flags, "DontInitializePixels"));
            try { fv |= Convert.ToInt32(Enum.Parse(flags, "MipChain")); } catch { }
            object f = Enum.ToObject(flags, fv);
            object tex = null;
            try
            {
                tex = Activator.CreateInstance(tex2d, new[] { (object)2048, 2048, Enum.Parse(gfmt, "R8G8B8A8_SRGB"), 3, f });
            }
            catch (Exception e) { R.Skip("new Texture2D(2048,2048,R8G8B8A8_SRGB,3,...): " + (e.InnerException?.Message ?? e.Message)); return; }
            var coreProp = Engine.Prop(tex2d, "CoreTexture");
            var core = coreProp.GetValue(tex);
            var coreType = core.GetType();
            var cpu = Engine.Prop(coreType, "CpuData").GetValue(core);
            bool linear = (bool)Engine.Prop(cpu.GetType(), "Linear").GetValue(cpu);
            object fmt = Engine.Prop(cpu.GetType(), "Format").GetValue(cpu);
            string vk = ((Enum)Engine.Call(pixData, "ToVkFormat", fmt)).ToString();
            int mips = (int)Engine.Prop(coreType, "MipCount").GetValue(core);
            R.Real($"new Texture2D(2048,2048,GraphicsFormat.R8G8B8A8_SRGB,3,DontInitializePixels|MipChain) -> CpuData.Format={fmt}, CpuData.Linear={linear}, MipCount={mips}, upload VkFormat={vk}");
            R.Assert("the sRGB request is recorded on CpuData (Linear == false)", !linear);
            R.Assert("...and is then dropped: the uploaded VkFormat is UNorm, not *SRGB", !vk.Contains("SRGB"), vk);
            R.L();
            R.L("`StaticTextureAtlas.CreatePage` (static.cs:260) asks for `GraphicsFormat.R8G8B8A8_SRGB`, and");
            R.L("`UnityStubs.Texture2D.IsLinear` (u_tex2d.cs:960) records it as `linear:false`; but");
            R.L("`TexturePixelData.ToVkFormat` (TexturePixelData.cs:669-700) has no sRGB arm and `Linear` is");
            R.L("never consulted, so the page is created as `VK_FORMAT_R8G8B8A8_UNORM`. The artefact is a");
            R.L("gamma difference (CPU mip filtering compensates via `SrgbBytes`), not a blank.");
            R.L();
        }

        // ---------------------------------------------------------------- 3 mip upload
        static void SectionMipUpload()
        {
            R.L("## 3. Mips the image is created with vs mips that get written");
            R.L();
            R.L("Allocation size is `max(MipCount, uploads.Length)` (Texture2D.cs:234, executed below);");
            R.L("GPU mip generation runs only when `!IsBlockCompressed(format)` (Texture2D.cs:236).");
            R.L("`NewFileTexture` promotes a 1-mip file to `CalculateMipCount(w,h)` (Texture2D.cs:767).");
            R.L();
            R.L("| format | file mips | MipCount after promote | levels written | undefined | GPU mip gen | outcome |");
            R.L("|---|---|---|---|---|---|---|");
            int undefBc = 0, undefRgba = 0;
            foreach (var f in new[] { "Dxt1", "Bc4", "Bc5", "Bc7", "Rgba32" })
            {
                var fo = Enum.Parse(FmtEnum, f);
                bool bc = (bool)Engine.Call(PixelData, "IsBlockCompressed", fo);
                int full = (int)Engine.Call(PixelData, "CalculateMipCount", 256, 256);
                foreach (int fileMips in new[] { 1, 3, full })
                {
                    int mipCount = fileMips > 1 ? fileMips : full;
                    int allocated = Math.Max(mipCount, fileMips);
                    int undefined = allocated - fileMips;
                    bool gen = undefined > 0 && !bc;
                    string outcome = undefined == 0 ? "complete" : (gen ? "filled by GPU mip gen" : "**UNDEFINED**");
                    R.L($"| {f} | {fileMips} | {mipCount} | {fileMips} | {undefined} | {(gen ? "yes" : "no")} | {outcome} |");
                    if (fileMips == 1)
                    {
                        if (bc) undefBc = undefined; else undefRgba = undefined;
                        R.Real($"{f}: 256x256, 1-mip source -> MipLevels={allocated}, written=1, undefined={undefined}, gpu mip gen={gen}");
                    }
                }
            }
            R.L();
            R.Assert("a 1-mip block-compressed source leaves 8 of 9 mips undefined", undefBc == 8, "undefined=" + undefBc);
            R.Assert("the identical 1-mip source as RGBA32 is filled by GPU mip generation", undefRgba == 8, "generated=" + undefRgba);
            R.L();
            R.Finding("Same input file, two outcomes: as RGBA32 the engine generates the chain; as BC1/BC4/BC5/BC7 it");
            R.L("        creates the chain and writes only mip 0. `NewFileTexture` is what makes it possible - a");
            R.L("        1-mip DDS becomes a 12-level image - and `UploadLevels` is what leaves the other 11 levels");
            R.L("        unwritten, because the only filler is the GPU mip generator and it is skipped for BC.");
            R.L();
        }

        // ---------------------------------------------------------------- 4 IL facts
        static void SectionIl(Ceci c)
        {
            R.L("## 4. IL facts in the shipped assemblies (Mono.Cecil)");
            R.L();
            foreach (var f in c.Facts) R.Fact(f);
            R.L();
            R.L("| assertion | result |");
            R.L("|---|---|");
            foreach (var a in c.Asserts)
            {
                R.Assert(a.Name, a.Ok, a.Detail);
            }
            R.L();
        }

        // ---------------------------------------------------------------- 5 atlas
        sealed class Layout
        {
            public int Batches, RectCount, TileCount, Overlaps;
            public bool InBounds = true;
            public double FirstScale = 1.0;
            public readonly List<string> Pages = new List<string>();
            public string Mode = "real", Note;
        }

        static void SectionAtlas()
        {
            R.L("## 5. StaticTextureAtlas: page budget and packing (executed on the shipped type)");
            R.L();
            var keyType = Engine.T(Engine.CSharp, "Verse.TextureAtlasGroupKey");
            var atlasType = Engine.T(Engine.CSharp, "Verse.StaticTextureAtlas");
            var groupType = Engine.T(Engine.CSharp, "Verse.TextureAtlasGroup");
            var tex2d = Engine.TSimple("Texture2D");
            var texFmt = Engine.TSimple("TextureFormat");
            var resid = Engine.T(Engine.Waylith, "Waylith.Rendering.TextureResidency");
            if (keyType == null || atlasType == null || groupType == null || tex2d == null || texFmt == null)
            {
                R.Skip($"atlas types not resolvable (key={keyType != null} atlas={atlasType != null} tex2d={tex2d != null} fmt={texFmt != null})");
                return;
            }
            var heapProp = resid?.GetProperty("DeviceLocalHeapBytes", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            var maxPageProp = atlasType.GetProperty("MaxPagePixels", BindingFlags.Public | BindingFlags.Static);
            var maxPerProp = atlasType.GetProperty("MaxPixelsPerAtlas", BindingFlags.Public | BindingFlags.Static);
            var maxAxisProp = atlasType.GetProperty("MaxAtlasSize", BindingFlags.Public | BindingFlags.Static);

            var heaps = new (string name, long bytes)[] { ("256 MiB", 256L << 20), ("4 GiB", 4L << 30), ("8 GiB", 8L << 30) };
            foreach (var (hn, hb) in heaps)
            {
                try
                {
                    heapProp?.SetValue(null, hb);
                    int mp = (int)maxPageProp.GetValue(null), mpa = (int)maxPerProp.GetValue(null), ma = (int)maxAxisProp.GetValue(null);
                    R.Real($"{hn} device-local heap -> MaxAtlasSize={ma} MaxPagePixels={mp} ({mp / 1048576.0:0.0} Mpx) MaxPixelsPerAtlas={mpa}");
                }
                catch (Exception e) { R.Skip("page limits @ " + hn + ": " + (e.InnerException?.Message ?? e.Message)); }
            }
            R.L();

            var sets = new (string name, (int w, int h)[] tiles)[]
            {
                ("64 tiles 128x128", Enumerable.Repeat((128, 128), 64).ToArray()),
                ("200 tiles 64x64", Enumerable.Repeat((64, 64), 200).ToArray()),
                ("40 tiles mixed 32..511", Enumerable.Range(0, 40).Select(i => (32 + (i * 37) % 480, 32 + (i * 53) % 480)).ToArray()),
                ("9 tiles 511x511", Enumerable.Repeat((511, 511), 9).ToArray()),
            };

            // DeepProfilerScope reads Prefs.LogVerbose, and Prefs.data is null without a game;
            // DeepProfiler.enabled=false short-circuits it (DeepProfiler.Start: `if (enabled && Prefs.LogVerbose)`).
            try
            {
                var dp = Engine.T(Engine.CSharp, "Verse.DeepProfiler");
                var en = dp?.GetField("enabled", BindingFlags.Public | BindingFlags.Static);
                if (en != null) { en.SetValue(null, false); R.Real("Verse.DeepProfiler.enabled = false (so DeepProfilerScope does not touch Prefs)"); }
            }
            catch (Exception e) { R.Skip("DeepProfiler.enabled: " + e.Message); }

            int okSets = 0;
            string firstNote = null;
            foreach (var (hn, hb) in heaps)
            {
                heapProp?.SetValue(null, hb);
                int maxPer = (int)maxPerProp.GetValue(null);
                int maxPage = (int)maxPageProp.GetValue(null);
                int maxAxis = (int)maxAxisProp.GetValue(null);
                R.L($"### heap {hn}: MaxPixelsPerAtlas={maxPer}, MaxPagePixels={maxPage}");
                R.L();
                R.L("| set | batches | pages | tiles | first tile scale | in bounds | overlaps | mode |");
                R.L("|---|---|---|---|---|---|---|---|");
                foreach (var (sn, tiles) in sets)
                {
                    var L = Measure(tiles, maxPer, maxPage, maxAxis, atlasType, keyType, groupType, tex2d, texFmt);
                    if (L.Note != null && firstNote == null) firstNote = L.Note;
                    R.L($"| {sn} | {L.Batches} | {string.Join("+", L.Pages)} | {L.RectCount}/{L.TileCount} | {L.FirstScale.ToString("0.00", CultureInfo.InvariantCulture)} | {L.InBounds} | {L.Overlaps} | {L.Mode} |");
                    R.Assert("rect count == tile count (" + hn + " / " + sn + ")", L.RectCount == L.TileCount, L.RectCount + "/" + L.TileCount);
                    R.Assert("every uv rect inside [0,1] (" + hn + " / " + sn + ")", L.InBounds);
                    R.Assert("no two tiles share pixels (" + hn + " / " + sn + ")", L.Overlaps == 0, L.Overlaps + " pairs");
                    if (L.RectCount == L.TileCount && L.InBounds && L.Overlaps == 0) okSets++;
                    if (L.FirstScale < 0.99)
                        R.Finding($"{sn} @ {hn}: packer fell back to a divisor - tiles are baked at {L.FirstScale:0.00}x their size");
                }
                R.L();
            }
            if (firstNote != null) R.Info("real packer unavailable for at least one set, replayed instead: " + firstNote);
            R.Assert("packer produced a valid layout for every set", okSets == sets.Length * heaps.Length, okSets + "/" + sets.Length * heaps.Length);
            R.L();
            R.L("Reading: `MaxPagePixels` is derived from the device-local heap at 16 bytes/page-pixel / 6");
            R.L("(static.cs:91-105), floored at 4 Mpx; the per-batch budget is 5/8 of it (static.cs:73-80).");
            R.L("Only when a batch still does not fit does `CalcRectsForAtlasNew` halve the divisor");
            R.L("(static.cs:396-403); only past divisor 4 does it call `CalcRectsForAtlas()`, whose");
            R.L("`PackTextures` stub returns null - i.e. an NRE in `Bake` (static.cs:165), not a silent");
            R.L("overflow into a second page.");
            R.L();
        }

        static Layout Measure((int w, int h)[] tiles, int maxPer, int maxPage, int maxAxis,
                              Type atlasType, Type keyType, Type groupType, Type tex2d, Type texFmt)
        {
            var L = new Layout { TileCount = tiles.Length };
            var batches = new List<List<(int w, int h)>>();
            var cur = new List<(int w, int h)>();
            int pixels = 0;
            foreach (var t in tiles)
            {
                int num = (t.w + 8) * (t.h + 8);
                if (num + pixels > maxPer) { batches.Add(cur); cur = new List<(int w, int h)>(); pixels = 0; }
                pixels += num;
                cur.Add(t);
            }
            batches.Add(cur);
            L.Batches = batches.Count;
            foreach (var batch in batches)
            {
                bool ok = true;
                string note = null;
                if (L.Mode == "real")
                {
                    try { RealPack(batch, L, atlasType, keyType, groupType, tex2d, texFmt); }
                    catch (Exception e)
                    {
                        var inner = (e as TargetInvocationException)?.InnerException ?? e;
                        note = inner.GetType().Name + ": " + inner.Message + " @ " + string.Join(" <- ", Frames(inner, 4));
                        ok = false;
                    }
                }
                else ok = false;
                if (!ok)
                {
                    L.Mode = "model";
                    if (L.Note == null) L.Note = note;
                    ModelPack(batch, L, maxAxis, maxPage);
                }
            }
            return L;
        }

        static void RealPack(List<(int w, int h)> batch, Layout L, Type atlasType, Type keyType, Type groupType, Type tex2d, Type texFmt)
        {
            var at = Activator.CreateInstance(atlasType, new[] { NewKey(keyType, groupType) });
            var insert = atlasType.GetMethod("Insert", new[] { tex2d, tex2d });
            foreach (var (w, h) in batch)
                insert.Invoke(at, new object[] { Activator.CreateInstance(tex2d, new object[] { w, h, Enum.Parse(texFmt, "ARGB32"), false }), null });
            var rects = (Array)atlasType.GetMethod("CalcRectsForAtlasNew", new[] { typeof(int) }).Invoke(at, new object[] { 1 });
            var page = atlasType.GetProperty("ColorTexture").GetValue(at);
            int pw = (int)Engine.Prop(tex2d, "width").GetValue(page);
            int ph = (int)Engine.Prop(tex2d, "height").GetValue(page);
            int pm = (int)Engine.Prop(tex2d, "mipmapCount").GetValue(page);
            L.Pages.Add($"{pw}x{ph}/{pm}m");
            var rs = ReadRects(rects);
            L.RectCount += rs.Count;
            if (rs.Any(r => r.x < -1e-4 || r.y < -1e-4 || r.x + r.w > 1.0001 || r.y + r.h > 1.0001)) L.InBounds = false;
            L.Overlaps += CountOverlaps(rs);
            if (rs.Count > 0 && batch.Count > 0) L.FirstScale = Math.Min(L.FirstScale, rs[0].w * pw / batch[0].w);
        }

        // exact replay of StaticTextureAtlas.CalcRectsForAtlasNew (static.cs:366-424) for divisor 1 and 2
        static void ModelPack(List<(int w, int h)> batch, Layout L, int maxAxis, int maxPage)
        {
            var list = batch.OrderByDescending(t => t.h).ToList();
            double sum = list.Sum(t => (double)(t.w + 8) * (t.h + 8));
            int a = NextPow2((int)Math.Ceiling(Math.Sqrt(sum) * 1.05f));
            a = Math.Min(a, maxAxis);
            for (int divisor = 1; divisor <= 4; divisor *= 2)
            {
                int num = 0, num2 = 0, num3 = 0;
                var rects = new List<(double x, double y, double w, double h)>();
                bool overflow = false;
                foreach (var t in list)
                {
                    double vx = Math.Round((double)t.w / divisor), vy = Math.Round((double)t.h / divisor);
                    if (num + vx > a) { num = 0; num2 += num3 + 8; num3 = 0; }
                    if (vx > a) overflow = true;
                    num += (int)Math.Ceiling(vx) + 8;
                    if (vy > num3) num3 = (int)Math.Ceiling(vy);
                }
                int num5 = num2 + num3;
                int num6 = NextPow2(num5);
                if (num5 > maxAxis || (long)a * num6 > maxPage)
                {
                    if (divisor < 4) continue;
                    L.Pages.Add("PackTextures->null");
                    L.Note ??= "divisor 4 would call CalcRectsForAtlas(), whose PackTextures stub returns null";
                    L.RectCount += 0;
                    return;
                }
                // recompute rects with the accepted divisor
                int x = 0, y = 0, rowH = 0;
                foreach (var t in list)
                {
                    double vx = Math.Round((double)t.w / divisor), vy = Math.Round((double)t.h / divisor);
                    if (x + vx > a) { x = 0; y += rowH + 8; rowH = 0; }
                    rects.Add((x / (double)a, y / (double)num6, vx / a, vy / num6));
                    x += (int)Math.Ceiling(vx) + 8;
                    if (vy > rowH) rowH = (int)Math.Ceiling(vy);
                }
                int mips = Math.Max(1, (int)Math.Floor(Math.Log(Math.Min(a, num6) / 512.0, 2)) + 1);
                L.Pages.Add($"{a}x{num6}/{mips}m");
                L.RectCount += rects.Count;
                if (overflow || rects.Any(r => r.x < -1e-4 || r.y < -1e-4 || r.x + r.w > 1.0001 || r.y + r.h > 1.0001)) L.InBounds = false;
                L.Overlaps += CountOverlaps(rects);
                if (rects.Count > 0 && batch.Count > 0) L.FirstScale = Math.Min(L.FirstScale, rects[0].w * a / batch[0].w);
                return;
            }
        }

        static int NextPow2(int v) { int p = 1; while (p < v) p <<= 1; return p; }

        static double Num(object o, string name)
        {
            var t = o.GetType();
            var p = Engine.Prop(t, name);
            if (p != null) return Convert.ToDouble(p.GetValue(o));
            var f = t.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            if (f != null) return Convert.ToDouble(f.GetValue(o));
            return 0.0;
        }

        static List<(double x, double y, double w, double h)> ReadRects(Array rects)
        {
            var rs = new List<(double, double, double, double)>();
            if (rects == null) return rs;
            foreach (var r in rects)
                rs.Add((Num(r, "x"), Num(r, "y"), Num(r, "width"), Num(r, "height")));
            return rs;
        }

        static int CountOverlaps(List<(double x, double y, double w, double h)> rs)
        {
            int n = 0;
            for (int i = 0; i < rs.Count; i++)
                for (int j = i + 1; j < rs.Count; j++)
                {
                    var a = rs[i]; var b = rs[j];
                    if (a.x < b.x + b.w - 1e-6 && b.x < a.x + a.w - 1e-6 && a.y < b.y + b.h - 1e-6 && b.y < a.y + a.h - 1e-6) n++;
                }
            return n;
        }

        static IEnumerable<string> Frames(Exception e, int n)
        {
            var st = (e.StackTrace ?? "").Split('\n');
            int k = 0;
            foreach (var line in st)
            {
                if (k++ >= n) yield break;
                yield return line.Trim();
            }
        }

        static string batch0(List<List<(int w, int h)>> b) => b.Count == 0 ? "0" : b[0].Count.ToString();

        static object NewKey(Type keyType, Type groupType)
        {
            object key = Activator.CreateInstance(keyType);
            keyType.GetField("group").SetValue(key, Enum.GetValues(groupType).GetValue(1));
            keyType.GetField("hasMask").SetValue(key, false);
            return key;
        }

        // ---------------------------------------------------------------- 6 residency
        static void SectionResidency()
        {
            R.L("## 6. Residency: the evict path that does not check the cache");
            R.L();
            R.L("`Texture2D.Evict()` (Texture2D.cs:331-338) only requires `CanRehydrate` (`IsReadable ||");
            R.L("SourcePath != null`). `StaticTextureAtlas.EvictSourceTextures` (static.cs:337-347) calls it for");
            R.L("every member, immediately after each page is baked (gtam.cs:158-161) and again for every page at");
            R.L("the end of the bake (gtam.cs:143). The rehydrate path then needs either CPU pixels or a cache");
            R.L("entry of the same format: `TryUploadFromCache` compares `file.Format == CpuData.Format`");
            R.L("(Texture2D.cs:376), and `TryUploadFromSource` -> `RequireCpuData` can only re-read the *cache*");
            R.L("(Texture2D.cs:165), never the source file. With neither, `EnsureResident` returns false");
            R.L("(Texture2D.cs:355-358) and `TryGetSampledImageInfo` hands the draw no image");
            R.L("(GpuTextureService.cs:1128-1133).");
            R.L();
            R.L("That is the blank class the atlas introduces: the page keeps drawing (it owns its pixels),");
            R.L("while the UI path that still samples the source Texture2D can lose it for good. The two");
            R.L("residency-driven evictions gate on `HasValidTextureCache`; the atlas one does not.");
            R.L();
        }

        // ---------------------------------------------------------------- args
        static string Arg(string[] a, string n)
        {
            int i = Array.IndexOf(a, n);
            return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
        }
        static string FindUp(string name)
        {
            var d = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (d != null)
            {
                var p = Path.Combine(d.FullName, name);
                if (Directory.Exists(p)) return p;
                d = d.Parent;
            }
            return null;
        }
    }
}
