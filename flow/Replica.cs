using System;
using System.Collections.Generic;
using System.Linq;

namespace IconFlow
{
    enum GraphicKind
    {
        Bad,        // BaseContent.BadGraphic: Init found an empty folder
        Single,     // Graphic_Single, one texture, optional folder-level mask
        Multi,      // Graphic_Multi, one texture set addressed by direction
    }

    sealed class Variant
    {
        public GraphicKind Kind;
        public string Path;          // what GraphicDatabase.Get received
        public string TextureName;   // what MatAt(rot).mainTexture is, for the north rotation
        public string MaskPath;      // Graphic.path + MaskSuffix, when the probe found it
        public bool IsMaskFlavoured => TextureNaming.IsMask(TextureName);
        public override string ToString() => Kind + ":" + TextureName;
    }

    sealed class InitResult
    {
        public string FolderPath;
        public int FilesInFolder;
        public int AfterMaskFilter;
        public int Groups;
        public List<Variant> SubGraphics = new List<Variant>();
    }

    /// <summary>
    /// Verse.Graphic_Collection.Init, reproduced line by line from the shipped IL.
    ///
    /// Source of truth: <c>dlls/Assembly-CSharp.dll</c> (1.7.9795.32073),
    /// <c>Verse.Graphic_Collection::Init(GraphicRequest)</c>. IL offsets below are that method's
    /// body, as decoded by MiniIl; the constants (mask suffix, grouping character, path separator,
    /// direction markers) are read from the same binary at run time by <see cref="FlowFacts"/>.
    ///
    /// Nothing is "improved" here: the known port bug (names carrying ".png") is reproduced by
    /// feeding the same code a different name contract, not by branching on correctness.
    /// </summary>
    static class GraphicCollectionInit
    {
        sealed class Entry
        {
            public Tex Tex;
            public string Key;
            public string Name => Tex.Name;
        }

        public static InitResult Init(string folderPath, IReadOnlyList<Tex> folder, FlowFacts facts, IContentFinder finder)
        {
            var result = new InitResult { FolderPath = folderPath, FilesInFolder = folder.Count };

            // IL_0079..IL_00F0
            //   from x in ContentFinder<Texture2D>.GetAllInFolder(req.path)
            //   where !x.name.EndsWith(Graphic_Single.MaskSuffix)     // IL_008D..IL_00A3
            //   orderby x.name                                        // IL_00A8
            //   select (x, x.name.Split('_')[0])                      // IL_00CC (Split(char, None)[0])
            //   .ToList()
            var query = folder
                .Where(x => !x.Name.EndsWith(facts.MaskSuffix))   // String::EndsWith(string), as in IL
                .OrderBy(x => x.Name, StringComparer.Ordinal)
                .Select(x => new Entry { Tex = x, Key = x.Name.Split(facts.GroupSeparator)[0] })
                .ToList();

            result.AfterMaskFilter = query.Count;

            // IL_00F7..IL_0127: an empty folder is an error, and subGraphics becomes
            // new[] { BaseContent.BadGraphic } - never an empty array.
            if (query.Count == 0)
            {
                result.SubGraphics.Add(new Variant { Kind = GraphicKind.Bad, Path = null, TextureName = null });
                return result;
            }

            var subGraphics = new List<Variant>();

            // IL_0128..: foreach (var group in query.GroupBy(s => s.Item2))
            foreach (var group in GroupInOrder(query, e => e.Key))
            {
                result.Groups++;

                // IL_0166..IL_017E: text = req.path + "/" + group.Key
                string groupPath = folderPath + facts.PathSeparator + group.Key;

                var items = group.Items;

                // IL_0185..IL_0229: drop every direction-specific entry; one multi graphic covers
                // the whole group afterwards. The test is name.Contains on the four literals.
                bool anyDirectional = false;
                for (int i = 0; i < items.Count; i++)
                {
                    // String::Contains(string), as in IL
                    if (facts.DirectionMarkers.Any(d => items[i].Name.Contains(d)))
                    {
                        items.RemoveAt(i);
                        i--;
                        anyDirectional = true;
                    }
                }

                // IL_022E..IL_02CD: one Graphic_Single per remaining texture
                //   path     = req.path + "/" + x.name
                //   maskPath = this.path + MaskSuffix           (folder-level mask, probed with Get)
                //   graph    = GraphicDatabase.Get(SingleGraphicType, path, ..., maskPath or null)
                if (items.Count > 0)
                {
                    foreach (var e in items)
                    {
                        string path = folderPath + facts.PathSeparator + e.Name;
                        string maskCandidate = folderPath + facts.MaskSuffix;
                        Tex mask = finder.Get(maskCandidate, false);
                        subGraphics.Add(new Variant
                        {
                            Kind = GraphicKind.Single,
                            Path = path,
                            TextureName = e.Name,
                            MaskPath = mask != null ? maskCandidate : null,
                        });
                    }
                }

                // IL_02E9..IL_0320: if any direction was culled, add one Graphic_Multi at the group path
                if (anyDirectional)
                {
                    subGraphics.Add(new Variant
                    {
                        Kind = GraphicKind.Multi,
                        Path = groupPath,
                        // Graphic_Multi addresses textures as path + "_north"/"_east"/... so the
                        // north-facing icon texture is the group key with that suffix.
                        TextureName = group.Key + facts.DirectionMarkers.FirstOrDefault(d => d.EndsWith("north", StringComparison.Ordinal)),
                        MaskPath = null,
                    });
                }
            }

            // IL_033C..IL_0343: subGraphics = list.ToArray()
            result.SubGraphics = subGraphics;
            return result;
        }

