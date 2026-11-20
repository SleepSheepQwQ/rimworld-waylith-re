using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using RimWorld.IO;
using UnityEngine;
using Verse;

/// <summary>
/// Texture decode / cache probe. Runs inside the real headless host after BootCompleted, so the
/// engine is configured the way it is on device (WaylithRuntime.App.CacheDataPath, RenderLog).
///
/// Phases:
///   1  decode matrix        the two branches of ModContentLoader.LoadTexture, one line per fixture
///   2  cache staleness      same byte length + same mtime, different pixels
///   3  zero-length levels   a cache entry whose level 0 is 0 bytes: accepted or rejected?
///   4  BC1 upload format    LoadedTextureFormat.Dxt1 -> which VkFormat, and does the CPU decode agree
///
/// Every line is [TEXPROBE]; gate.py decides pass/fail from MECHANISM / assert / RESULT lines.
/// </summary>
internal static class StartupHook
{
    private const string T = "[TEXPROBE]";

    private static int _started;
    private static int _done;
    private static readonly List<string> Failed = new List<string>();
    private static readonly Dictionary<string, Fact> Facts = new Dictionary<string, Fact>(StringComparer.Ordinal);

    private static string _outDir;
    private static string _fx;

    private sealed class Fact
    {
        public string Tag, File, Error;
        public int Width, Height, Pixels, RawBytes, Alpha0, Distinct;
        public string Vk;
        public bool Placeholder;
        public List<Color32> Colours = new List<Color32>();
        public bool PixelsThrew;
        public string PixelError;
    }

    public static void Initialize()
    {
        new Thread(WaitForBoot) { IsBackground = true, Name = "texprobe-waiter" }.Start();
        new Thread(Watchdog) { IsBackground = true, Name = "texprobe-watchdog" }.Start();
    }

    private static void WaitForBoot()
    {
        while (Waylith.Runtime.WaylithRuntime.Current == null) Thread.Sleep(20);
        Waylith.Runtime.WaylithRuntime.Current.App.BootCompleted += Run;
        Console.Error.WriteLine($"{T} hook armed");
    }

    private static void Watchdog()
    {
        Thread.Sleep(TimeSpan.FromSeconds(300));
        if (Volatile.Read(ref _done) != 0) return;
        Console.Error.WriteLine($"{T} RESULT=FAIL failed=watchdog-timeout");
        Environment.Exit(4);
    }

