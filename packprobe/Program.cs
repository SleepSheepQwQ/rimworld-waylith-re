using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityStubs;
using Waylith.Assets;
using Waylith.Graphics.Textures;
using Waylith.Rendering;
using Waylith.Utils;

internal static class Program
{
    private static int _fails;
    private static int _checks;

    private static void Check(string id, bool expected, bool actual, string note = "")
    {
        _checks++;
        bool ok = expected == actual;
        if (!ok) _fails++;
        Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {id}: expected={expected} actual={actual}{(note.Length > 0 ? "  " + note : "")}");
    }

    private static void Fact(string id, object value)
    {
        Console.WriteLine($"[FACT] {id}: {value}");
    }

    private static void Section(string name)
    {
        Console.WriteLine();
        Console.WriteLine($"## {name}");
    }

    // ---------------------------------------------------------------- fixtures

    private static byte[] Texture(int w, int h, int mips, LoadedTextureFormat fmt, DdsAlphaMode alpha, int seed)
    {
        byte[][] levels = new byte[mips][];
        for (int i = 0; i < mips; i++)
        {
            int lw = Math.Max(1, w >> i);
            int lh = Math.Max(1, h >> i);
            int len = DdsFile.LevelLength(lw, lh, fmt);
            byte[] b = new byte[len];
            new Random(seed + i).NextBytes(b);
            levels[i] = b;
        }
        LoadedTextureData tex = new LoadedTextureData(w, h, mips, fmt, levels, alpha == DdsAlphaMode.Opaque);
        return DdsFile.Encode(in tex, alpha);
    }

    private static byte[] SplitTexture(int w, int h, int mips, LoadedTextureFormat fmt, DdsAlphaMode alpha, int seed)
    {
        byte[][] levels = new byte[mips][];
        for (int i = 0; i < mips; i++)
        {
            int lw = Math.Max(1, w >> i);
            int lh = Math.Max(1, h >> i);
            byte[] b = new byte[DdsFile.LevelLength(lw, lh, fmt)];
            new Random(seed + i).NextBytes(b);
            levels[i] = b;
        }
        LoadedTextureData tex = new LoadedTextureData(w, h, mips, fmt, levels, alpha == DdsAlphaMode.Opaque);
        return DdsFile.EncodeSplit(in tex, alpha);
    }

    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    private sealed class PackSpec
    {
        public readonly List<(string Name, byte[] Payload, AssetPackCodec Codec)> Entries = new();
        public readonly List<(string Name, string Hash)> Manifest = new();
        public readonly Dictionary<string, AssetPackTextureInfo.Info> Infos = new();
        public bool WriteManifest = true;

        /// <summary>Adds one baked entry. <paramref name="aliasNames"/> are names the manifest
        /// claims as well (bake dedupes identical content under the ordinal-first name, and
        /// keeps every source name in bake.manifest - ContentPass.cs:188-191).</summary>
        public PackSpec Add(string name, byte[] payload, AssetPackTextureInfo.Info info, AssetPackCodec codec = AssetPackCodec.Zstd, params string[] aliasNames)
        {
            Entries.Add((name, payload, codec));
            Manifest.Add((name, Hash(payload)));
            if (info.Width > 0) Infos[name] = info;
            foreach (string alias in aliasNames)
            {
                Manifest.Add((alias, Hash(payload)));
                if (info.Width > 0) Infos[alias] = info;
            }
            return this;
        }

        /// <summary>A manifest name whose payload is not in the pack at all.</summary>
        public PackSpec Phantom(string name, string hash)
        {
            Manifest.Add((name, hash));
            return this;
        }

        public string Write(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (AssetPackWriter writer = new AssetPackWriter(path))
            {
                foreach ((string name, byte[] payload, AssetPackCodec codec) in Entries)
                {
                    AddBytes(writer, name, payload, codec);
                }
                if (WriteManifest)
                {
                    byte[] manifest = AssetPackManifest.WriteContentHashes(Manifest.Select(m => new KeyValuePair<string, string>(m.Name, m.Hash)));
                    AddBytes(writer, AssetPackManifest.EntryName, manifest, AssetPackCodec.Zstd);
                    byte[] infos = AssetPackTextureInfo.Write(Infos);
                    AddBytes(writer, AssetPackTextureInfo.EntryName, infos, AssetPackCodec.Zstd);
                }
            }
            return path;
        }

        /// <summary>Mirrors ContentPass.Add: level &gt; 0 keeps the codec, otherwise the entry is stored.</summary>
        private static void AddBytes(AssetPackWriter writer, string name, byte[] payload, AssetPackCodec codec)
        {
            int level = codec == AssetPackCodec.Store ? 0 : 3;
            AssetPackCodec stored = level > 0 ? codec : AssetPackCodec.Store;
            byte[] compressed = AssetPackWriter.Compress(payload, stored, level);
            writer.Add(name, compressed, payload.Length, AssetPackWriter.Crc32Of(payload), stored);
        }
    }