        // Enumerable.GroupBy keeps keys in first-appearance order; so does this.
        static IEnumerable<(string Key, List<T> Items)> GroupInOrder<T>(List<T> items, Func<T, string> key)
        {
            var order = new List<string>();
            var map = new Dictionary<string, List<T>>(StringComparer.Ordinal);
            foreach (var it in items)
            {
                string k = key(it);
                if (!map.TryGetValue(k, out var list))
                {
                    list = new List<T>();
                    map[k] = list;
                    order.Add(k);
                }
                list.Add(it);
            }
            foreach (var k in order) yield return (k, map[k]);
        }
    }

    /// <summary>
    /// The rest of the chain, also from the shipped IL:
    ///
    ///   Graphic_Random::SubGraphicFor(thing)                                  (selector)
    ///     = subGraphics[(thing.OverrideGraphicIndex ?? thing.thingIDNumber) % subGraphics.Length]
    ///
    ///   GraphicUtility::ExtractInnerGraphicFor(graphic, thing, indexOverride)  (def -> inner)
    ///     = graphic is Graphic_Random ? SubGraphicFor(thing) : graphic
    ///
    ///   Widgets::GetIconFor(thing, ...)                                       (inner -> texture)
    ///     = thing.Graphic.ExtractInnerGraphicFor(thing).MatAt(rot).mainTexture
    /// </summary>
    static class IconChain
    {
        const int North = 0;   // Rot4.North

        public static Variant SubGraphicFor(InitResult collection, int thingIDNumber, int? overrideIndex = null)
        {
            int index = overrideIndex ?? thingIDNumber;
            return collection.SubGraphics[index % collection.SubGraphics.Count];
        }

        public static Variant ExtractInnerGraphicFor(InitResult collection, int thingIDNumber, int? overrideIndex = null)
            => SubGraphicFor(collection, thingIDNumber, overrideIndex);

        public static string IconTextureName(Variant variant, int rot = North)
        {
            if (variant == null) return null;
            if (variant.Kind != GraphicKind.Multi) return variant.TextureName;
            return variant.TextureName;   // already key + "_north" for the north rotation
        }

        public static string IconTextureNameFor(InitResult collection, int thingIDNumber, int rot = North)
            => IconTextureName(ExtractInnerGraphicFor(collection, thingIDNumber), rot);
    }
}
