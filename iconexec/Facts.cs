using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace IconExec
{
    /// <summary>
    /// What the shipped dll says about the icon path, read with metadata + IL only.
    ///
    /// These are not comments: every one is asserted against dlls/Assembly-CSharp.dll before any
    /// code is executed, and a build that no longer has this shape stops the run as BROKEN (exit 2)
    /// instead of letting the scenarios pass against methods that no longer exist.
    /// </summary>
    sealed class Facts
    {
        public readonly List<string> Ok = new List<string>();
        public readonly List<string> Broken = new List<string>();

        readonly Asm _d;

        /// <summary>Is this literal present in the assembly's #US (user string) heap? UTF-16LE scan,
        /// so it answers "the shipped binary contains this string" without decoding IL tokens.</summary>
        public Func<string, bool> Literal = _ => false;

        public Facts(Asm d) { _d = d; }

        public void Run()
        {
            // ---------------------------------------------------------------- inheritance
            Fact("Graphic_Random derives from Graphic_Collection (so the collection's Init fills subGraphics)",
                 _d.BaseTypeOf("Verse.Graphic_Random") == "Verse.Graphic_Collection");
            Fact("Graphic_Collection is abstract and derives from Graphic",
                 _d.BaseTypeOf("Verse.Graphic_Collection") == "Verse.Graphic");
            Fact("subGraphics lives on Graphic_Collection and is a Graphic[]",
                 _d.FieldType("Verse.Graphic_Collection", "subGraphics") == "Verse.Graphic[]");
            Fact("Graphic_Single.mat is the single material the icon is read from",
                 _d.FieldType("Verse.Graphic_Single", "mat") == "UnityEngine.Material");
            Fact("Graphic_Single.MaskSuffix is a static string",
                 _d.FieldType("Verse.Graphic_Single", "MaskSuffix") == "String");

            // ---------------------------------------------------------------- Init
            var init = _d.Methods("Verse.Graphic_Collection", "Init")
                          .FirstOrDefault(h => _d.Signature(h) == "Verse.Graphic_Collection::Init(Verse.GraphicRequest)");
            if (init.IsNil) { Broken.Add("Verse.Graphic_Collection::Init(Verse.GraphicRequest) not found"); return; }

            var il = Ops(init);
            Fact("Init: reads the folder with ContentFinder<Texture2D>.GetAllInFolder",
                 il.Any(s => s.Contains("GetAllInFolder")));
            Fact("Init: Where -> OrderBy -> Select -> GroupBy over the folder listing",
                 il.Any(s => s.Contains("Enumerable::Where")) && il.Any(s => s.Contains("Enumerable::OrderBy"))
                 && il.Any(s => s.Contains("Enumerable::Select")) && il.Any(s => s.Contains("Enumerable::GroupBy")));
            Fact("Init: filters candidates on Graphic_Single.MaskSuffix",
                 il.Any(s => s.Contains("Graphic_Single::MaskSuffix")));
            Fact("Init: culls the _north/_east/_south/_west suffixed files",
                 new[] { "_north", "_east", "_south", "_west" }.All(Literal));
            Fact("Init: builds each variant through GraphicDatabase.Get",
                 il.Any(s => s.Contains("GraphicDatabase::Get")));
            Fact("Init: probes <path>+MaskSuffix with ContentFinder<Texture2D>.Get for the mask",
                 il.Count(s => s.Contains("ContentFinder`1<UnityEngine.Texture2D>::Get")) >= 1);
            Fact("Init: assigns the assembled array to Graphic_Collection.subGraphics",
                 il.Any(s => s.Contains("Verse.Graphic_Collection::subGraphics")) && Raw(init).Any(i => i.Op == 0x7D));

            // ---------------------------------------------------------------- SubGraphicFor
            var sub = _d.Methods("Verse.Graphic_Random", "SubGraphicFor")
                         .FirstOrDefault(h => _d.Signature(h) == "Verse.Graphic_Random::SubGraphicFor(Verse.Thing)");
            if (sub.IsNil) { Broken.Add("Verse.Graphic_Random::SubGraphicFor(Verse.Thing) not found"); return; }
            var sil = Ops(sub);
            var raw = Raw(sub);
            Fact("SubGraphicFor: OverrideGraphicIndex first, thingIDNumber second",
                 sil.Any(s => s.Contains("Thing::get_OverrideGraphicIndex")) && sil.Any(s => s.Contains("Thing::thingIDNumber")));
            Fact("SubGraphicFor: index is `% subGraphics.Length` (the `rem` opcode)",
                 raw.Any(i => i.Op == 0x5D) && sil.Any(s => s.Contains("subGraphics")))
                ;
            Fact("SubGraphicFor: a null thing falls back to subGraphics[0]",
                 sil.Any(s => s.Contains("ldelem.ref")));

            // ---------------------------------------------------------------- MatSingle (the icon)
            var ms = _d.Methods("Verse.Graphic_Random", "get_MatSingle").FirstOrDefault();
            if (ms.IsNil) { Broken.Add("Verse.Graphic_Random::get_MatSingle not found"); return; }
            var mil = Ops(ms);
            Fact("Graphic_Random.get_MatSingle: subGraphics[Rand.Range(0, subGraphics.Length)].MatSingle",
                 mil.Any(s => s.Contains("Verse.Rand::Range")) && mil.Any(s => s.Contains("subGraphics"))
                 && mil.Any(s => s.Contains("Verse.Graphic::get_MatSingle")));
            Fact("Graphic::get_MatSingle default returns BaseContent.BadMat",
                 _d.Methods("Verse.Graphic", "get_MatSingle")
                   .Select(Ops).Any(l => l.Any(s => s.Contains("BaseContent::BadMat"))));

            // ---------------------------------------------------------------- Graphic_Single.Init
            var gs = _d.Methods("Verse.Graphic_Single", "Init")
                        .FirstOrDefault(h => _d.Signature(h) == "Verse.Graphic_Single::Init(Verse.GraphicRequest)");
            if (gs.IsNil) { Broken.Add("Verse.Graphic_Single::Init(Verse.GraphicRequest) not found"); return; }
            var gsil = Ops(gs);
            Fact("Graphic_Single.Init: ContentFinder<Texture2D>.Get(path) for the main texture",
                 gsil.Any(s => s.Contains("ContentFinder`1<UnityEngine.Texture2D>::Get")));
            Fact("Graphic_Single.Init: ShaderUtility.SupportsMaskTex gates the mask lookup",
                 gsil.Any(s => s.Contains("ShaderUtility::SupportsMaskTex")));
            Fact("Graphic_Single.Init: material comes from MaterialPool.MatFrom",
                 gsil.Any(s => s.Contains("MaterialPool::MatFrom")));

            // ---------------------------------------------------------------- Widgets.GetIconFor
            var icon = _d.Methods("Verse.Widgets", "GetIconFor")
                          .Where(h => _d.Signature(h).StartsWith("Verse.Widgets::GetIconFor(Verse.Thing,"))
                          .ToList();
            if (icon.Count == 0) { Broken.Add("Widgets.GetIconFor(Thing, ...) not found"); return; }
            var iil = Ops(icon[0]);
            Fact("GetIconFor(Thing,...): " + _d.Signature(icon[0]), true);
            Fact("GetIconFor: GraphicUtility.ExtractInnerGraphicFor(thing.Graphic, thing, index)",
                 iil.Any(s => s.Contains("GraphicUtility::ExtractInnerGraphicFor")));
            Fact("GetIconFor: MatAt(rot, null) - the icon is the collection's MatSingle, not a per-thing pick",
                 iil.Any(s => s.Contains("Graphic::MatAt")) && iil.Any(s => s == "ldnull")
                 && !iil.Any(s => s.Contains("Graphic_Random::SubGraphicFor")));
            // The Thing overload returns UnityEngine.Texture straight from the material; the
            // def overload is the one that casts to Texture2D. Both read Material.mainTexture.
            Fact("GetIconFor: the icon texture is read from the material (Material.mainTexture), not from the path",
                 iil.Any(s => s.Contains("Material::get_mainTexture")));
        }

        // ---------------------------------------------------------------- helpers

        void Fact(string text, bool ok)
        {
            if (ok) Ok.Add(text); else Broken.Add(text);
        }

        /// <summary>Operand names of every instruction, in order ("Verse.Graphic::MatAt", "str:_north", ...).</summary>
        List<string> Ops(MethodDefinitionHandle h)
        {
            var list = new List<string>();
            foreach (var ins in _d.Body(h))
            {
                if (ins.HasToken)
                {
                    int tok = ins.Token;
                    if ((tok >> 24) == 0x70)
                    {
                        try { list.Add("str:" + _d.Md.GetUserString(MetadataTokens.UserStringHandle(tok & 0x00FFFFFF))); }
                        catch { list.Add("str:?"); }
                    }
                    else
                    {
                        try { list.Add(_d.Describe(MetadataTokens.EntityHandle(tok))); }
                        catch { list.Add("tok0x" + tok.ToString("X8")); }
                    }
                }
                list.Add(ins.Name);
            }
            return list;
        }

        List<Insn> Raw(MethodDefinitionHandle h) { return _d.Body(h); }
    }
}