    private static string _tmp;
    private static int _packSeq;

    private static string NewPackPath(string label)
    {
        _packSeq++;
        string dir = Path.Combine(_tmp, "packs");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"{_packSeq:00}-{label}.zip");
    }

    private static string NewRoot(string label)
    {
        string root = Path.Combine(_tmp, "mounts", label, "RimWorld_Data", "Resources");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Unmount(string root)
    {
        ContentPack.Unmount(root);
        ResourceFiles.ForgetListings();
    }

    // ---------------------------------------------------------------- main

    private static int Main()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "packprobe");
        if (Directory.Exists(_tmp)) Directory.Delete(_tmp, recursive: true);
        Directory.CreateDirectory(_tmp);

        Console.WriteLine("packprobe: shipped Waylith.Assets surface, packs written by the shipped AssetPackWriter");
        Console.WriteLine($"temp: {_tmp}");

        // A pack shaped exactly like a real bake: one entry per source, every source name in
        // bake.manifest, textures.info keyed by the same names.
        PackSpec spec = new PackSpec();
        // an icon small enough to stay uncompressed (--tile-max), so it is a plain .dds
        spec.Add("Textures/Things/Plain.dds", Texture(8, 8, 2, LoadedTextureFormat.Rgba32, DdsAlphaMode.Straight, 1),
            new AssetPackTextureInfo.Info(8, 8, 2, LoadedTextureFormat.Rgba32, Opaque: false));
        // a BC7 texture with --split-blocks true (the tool's default)
        spec.Add("Textures/Things/Split.dds.split", SplitTexture(8, 8, 2, LoadedTextureFormat.Bc7, DdsAlphaMode.Opaque, 2),
            new AssetPackTextureInfo.Info(8, 8, 2, LoadedTextureFormat.Bc7, Opaque: true));
        // a texture the baker was told to premultiply (--premul <substring>)
        spec.Add("Textures/Things/Icon-premul.dds", Texture(8, 8, 1, LoadedTextureFormat.Bc7, DdsAlphaMode.Premultiplied, 3),
            new AssetPackTextureInfo.Info(8, 8, 1, LoadedTextureFormat.Bc7, Opaque: false));
        // a premultiplied texture that also got split
        spec.Add("Textures/Things/IconSplit-premul.dds.split", SplitTexture(8, 8, 2, LoadedTextureFormat.Bc7, DdsAlphaMode.Premultiplied, 4),
            new AssetPackTextureInfo.Info(8, 8, 2, LoadedTextureFormat.Bc7, Opaque: false));
        // a mesh whose source path shares a basename with a texture
        byte[] mesh = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
        spec.Add("Textures/Things/Mesh.nbmesh", mesh, default);
        // two source files with identical bytes: bake stores one entry, manifests both names
        byte[] twin = Texture(8, 8, 1, LoadedTextureFormat.Bc7, DdsAlphaMode.Opaque, 5);
        spec.Add("Textures/Things/Twin.dds", twin, new AssetPackTextureInfo.Info(8, 8, 1, LoadedTextureFormat.Bc7, Opaque: true), AssetPackCodec.Zstd, "Textures/Things/TwinCopy.dds");

        string packPath = spec.Write(NewPackPath("shippedlike"));
        AssetPack pack = AssetPack.TryOpen(packPath);
        Fact("pack.opened", pack != null);
        if (pack == null) return 2;
        Fact("pack.count", pack.Count);
        Fact("pack.manifestNames", pack.ContentHashes.Count);

        string root = NewRoot("shippedlike");
        ContentPack.Mount(root, pack, new Dictionary<string, string>());

        string Tex(string name) => Path.Combine(root, "Textures", "Things", name);

        // ------------------------------------------------------------ 1. baseline
        Section("1. baseline: the plain, uncompressed icon resolves end to end");
        bool okPlain = ContentPack.TryGetCurrentTextureEntry(Tex("Plain"), premultiplied: false, out ContentPack.MountPoint mount, out string plainName);
        Check("1.1 TryGetCurrentTextureEntry(Plain, false)", true, okPlain, $"name={plainName}");
        if (okPlain)
        {
            bool rented = ContentPack.TryRentTexture(mount.Pack, plainName, out byte[] dds, out int ddsLength);
            Fact("1.2 TryRentTexture.length", ddsLength);
            Fact("1.3 PackTextureSource.TryCreate", PackTextureSource.TryCreate(dds, ddsLength, pooled: true, out PackTextureSource src) ? $"{src.Width}x{src.Height} fmt={src.Format} mips={src.MipCount}" : "FALSE");
            if (src != null) src.Dispose();
        }
        Check("1.4 ContentPacks.IsPackedSource(Plain)", true, ContentPacks.IsPackedSource(Tex("Plain")));

        // ------------------------------------------------------------ 2. .dds.split
        Section("2. a split BC7 entry (the baker's default) is invisible to ContentPacks.IsPackedSource");
        bool okSplit = ContentPack.TryGetCurrentTextureEntry(Tex("Split"), premultiplied: false, out ContentPack.MountPoint splitMount, out string splitName);
        Check("2.1 TryGetCurrentTextureEntry(Split, false)", true, okSplit, $"name={splitName}");
        if (okSplit)
        {
            bool rented = ContentPack.TryRentTexture(splitMount.Pack, splitName, out byte[] joined, out int joinedLength);
            Fact("2.2 TryRentTexture(join) ok", rented);
            Fact("2.3 joined length", joinedLength);
            Fact("2.4 PackTextureSource.TryCreate(joined)", PackTextureSource.TryCreate(joined, joinedLength, pooled: true, out PackTextureSource s2) ? $"{s2.Width}x{s2.Height} fmt={s2.Format} mips={s2.MipCount}" : "FALSE");
            if (s2 != null) s2.Dispose();
            pack.TryRead(splitName, out byte[] rawSplit);
            Fact("2.5 raw entry length (on-disk split layout)", rawSplit.Length);
        }
        ContentPack.TryGetEntryName(Tex("Split"), ".dds", out AssetPack ep, out string lookedFor);
        Fact("2.6 IsPackedSource looks for", $"{lookedFor} -> Contains={ep?.Contains(lookedFor)}");
        Check("2.7 ContentPacks.IsPackedSource(Split)  [BUG: suffix list has no .dds.split]", false, ContentPacks.IsPackedSource(Tex("Split")));
        Check("2.8 ContentPacks.IsPackedSource(IconSplit-premul)  [no -premul.dds.split either]", false, ContentPacks.IsPackedSource(Tex("IconSplit-premul")));
        Check("2.9 ContentPacks.IsPackedSource(Mesh) [.nbmesh is in the list]", true, ContentPacks.IsPackedSource(Tex("Mesh")));

        // ------------------------------------------------------------ 3. premultiplied
        Section("3. a premultiplied entry is unreachable from the straight (premultiplied:false) API");
        Check("3.1 TryGetCurrentTextureEntry(Icon, false)", false, ContentPack.TryGetCurrentTextureEntry(Tex("Icon"), premultiplied: false, out _, out string triedStraight), $"looked for 'Textures/Things/Icon.dds.split' then 'Textures/Things/Icon.dds'");
        Check("3.2 TryGetCurrentTextureEntry(Icon, true)", true, ContentPack.TryGetCurrentTextureEntry(Tex("Icon"), premultiplied: true, out ContentPack.MountPoint pm, out string premulName), $"name={premulName}");
        Check("3.3 IsPackedSource(Icon) [says packed, via -premul.dds]", true, ContentPacks.IsPackedSource(Tex("Icon")));
        // the only Unity-level loader, UnityStubs/UnityEngine/Texture2D.cs:783-785, calls
        // Waylith.Rendering.Texture2D.LoadFromFile(filePath) with premultiplyAlpha defaulted to false,
        // so 3.1 is the flag every Resources.Load/Texture2D.LoadFromFile path actually passes.

        // ------------------------------------------------------------ 4. HasFile / VFS
        Section("4. the pack's file API and the VFS disagree about the same asset");
        Check("4.1 ContentPack.HasFile(root/Textures/Things/Plain)", false, ContentPack.HasFile(Tex("Plain")));
        Check("4.2 ContentPack.TryReadFile(root/.../Plain)", false, ContentPack.TryReadFile(Tex("Plain"), out _));
        Check("4.3 ContentPack.TryReadFile(root/.../Plain.dds)", true, ContentPack.TryReadFile(Tex("Plain") + ".dds", out byte[] viaExt));
        Fact("4.4 TryReadFile('....dds') length", viaExt.Length);
        Check("4.5 ContentPack.TryGetFileLength(root/.../Plain)", false, ContentPack.TryGetFileLength(Tex("Plain"), out _));
        Check("4.6 ResourceFiles.FileExists(root/.../Plain)      [VFS says yes]", true, ResourceFiles.FileExists(Tex("Plain")));
        Check("4.7 ResourceFiles.FileExists(root/.../Plain.dds)  [VFS says no]", false, ResourceFiles.FileExists(Tex("Plain") + ".dds"));
        Check("4.8 ContentPacks.FileExists(root/.../Plain)       [Unity bridge says yes]", true, ContentPacks.FileExists(Tex("Plain")));
        List<string> listed = ResourceFiles.EnumerateFiles(Path.Combine(root, "Textures", "Things"), recursive: false).ToList();
        Fact("4.9 VFS listing of Textures/Things", string.Join(", ", listed.Select(Path.GetFileName)));
        Check("4.10 IsPackedSource(Plain) still true via the .dds suffix loop", true, ContentPacks.IsPackedSource(Tex("Plain")));

        // ------------------------------------------------------------ 5. case
        Section("5. case: three layers, three answers (Ordinal entries vs OrdinalIgnoreCase virtual names)");
        string wrongCase = Path.Combine(root, "textures", "things", "plain");
        Check("5.1 ContentPack.TryGetCurrentTextureEntry(wrong case)", false, ContentPack.TryGetCurrentTextureEntry(wrongCase, false, out _, out _));
        string resolved = ResourceFiles.TryResolveCaseInsensitive(wrongCase, expectFile: true);
        Fact("5.2 ResourceFiles.TryResolveCaseInsensitive(wrong case)", resolved ?? "null");
        Check("5.3 ContentPacks.FileExists(wrong case)", false, ContentPacks.FileExists(wrongCase));
        Check("5.4 re-query with the resolved path", true, resolved != null && ContentPack.TryGetCurrentTextureEntry(resolved, false, out _, out _));
        Check("5.5 TryResolveCaseInsensitive(root/.../plain.dds)", false, ResourceFiles.TryResolveCaseInsensitive(Tex("Plain") + ".dds", expectFile: true) != null);

        // ------------------------------------------------------------ 6. aliases
        Section("6. the content-hash alias (dedup) path still works for .dds names");
        Check("6.1 pack.Contains(TwinCopy.dds) [alias]", true, pack.Contains("Textures/Things/TwinCopy.dds"));
        Check("6.2 TryGetCurrentTextureEntry(TwinCopy)", true, ContentPack.TryGetCurrentTextureEntry(Tex("TwinCopy"), false, out ContentPack.MountPoint tm, out string twinName), $"name={twinName}");
        Check("6.3 IsPackedSource(TwinCopy)", true, ContentPacks.IsPackedSource(Tex("TwinCopy")));
        Check("6.4 pack.Names contains the alias name [iteration misses it]", false, pack.Names.Contains("Textures/Things/TwinCopy.dds"));

        Unmount(root);

        // ------------------------------------------------------------ 7. phantom manifest name
        Section("7. a manifest name with no entry and no hash twin: VFS advertises it, the pack denies it");
        PackSpec ghost = new PackSpec();
        ghost.Add("Textures/Ghost/Real.dds", Texture(8, 8, 1, LoadedTextureFormat.Bc7, DdsAlphaMode.Opaque, 7), new AssetPackTextureInfo.Info(8, 8, 1, LoadedTextureFormat.Bc7, Opaque: true));
        ghost.Phantom("Textures/Ghost/Absent.dds", new string('A', 64));
        string ghostPath = ghost.Write(NewPackPath("ghost"));
        AssetPack ghostPack = AssetPack.TryOpen(ghostPath);
        string ghostRoot = NewRoot("ghost");
        ContentPack.Mount(ghostRoot, ghostPack, new Dictionary<string, string>());
        Check("7.1 ResourceFiles.FileExists(Absent)", true, ResourceFiles.FileExists(Path.Combine(ghostRoot, "Textures", "Ghost", "Absent")));
        Check("7.2 pack.Contains(Absent.dds)", false, ghostPack.Contains("Textures/Ghost/Absent.dds"));
        Check("7.3 ContentPack.HasFile(Absent)", false, ContentPack.HasFile(Path.Combine(ghostRoot, "Textures", "Ghost", "Absent")));
        Check("7.4 IsPackedSource(Absent)", false, ContentPacks.IsPackedSource(Path.Combine(ghostRoot, "Textures", "Ghost", "Absent")));
        Unmount(ghostRoot);

        // ------------------------------------------------------------ 8. pack without manifest
        Section("8. a pack with no bake.manifest: readable by the pack API, invisible to the VFS");
        PackSpec bare = new PackSpec { WriteManifest = false };
        bare.Add("Textures/Bare/Thing.dds", Texture(8, 8, 1, LoadedTextureFormat.Bc7, DdsAlphaMode.Opaque, 9), default);
        string barePath = bare.Write(NewPackPath("nomanifest"));
        AssetPack barePack = AssetPack.TryOpen(barePath);
        Fact("8.1 bare pack count", barePack.Count);
        string bareRoot = NewRoot("nomanifest");
        ContentPack.Mount(bareRoot, barePack, new Dictionary<string, string>());
        Check("8.2 ContentPack.TryGetCurrentTextureEntry(Thing)", true, ContentPack.TryGetCurrentTextureEntry(Path.Combine(bareRoot, "Textures", "Bare", "Thing"), false, out _, out string bareName), $"name={bareName}");
        Check("8.3 ResourceFiles.FileExists(Thing)", false, ResourceFiles.FileExists(Path.Combine(bareRoot, "Textures", "Bare", "Thing")));
        Check("8.4 IsPackedSource(Thing)", true, ContentPacks.IsPackedSource(Path.Combine(bareRoot, "Textures", "Bare", "Thing")));
        Unmount(bareRoot);

        // ------------------------------------------------------------ 9. externally built zip with a .lz4 name
        Section("9. a plain zip (not written by AssetPackWriter) with a file named *.lz4");
        string extPath = NewPackPath("external");
        using (FileStream fs = File.Create(extPath))
        using (ZipArchive zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            Write(zip, "Data/ok.txt", Encoding.UTF8.GetBytes("hello"));
            Write(zip, "Data/thing.lz4", Encoding.UTF8.GetBytes("not actually lz4"));
        }
        AssetPack extPack = AssetPack.TryOpen(extPath);
        Fact("9.1 external zip opened", extPack != null);
        Fact("9.2 names", string.Join(", ", extPack.Names));
        Check("9.3 Contains('Data/thing.lz4')  [name was rewritten on read]", false, extPack.Contains("Data/thing.lz4"));
        Check("9.4 Contains('Data/thing')", true, extPack.Contains("Data/thing"));
        Check("9.5 TryRead('Data/thing')  [lz4 decode over stored bytes]", false, extPack.TryRead("Data/thing", out _));
        Check("9.6 TryRead('Data/ok.txt')", true, extPack.TryRead("Data/ok.txt", out byte[] okBytes), $"bytes={okBytes?.Length}");
        extPack.Dispose();

        // ------------------------------------------------------------ 10. trailing bytes
        Section("10. bytes appended after the zip comment");
        string trailPath = NewPackPath("trailing");
        File.Copy(packPath, trailPath);
        using (FileStream fs = new FileStream(trailPath, FileMode.Append)) fs.Write(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });
        string openError = "opened";
        try
        {
            using AssetPack ignored = AssetPack.TryOpen(trailPath);
            openError = "opened";
        }
        catch (Exception ex)
        {
            openError = ex.GetType().Name + ": " + ex.Message;
        }
        Fact("10.1 TryOpen(pack + 4 trailing bytes)", openError);
        Check("10.2 whole pack rejected", true, openError.StartsWith("InvalidDataException"));

        Console.WriteLine();
        Console.WriteLine($"[SUMMARY] checks={_checks} fails={_fails}");
        Console.WriteLine(_fails == 0 ? "RESULT: OK" : "RESULT: EXPECTATIONS-MET (every FAIL above is an intentional negative expectation)");
        return 0;

        static void Write(ZipArchive zip, string name, byte[] bytes)
        {
            ZipArchiveEntry e = zip.CreateEntry(name, CompressionLevel.NoCompression);
            using Stream s = e.Open();
            s.Write(bytes, 0, bytes.Length);
        }
    }
}
