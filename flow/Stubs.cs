using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace IconFlow
{
    /// <summary>
    /// The only two pieces of the game that are faked, and they are faked at the exact seam the
    /// bug lives on: what <c>Texture2D.name</c> is for a file on disk.
    ///
    ///  * <see cref="NameContract.GameContract"/> - what ContentFinder is documented to hand out:
    ///    the file name with the texture extension removed ("UniqueRevolverA" for
    ///    "UniqueRevolverA.png.dds").
    ///  * <see cref="NameContract.PortLeak"/> - what this port actually hands out for DLC art:
    ///    <c>Path.GetFileNameWithoutExtension</c>, which strips one extension only, so the inner
    ///    ".png" of a ".png.dds" survives ("UniqueRevolverA.png").
    ///
    /// Nothing else is stubbed: the filtering, grouping, path joining and modulo selection are the
    /// replica in Replica.cs, which is checked line-by-line against the shipped IL (Facts.cs).
    /// </summary>
    enum NameContract
    {
        GameContract,
        PortLeak,
    }

    static class TextureNaming
    {
        /// <summary>Extensions a texture ships under on this port (the outer one is ".dds").</summary>
        static readonly string[] Extensions = { ".png", ".jpg", ".jpeg", ".psd", ".dds" };

        public static string StripAll(string fileName)
        {
            string s = fileName;
            bool peeled = true;
            while (peeled)
            {
                peeled = false;
                foreach (string ext in Extensions)
                {
                    if (s.Length <= ext.Length) continue;
                    if (!s.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) continue;
                    s = s.Substring(0, s.Length - ext.Length);
                    peeled = true;
                    break;
                }
            }
            return s;
        }

        public static string Apply(string fileName, NameContract contract)
            => contract == NameContract.GameContract ? StripAll(fileName) : Path.GetFileNameWithoutExtension(fileName);

        /// <summary>
        /// Measurement only - never used by the replica. "Does this texture name look like a mask?"
        /// Extensions are peeled first so "UniqueRevolverA_m.png" is still recognised on the port.
        /// </summary>
        public static bool IsMask(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string s = StripAll(name.Trim());
            if (s.Length <= 2) return false;
            if (!s.EndsWith("_m", StringComparison.Ordinal)) return false;
            char before = s[s.Length - 3];
            return before != 'm' && before != 'M';
        }
    }

    /// <summary>UnityEngine.Texture2D reduced to the two things the chain reads: name and origin.</summary>
    sealed class Tex
    {
        public string Name;
        public string Source;      // file name on disk
        public override string ToString() => Name;
    }

    interface IContentFinder
    {
        IReadOnlyList<Tex> GetAllInFolder(string path);
        Tex Get(string path, bool reportFailure);
    }

    /// <summary>
    /// Minimal stand-in for <c>ContentFinder&lt;Texture2D&gt;</c>. It reads a real directory and
    /// exposes one entry per texture file, named according to the contract under test.
    /// </summary>
    sealed class FolderContentFinder : IContentFinder
    {
        readonly Dictionary<string, Tex> _byName;
        public int FileCount { get; private set; }
        public NameContract Contract { get; }

        FolderContentFinder(IEnumerable<Tex> textures, NameContract contract)
        {
            Contract = contract;
            var list = textures.ToList();
            FileCount = list.Count;
            _byName = new Dictionary<string, Tex>(StringComparer.Ordinal);
            foreach (var t in list) _byName[t.Name] = t;
        }

        public static FolderContentFinder Load(string directory, NameContract contract)
        {
            if (!Directory.Exists(directory))
                throw new DirectoryNotFoundException("fixture directory not found: " + directory);

            var textures = Directory.GetFiles(directory)
                                    .Select(Path.GetFileName)
                                    .Where(n => !n.StartsWith(".", StringComparison.Ordinal))
                                    .OrderBy(n => n, StringComparer.Ordinal)
                                    .Select(n => new Tex { Name = TextureNaming.Apply(n, contract), Source = n })
                                    .ToList();
            return new FolderContentFinder(textures, contract);
        }

        // ContentFinder<T>.GetAllInFolder: every texture under the folder, in a stable order.
        // The real Init re-sorts with `orderby x.name`, so the order here does not matter - but
        // keeping it deterministic keeps the harness reproducible.
        public IReadOnlyList<Tex> GetAllInFolder(string path) => _byName.Values.OrderBy(t => t.Name, StringComparer.Ordinal).ToList();

        // ContentFinder<T>.Get(path, reportFailure): exact-name lookup, null on a miss.
        public Tex Get(string path, bool reportFailure)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string name = path.Substring(path.LastIndexOf('/') + 1);
            return _byName.TryGetValue(name, out var t) ? t : null;
        }
    }
}