    private static void Run()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        try
        {
            _outDir = Environment.GetEnvironmentVariable("SCENARIO_OUT");
            if (string.IsNullOrEmpty(_outDir)) _outDir = Directory.GetCurrentDirectory();
            Directory.CreateDirectory(_outDir);
            _fx = Environment.GetEnvironmentVariable("TEXPROBE_FIXTURES");
            if (string.IsNullOrEmpty(_fx)) _fx = Path.Combine(_outDir, "fixtures");

            Console.Error.WriteLine($"{T} boot=complete fixtures={_fx} exists={Directory.Exists(_fx)} " +
                                    $"cacheRoot={CacheRoot()} tmp={Path.GetTempPath()}");

            Phase1Decode();
            Phase2CacheStaleness();
            Phase3EmptyLevels();
            Phase4Formats();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{T} probe stack:\n{ex}");
            Failed.Add("probe-exception");
        }
        Console.Error.WriteLine($"{T} RESULT={(Failed.Count == 0 ? "OK" : "FAIL")}" +
                                (Failed.Count == 0 ? "" : " failed=" + string.Join(",", Failed)));
        Quit();
        // Root.Shutdown() ends the headless loop; if it does not, do not sit in ci/run.sh's
        // timeout for seven minutes. The gate reads the log, not this exit code.
        new Thread(() => { Thread.Sleep(8000); Environment.Exit(0); }) { IsBackground = true }.Start();
    }

    // ---------------------------------------------------------------- 1. decode matrix ---------
    private static void Phase1Decode()
    {
        Dump("ok.png", "ok.png", dds: false);
        Dump("inter.png", "inter.png", dds: false);
        Dump("pal.png", "pal.png", dds: false);
        Dump("g16.png", "g16.png", dds: false);
        Dump("gray.png", "gray.png", dds: false);
        Dump("trunc.png", "trunc.png", dds: false);
        Dump("dxt1_ok.dds", "dxt1_ok.dds", dds: true);
        Dump("dxt1_alpha.dds", "dxt1_alpha.dds", dds: true);
        Dump("dxt3.dds", "dxt3.dds", dds: true);
        Dump("bc5_dx10.dds", "bc5_dx10.dds", dds: true);
        Dump("mip4.dds", "mip4.dds", dds: true);
        Dump("short_mips.dds", "short_mips.dds", dds: true);

        // --- the control: the same pixels through the same path must survive ---------------
        Fact ok = Facts["ok.png"];
        bool control = ok.Error == null && ok.Width == 8 && ok.Height == 8 && ok.Distinct == 4;
        Check("control-png", control, ok.Summary());

        // --- M1: an interlaced PNG is refused by BOTH decoders ---------------------------
        // PngDecoder bails on the interlace byte (Waylith.Graphics.Textures/PngDecoder.cs:53),
        // TextureLoader is stb, and stb_image has no Adam7 support, so Texture2D.LoadFromFile
        // catches the throw and replaces the whole icon with a 2x2 magenta placeholder
        // (Waylith.Rendering/Texture2D.cs:749-763).
        Fact inter = Facts["inter.png"];
        bool m1 = inter.Width <= 2 && inter.Height <= 2 && inter.Distinct == 1 && inter.Placeholder;
        Mechanism("M1", "interlaced-png-to-2x2-magenta", m1,
                  $"ok.png=8x8x4colours inter.png={inter.Short()} pixel={Hex(inter.Colours.FirstOrDefault())} vk=n/a");
        Check("interlaced-not-8x8", m1, inter.Summary());

        // --- M5 (finding, not blank/wrong-colour): the DDS mip chain is trusted -----------
        Fact mip4 = Facts["mip4.dds"], shortMips = Facts["short_mips.dds"];
        bool m5 = mip4.RawBytes == 80 && shortMips.RawBytes == 32 && shortMips.Error == null;
        Finding("mip-truncation", m5,
                $"mip4.dds raw={mip4.RawBytes}B (expected 80) short_mips.dds raw={shortMips.RawBytes}B " +
                $"(expected 80, header says MipMapCount=4) error={(shortMips.Error ?? "none")}");

        // --- known format gates, re-measured here so the pair below is self-contained ----
        Console.Error.WriteLine($"{T} decode.dxt3 error={(Facts["dxt3.dds"].Error ?? "none")}");
        Console.Error.WriteLine($"{T} decode.bc5_dx10 format={Facts["bc5_dx10.dds"].Vk} " +
                                $"error={(Facts["bc5_dx10.dds"].Error ?? "none")}");
    }

    private static void Dump(string tag, string name, bool dds)
    {
        string path = Path.Combine(_fx, name);
        var f = new Fact { Tag = tag, File = path };
        Facts[tag] = f;
        if (!File.Exists(path))
        {
            f.Error = "fixture-missing";
            Console.Error.WriteLine($"{T} decode {tag} MISSING {path}");
            return;
        }
        try
        {
            UnityEngine.Texture2D tex = dds
                ? ModDdsLoader.TryLoadDds(new FilesystemFile(new FileInfo(path)))
                : UnityEngine.Texture2D.LoadFromFile(path);
            // The write is queued on a background writer; a read-back of a texture whose CPU
            // levels were released (Apply(makeNoLongerReadable)) needs the entry to be on disk.
            try { Waylith.Rendering.GpuTextureService.FlushTextureCache(); } catch { }
            if (tex == null) { f.Error = "null-texture"; Console.Error.WriteLine($"{T} decode {tag} null"); return; }

            f.Width = tex.width;
            f.Height = tex.height;
            f.Vk = VkLabel(tex);
            try { f.RawBytes = tex.GetRawTextureData()?.Length ?? 0; } catch (Exception ex) { f.RawBytes = -1; f.PixelError = ex.GetType().Name; }

            Color32[] px = null;
            try { px = tex.GetPixels32(); }
            catch (Exception ex) { f.PixelsThrew = true; f.PixelError = ex.GetType().Name + ": " + ex.Message; }

            if (px != null)
            {
                f.Pixels = px.Length;
                var set = new HashSet<int>();
                foreach (Color32 c in px)
                {
                    set.Add((c.r << 16) | (c.g << 8) | c.b | (c.a << 24));
                    if (c.a == 0) f.Alpha0++;
                }
                f.Distinct = set.Count;
                f.Colours = px.Distinct().Take(6).ToList();
                f.Placeholder = f.Width <= 2 && f.Height <= 2 && f.Distinct == 1 &&
                                f.Colours.Any(c => c.r > 200 && c.g < 60 && c.b > 200 && c.a > 200);
            }
            Console.Error.WriteLine($"{T} decode {tag} {f.Summary()}");
        }
        catch (Exception ex)
        {
            f.Error = ex.GetType().Name + ": " + ex.Message;
            Console.Error.WriteLine($"{T} decode {tag} EXC {f.Error}");
        }
    }

    // ---------------------------------------------------------------- 2. cache staleness ------
    private static void Phase2CacheStaleness()
    {
        string dir = Path.Combine(_outDir, "cache");
        Directory.CreateDirectory(dir);
        string a = Path.Combine(_fx, "cache_a.png");
        string b = Path.Combine(_fx, "cache_b.png");
        string work = Path.Combine(dir, "cache_a.png");

        try
        {
            File.Copy(a, work, overwrite: true);
            int hashA = Hash(UnityEngine.Texture2D.LoadFromFile(work));
            UnityEngine.Texture2D.LoadFromFile(work).PersistToTextureCache(work);
            Waylith.Rendering.GpuTextureService.FlushTextureCache();
            DateTime stamp = File.GetLastWriteTimeUtc(work);

            int hashBFresh = Hash(UnityEngine.Texture2D.LoadFromFile(b)); // baseline: no cache entry for b

            long lenA = new FileInfo(work).Length;
            File.WriteAllBytes(work, File.ReadAllBytes(b));          // different pixels, same length
            File.SetLastWriteTimeUtc(work, stamp);                    // ... and the old timestamp
            long lenB = new FileInfo(work).Length;
            int hashStale = Hash(UnityEngine.Texture2D.LoadFromFile(work));

            File.SetLastWriteTimeUtc(work, stamp.AddSeconds(5));      // control: timestamp moves
            int hashFresh = Hash(UnityEngine.Texture2D.LoadFromFile(work));

            bool stale = hashStale == hashA && hashA != hashBFresh && hashStale != hashBFresh;
            bool control = hashFresh == hashBFresh && hashBFresh != hashA;
            Console.Error.WriteLine($"{T} cache.stale lenA={lenA} lenB={lenB} hashA={hashA:x8} " +
                                    $"hashBFresh={hashBFresh:x8} hashStale={hashStale:x8} hashFresh={hashFresh:x8}");
            Mechanism("M2", "cache-served-stale-on-equal-length-and-mtime", stale && control,
                      $"cache={CacheRoot()} a={hashA:x8} b={hashBFresh:x8} served={hashStale:x8} " +
                      $"after-mtime-bump={hashFresh:x8} control={(control ? "ok" : "BROKEN")}");
            Check("cache-control", control, $"hashBFresh={hashBFresh:x8} hashFresh={hashFresh:x8}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{T} cache.stale EXC {ex.GetType().Name}: {ex.Message}");
            Mechanism("M2", "cache-served-stale-on-equal-length-and-mtime", false, "exception");
            Failed.Add("cache-stale-exception");
        }
    }

    // ---------------------------------------------------------------- 3. zero-length levels ----
    private static void Phase3EmptyLevels()
    {
        string dir = Path.Combine(_outDir, "empty");
        Directory.CreateDirectory(dir);
        string target = Path.Combine(dir, "empty.png");
        File.Copy(Path.Combine(_fx, "ok.png"), target, overwrite: true);
        try
        {
            // A texture created the ordinary Unity way, never filled in: TexturePixelData allocates
            // MipCount null level slots, so HasData (Levels.Length != 0) is already true and
            // PersistToTextureCache writes one zero-length level per mip (WritePayload writes
            // levels[i] ?? empty). TryReadLevelTable accepts a 0 length because its guard is
            // `num2 != 0 && num2 < expected` (TextureCacheStore.cs:188).
            var blank = new UnityEngine.Texture2D(8, 8, TextureFormat.RGBA32, 2, linear: false);
            blank.PersistToTextureCache(target);
            Waylith.Rendering.GpuTextureService.FlushTextureCache();

            UnityEngine.Texture2D back = null;
            string loadError = null;
            try { back = UnityEngine.Texture2D.LoadFromFile(target); }
            catch (Exception ex) { loadError = ex.GetType().Name + ": " + ex.Message; }

            string pixels = "n/a";
            int alpha0 = -1, distinct = -1, pixelsLen = -1;
            if (back != null)
            {
                try
                {
                    Color32[] px = back.GetPixels32();
                    pixelsLen = px.Length;
                    alpha0 = px.Count(c => c.a == 0);
                    distinct = px.Distinct().Count();
                    pixels = $"n={px.Length} alpha0={alpha0} distinct={distinct}";
                }
                catch (Exception ex) { pixels = "EXC " + ex.GetType().Name + ": " + ex.Message; }
            }
            bool accepted = back != null && back.width == 8 && back.height == 8;
            Console.Error.WriteLine($"{T} cache.emptylevels load={(back == null ? "<null>" : back.width + "x" + back.height)} " +
                                    $"pixels={pixels} loadError={(loadError ?? "none")}");
            Mechanism("M3", "cache-entry-with-zero-length-levels-accepted", accepted,
                      $"entry written from an unfilled 8x8 RGBA32 texture; read back " +
                      $"{(back == null ? "<null>" : back.width + "x" + back.height)} pixels={pixels}");
            Check("cache-empty-entry-readback", accepted && (alpha0 == 64 || pixels.StartsWith("EXC", StringComparison.Ordinal)), pixels);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{T} cache.emptylevels EXC {ex.GetType().Name}: {ex.Message}");
            Mechanism("M3", "cache-entry-with-zero-length-levels-accepted", false, "exception");
            Failed.Add("cache-empty-exception");
        }
    }

    // ---------------------------------------------------------------- 4. BC1 upload format -----
    private static void Phase4Formats()
    {
        var map = new (string Name, Waylith.Rendering.LoadedTextureFormat Format)[]
        {
            ("Dxt1", Waylith.Rendering.LoadedTextureFormat.Dxt1), ("Dxt5", Waylith.Rendering.LoadedTextureFormat.Dxt5),
            ("Bc4", Waylith.Rendering.LoadedTextureFormat.Bc4), ("Bc5", Waylith.Rendering.LoadedTextureFormat.Bc5),
            ("Bc7", Waylith.Rendering.LoadedTextureFormat.Bc7), ("Rgba32", Waylith.Rendering.LoadedTextureFormat.Rgba32),
        };
        var table = new List<string>();
        string dxt1 = "?";
        foreach (var (name, fmt) in map)
        {
            string vk = VkOf(fmt);
            table.Add($"{name}->{vk}");
            if (name == "Dxt1") dxt1 = vk;
        }
        Console.Error.WriteLine($"{T} formats {string.Join(" ", table)}");

        Fact alpha = Facts.TryGetValue("dxt1_alpha.dds", out var f) ? f : null;
        bool cpuSeesAlpha = alpha != null && alpha.Error == null && alpha.Alpha0 > 0;
        bool rgbBlock = dxt1.Contains("Bc1RGBUNormBlock", StringComparison.Ordinal);
        // VK_FORMAT_BC1_RGB_*_BLOCK is defined as opaque (alpha = 1.0); the punch-through texels
        // the CPU decoder paints with alpha 0 therefore render opaque on the GPU.
        Mechanism("M4", "dxt1-punch-through-alpha-uploaded-as-bc1-rgb", cpuSeesAlpha && rgbBlock,
                  $"ToVkFormat(Dxt1)={dxt1} cpuAlpha0={alpha?.Alpha0 ?? -1}/{alpha?.Pixels ?? -1} " +
                  $"fixture=dxt1_alpha.dds(3-colour BC1)");
        Check("bc1-alpha-disagreement", cpuSeesAlpha && rgbBlock, $"dxt1_vk={dxt1} cpuAlpha0={alpha?.Alpha0}");
    }

    // ---------------------------------------------------------------- helpers -----------------
    private static int Hash(UnityEngine.Texture2D tex)
    {
        Color32[] px = tex.GetPixels32();
        uint h = 2166136261;
        foreach (Color32 c in px)
        {
            foreach (byte b in new[] { c.r, c.g, c.b, c.a }) { h ^= b; h *= 16777619; }
        }
        return unchecked((int)h);
    }

    private static string Hex(Color32 c) => $"{c.r:x2}{c.g:x2}{c.b:x2}{c.a:x2}";

    private static string VkLabel(UnityEngine.Texture2D tex)
    {
        try
        {
            object core = typeof(UnityEngine.Texture2D)
                .GetField("CoreTexture", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(tex);
            object cpu = core?.GetType().GetProperty("CpuData")?.GetValue(core);
            object fmt = cpu?.GetType().GetProperty("Format")?.GetValue(cpu);
            return fmt?.ToString() ?? "?";
        }
        catch { return "?"; }
    }

    private static string VkOf(Waylith.Rendering.LoadedTextureFormat fmt)
    {
        try
        {
            MethodInfo m = typeof(Waylith.Graphics.Textures.TexturePixelData)
                .GetMethod("ToVkFormat", BindingFlags.Public | BindingFlags.Static);
            return m?.Invoke(null, new object[] { fmt })?.ToString() ?? "?";
        }
        catch (Exception ex) { return "EXC:" + ex.GetType().Name; }
    }

    private static string CacheRoot()
    {
        try
        {
            PropertyInfo p = typeof(Waylith.Rendering.GpuTextureService)
                .GetProperty("TextureCacheRoot", BindingFlags.NonPublic | BindingFlags.Static);
            return p?.GetValue(null)?.ToString() ?? "?";
        }
        catch (Exception ex) { return "EXC:" + ex.GetType().Name; }
    }

    private static string Summary(this Fact f)
    {
        if (f.Error != null) return $"error={f.Error}";
        string colours = string.Join(",", f.Colours.Select(Hex));
        return $"size={f.Width}x{f.Height} fmt={f.Vk} px={f.Pixels} distinct={f.Distinct} " +
               $"alpha0={f.Alpha0} raw={f.RawBytes} colours={colours}" +
               (f.PixelError != null ? $" pixelError={f.PixelError}" : "");
    }

    private static string Short(this Fact f) =>
        f.Error != null ? "error:" + f.Error : $"{f.Width}x{f.Height}/{f.Distinct}col/a0={f.Alpha0}";

    private static void Check(string name, bool ok, string detail)
    {
        Console.Error.WriteLine($"{T} assert={name} ok={(ok ? "true" : "false")} {detail}");
        if (!ok) Failed.Add(name);
    }

    private static void Mechanism(string id, string name, bool ok, string evidence)
    {
        Console.Error.WriteLine($"{T} MECHANISM={id} name={name} verdict={(ok ? "CONFIRMED" : "REFUTED")} {evidence}");
        if (!ok) Failed.Add("mechanism-" + id);
    }

    private static void Finding(string name, bool ok, string detail)
    {
        Console.Error.WriteLine($"{T} FINDING={name} verdict={(ok ? "CONFIRMED" : "REFUTED")} {detail}");
        if (!ok) Failed.Add("finding-" + name);
    }

    private static void Quit()
    {
        if (Interlocked.Exchange(ref _done, 1) != 0) return;
        try { Root.Shutdown(); }
        catch (Exception ex) { Console.Error.WriteLine($"{T} shutdown failed: {ex.GetType().Name}: {ex.Message}"); }
    }
}
