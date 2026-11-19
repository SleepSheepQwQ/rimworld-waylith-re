using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace IconFlow
{
    /// <summary>
    /// Everything the replica is allowed to assume, read out of the shipped binary instead of
    /// being typed in by hand: the mask suffix, the grouping character, the path separator, the
    /// direction markers, and the shape of Init / SubGraphicFor / GetIconFor. If the dll and this
    /// file ever disagree the run is [BROKEN], not [FAIL] - a broken harness must not report green.
    /// </summary>
    sealed class FlowFacts
    {
        public string DllPath;
        public string AssemblyVersion;

        // replica inputs
        public string MaskSuffix;
        public char GroupSeparator;
        public string PathSeparator;
        public List<string> DirectionMarkers = new List<string>();

        public List<string> Evidence = new List<string>();
        public List<string> Problems = new List<string>();

        public bool Ok => Problems.Count == 0;

        public static FlowFacts Read(string asmPath)
        {
            var f = new FlowFacts { DllPath = asmPath };
            var dll = Dll.Open(asmPath);
            try { f.AssemblyVersion = dll.Md.GetAssemblyDefinition().Version.ToString(); } catch { f.AssemblyVersion = "?"; }

            // ---- constants ---------------------------------------------------------------
            f.MaskSuffix = dll.StaticString("Verse.Graphic_Single", "MaskSuffix");
            if (string.IsNullOrEmpty(f.MaskSuffix)) f.Problems.Add("Verse.Graphic_Single::MaskSuffix is not a string constant");
            else f.Evidence.Add("Graphic_Single.MaskSuffix = \"" + f.MaskSuffix + "\"  (static string field, read from its type initializer in " + System.IO.Path.GetFileName(asmPath) + ")");

            string subType = dll.FieldType("Verse.Graphic_Collection", "subGraphics");
            if (subType != "Verse.Graphic[]") f.Problems.Add("Graphic_Collection::subGraphics is '" + (subType ?? "missing") + "', expected Verse.Graphic[]");
            else f.Evidence.Add("Graphic_Collection.subGraphics : Verse.Graphic[]  (Init ends with ToArray + stfld)");

            var init = dll.Methods("Verse.Graphic_Collection", "Init").FirstOrDefault();
            if (init.Equals(default(MethodDefinitionHandle))) { f.Problems.Add("Verse.Graphic_Collection::Init not found"); return f; }
            var il = dll.BodyWithClosures(init);
            var calls = Calls(dll, il);
            var literals = Literals(dll, il);

            // grouping character: 'ldc.i4.s 95' right before 'String::Split(char, StringSplitOptions)'
            int splitAt = il.FindIndex(i => i.Name == "callvirt" && Describe(dll, i) == "System.String::Split");
            if (splitAt < 0) f.Problems.Add("Init has no String::Split call (variant grouping key)");
            else
            {
                var ch = il.Take(splitAt).Reverse().Take(4).FirstOrDefault(i => i.Name == "ldc.i4.s" || i.Name == "ldc.i4");
                if (ch == null || !ch.HasImm) f.Problems.Add("could not read the character passed to String::Split");
                else
                {
                    f.GroupSeparator = (char)ch.Imm;
                    f.Evidence.Add("grouping key = name.Split('" + f.GroupSeparator + "')[0]  (ldc.i4.s " + ch.Imm + " before String::Split)");
                }
            }

            // path separator: the ldstr used by String::Concat(path, sep, name)
            if (!literals.Contains("/")) f.Problems.Add("Init has no \"/\" literal for the sub-graphic path join");
            else
            {
                f.PathSeparator = "/";
                f.Evidence.Add("sub-graphic path = req.path + \"/\" + name   (String::Concat(string,string,string), 2 sites: per-texture and per-group)");
            }

            // direction markers: every "_word" literal in Init (Graphic_Multi per-rotation naming)
            string maskLiteral = f.MaskSuffix == null ? null : "_" + f.MaskSuffix.TrimStart('_');
            f.DirectionMarkers = literals.Where(s => !string.IsNullOrEmpty(s) && s.Length >= 2 && s[0] == '_'
                                                     && s.Skip(1).All(char.IsLetter)
                                                     && !string.Equals(s, maskLiteral, StringComparison.Ordinal))
                                          .Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList();
            if (f.DirectionMarkers.Count != 4) f.Problems.Add("expected 4 direction markers in Init, found " + f.DirectionMarkers.Count + " (" + string.Join(",", f.DirectionMarkers) + ")");
            else f.Evidence.Add("direction markers = " + string.Join(" ", f.DirectionMarkers) + "  (ldstr literals; entries containing one are culled and replaced by one multi graphic)");

            // ---- shape of the pipeline ---------------------------------------------------
            Require(f, calls, "GetAllInFolder", "ContentFinder", "ContentFinder<Texture2D>.GetAllInFolder(req.path)");
            Require(f, calls, "EndsWith", "String", "where !x.name.EndsWith(MaskSuffix)");
            Require(f, calls, "Where", "Enumerable", "Enumerable.Where");
            Require(f, calls, "OrderBy", "Enumerable", "Enumerable.OrderBy");
            Require(f, calls, "Select", "Enumerable", "Enumerable.Select  (x -> (x, x.name.Split('_')[0]))");
            Require(f, calls, "ToList", "Enumerable", "Enumerable.ToList");
            Require(f, calls, "GroupBy", "Enumerable", "Enumerable.GroupBy  (by the split key)");
            Require(f, calls, "Get", "ContentFinder", "ContentFinder<Texture2D>.Get(path + MaskSuffix)  (mask probe)");
            Require(f, calls, "Get", "GraphicDatabase", "GraphicDatabase.Get  (Graphic_Single / Graphic_Multi construction)");
            Require(f, calls, "get_SingleGraphicType", "Graphic_Collection", "SingleGraphicType");
            Require(f, calls, "get_MultiGraphicType", "Graphic_Collection", "MultiGraphicType");
            Require(f, calls, "ToArray", "List", "subGraphics = list.ToArray()");

            if (!il.Any(i => i.Name == "stfld" && Describe(dll, i) == "Verse.Graphic_Collection::subGraphics"))
                f.Problems.Add("Init never assigns Graphic_Collection::subGraphics");

            // ---- the selector ------------------------------------------------------------
            var sub = dll.Methods("Verse.Graphic_Random", "SubGraphicFor").FirstOrDefault();
            if (sub.Equals(default(MethodDefinitionHandle))) f.Problems.Add("Verse.Graphic_Random::SubGraphicFor not found");
            else
            {
                var sil = dll.BodyWithClosures(sub);
                bool id = sil.Any(i => i.Name == "ldfld" && Describe(dll, i) == "Verse.Thing::thingIDNumber");
                bool rem = sil.Any(i => i.Name == "rem");
                bool len = sil.Any(i => i.Name == "ldlen");
                bool arr = sil.Any(i => i.Name == "ldfld" && Describe(dll, i) == "Verse.Graphic_Collection::subGraphics");
                if (!(id && rem && len && arr))
                    f.Problems.Add("SubGraphicFor is not (thingIDNumber % subGraphics.Length): thingIDNumber=" + id + " rem=" + rem + " ldlen=" + len + " subGraphics=" + arr);
                else
                    f.Evidence.Add("SubGraphicFor = subGraphics[(OverrideGraphicIndex ?? thingIDNumber) % subGraphics.Length]");
            }

            // ---- def -> graphic -> material -> texture -----------------------------------
            var extract = dll.Methods("Verse.GraphicUtility", "ExtractInnerGraphicFor").FirstOrDefault();
            if (extract.Equals(default(MethodDefinitionHandle))) f.Problems.Add("Verse.GraphicUtility::ExtractInnerGraphicFor not found");
            else
            {
                var eil = dll.BodyWithClosures(extract);
                bool isRandom = eil.Any(i => i.Name == "isinst" && Describe(dll, i) == "Verse.Graphic_Random");
                bool subFor = eil.Any(i => i.Name == "callvirt" && Describe(dll, i) == "Verse.Graphic_Random::SubGraphicFor");
                if (!(isRandom && subFor)) f.Problems.Add("ExtractInnerGraphicFor does not dispatch Graphic_Random -> SubGraphicFor (isinst=" + isRandom + ", call=" + subFor + ")");
                else f.Evidence.Add("ExtractInnerGraphicFor: Graphic_Random -> SubGraphicFor(thing)");
            }

            var icon = dll.Methods("Verse.Widgets", "GetIconFor")
                          .FirstOrDefault(h => dll.MethodSignature(h).StartsWith("Verse.Widgets::GetIconFor(Verse.Thing,", StringComparison.Ordinal));
            if (icon.Equals(default(MethodDefinitionHandle))) f.Problems.Add("Widgets.GetIconFor(Thing,...) not found");
            else
            {
                var gil = dll.BodyWithClosures(icon);
                var gc = Calls(dll, gil);
                bool ex = gc.Any(c => c.Contains("GraphicUtility::ExtractInnerGraphicFor"));
                bool mat = gc.Any(c => c.Contains("Verse.Graphic::MatAt"));
                bool tex = gc.Any(c => c.Contains("Material::get_mainTexture"));
                if (!(ex && mat && tex))
                    f.Problems.Add("GetIconFor(Thing) is not ExtractInnerGraphicFor -> MatAt -> mainTexture (extract=" + ex + " matAt=" + mat + " mainTexture=" + tex + ")");
                else
                    f.Evidence.Add("Widgets.GetIconFor(Thing) = thing.Graphic.ExtractInnerGraphicFor(thing).MatAt(rot).mainTexture");
            }

            return f;
        }

        static void Require(FlowFacts f, List<string> calls, string member, string declFragment, string label)
        {
            bool ok = calls.Any(c => TrimGeneric(c).EndsWith("::" + member, StringComparison.Ordinal)
                                     && c.IndexOf(declFragment, StringComparison.Ordinal) >= 0);
            if (!ok) f.Problems.Add("Init is missing " + label);
            else f.Evidence.Add("Init contains " + label);
        }

        static string TrimGeneric(string call) => call.EndsWith("<>", StringComparison.Ordinal) ? call.Substring(0, call.Length - 2) : call;

        static string Describe(Dll dll, Insn i)
        {
            if (!i.HasToken) return "";
            if ((i.Token >> 24) == 0x70) return "\"" + dll.UserString(i.Token) + "\"";
            try { return dll.Describe(MetadataTokens.EntityHandle(i.Token)); } catch { return ""; }
        }

        static List<string> Calls(Dll dll, List<Insn> il)
        {
            var list = new List<string>();
            foreach (var i in il)
            {
                if (!i.HasToken || (i.Token >> 24) == 0x70) continue;
                if (i.Name == "call" || i.Name == "callvirt" || i.Name == "newobj" || i.Name == "ldftn")
                    list.Add(Describe(dll, i));
            }
            return list;
        }

        static List<string> Literals(Dll dll, List<Insn> il)
        {
            var list = new List<string>();
            foreach (var i in il)
                if (i.Name == "ldstr" && i.HasToken) list.Add(dll.UserString(i.Token));
            return list;
        }
    }
}
