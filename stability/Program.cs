using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using IconExec;

namespace Stability
{
    /// <summary>
    /// Reads dlls/Assembly-CSharp.dll and answers, from the IL only:
    ///
    ///   1. is Rand state shared/static, and is it reset by the UI?
    ///   2. when is Thing.overrideGraphicIndex written?
    ///   3. does Widgets.ThingIcon cache GetIconFor?
    ///   4. which icon path actually reaches Graphic_Random.MatSingle's Rand.Range?
    ///
    /// Nothing in this file executes game code. A build whose IL no longer has this shape
    /// exits 1, so the assertion cannot silently pass against a different dll.
    /// </summary>
    static class Program
    {
        static Asm A;
        static readonly List<string> Fails = new List<string>();
        static readonly Dictionary<int, List<Insn>> BodyCache = new Dictionary<int, List<Insn>>();
        static readonly Dictionary<int, HashSet<string>> CallCache = new Dictionary<int, HashSet<string>>();
        static List<MethodDefinitionHandle> AllMethods;

        static int Main(string[] argv)
        {
            string dir = null;
            for (int i = 0; i < argv.Length - 1; i++) if (argv[i] == "--dlls") dir = argv[i + 1];
            if (dir == null) dir = "dlls";
            string dll = System.IO.Path.Combine(dir, "Assembly-CSharp.dll");
            if (!System.IO.File.Exists(dll))
            {
                Console.WriteLine("[STABFACT] FAIL dll.missing " + dll);
                return 1;
            }

            A = Asm.Open(dll);
            Console.WriteLine("[STABFACT] INFO assembly " + A.Md.GetAssemblyDefinition().Version
                              + " mvid=" + A.Md.GetGuid(A.Md.GetModuleDefinition().Mvid));
            AllMethods = new List<MethodDefinitionHandle>();
            foreach (var th in A.Md.TypeDefinitions)
                foreach (var mh in A.Md.GetTypeDefinition(th).GetMethods())
                    AllMethods.Add(mh);

            try
            {
                RandFacts();
                GraphicFacts();
                WidgetFacts();
                ThingFacts();
                CallerFacts();
            }
            catch (Exception ex)
            {
                Console.WriteLine("[STABFACT] FAIL harness.exception " + ex.GetType().Name + ": " + ex.Message);
                Console.WriteLine(ex.StackTrace);
                return 1;
            }

            Console.WriteLine("[STABFACT] INFO methods.scanned=" + AllMethods.Count
                              + " failures=" + Fails.Count);
            Console.WriteLine("RESULT: " + (Fails.Count == 0 ? "PASS" : "FAIL"));
            return Fails.Count == 0 ? 0 : 1;
        }

        // ------------------------------------------------------------------ 1. Rand

        static void RandFacts()
        {
            string seedType = A.FieldType("Verse.Rand", "seed");
            string iterType = A.FieldType("Verse.Rand", "iterations");
            string stackType = A.FieldType("Verse.Rand", "stateStack");
            Fact("rand.seed static UInt32", seedType == "UInt32" && IsStatic("Verse.Rand", "seed"), seedType);
            Fact("rand.iterations static UInt32", iterType == "UInt32" && IsStatic("Verse.Rand", "iterations"), iterType);
            Fact("rand.stateStack static Stack<UInt64>", stackType != null && stackType.Contains("Stack") && stackType.Contains("UInt64")
                 && IsStatic("Verse.Rand", "stateStack"), stackType);
            Dump("Rand.cctor", A.Method("Verse.Rand", ".cctor"));
            Dump("Rand.get_Int", A.Method("Verse.Rand", "get_Int"));

            var getInt = Body(A.Method("Verse.Rand", "get_Int"));
            Fact("Rand.Int = MurmurHash(seed, iterations++) on the shared static pair",
                 Has(getInt, "Verse.Rand::seed") && Has(getInt, "Verse.Rand::iterations")
                 && getInt.Any(i => i.Name == "stsfld" && Describe(i).Contains("Verse.Rand::iterations"))
                 && Has(getInt, "Verse.MurmurHash::GetInt"),
                 "no per-call RNG object; the counter is a static field");

            var range = Find("Verse.Rand", "Range", h => A.Signature(h).Contains("Int32,Int32"));
            Fact("Rand.Range(int,int) calls Rand.Int", !range.Equals(default(MethodDefinitionHandle)) && Has(Body(range), "Verse.Rand::get_Int"));
            Dump("Rand.Range(int,int)", range);

            var writers = AllMethods
                .Where(h => Body(h).Any(i => i.Name == "stsfld" && Describe(i) == "Verse.Rand::iterations"))
                .Select(Disp).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
            Fact("iterations is written only by Verse.Rand itself",
                 writers.Count > 0 && writers.All(w => w.StartsWith("Verse.Rand::", StringComparison.Ordinal)),
                 string.Join(", ", writers));
            Console.WriteLine("[STABFACT] INFO rand.iterations.writers=" + string.Join(",", writers));

            var uiReset = writers.Where(w => w.Contains("Widgets") || w.Contains("MainTabWindow") || w.Contains("Designator")
                                             || w.Contains("UIRoot") || w.Contains("ThingIcon")).ToList();
            Fact("no UI type resets Rand.iterations", uiReset.Count == 0, string.Join(",", uiReset));

            var pushers = AllMethods
                .Where(h => Body(h).Any(i => (i.Name == "call" || i.Name == "callvirt")
                                             && (Describe(i) == "Verse.Rand::PushState" || Describe(i) == "Verse.Rand::PopState")))
                .Select(Disp).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
            Console.WriteLine("[STABFACT] INFO rand.pushpop.callers=" + pushers.Count + " " + string.Join(",", pushers.Take(24)));
            Fact("no Widgets method pushes/pops Rand state",
                 !pushers.Any(p => p.StartsWith("Verse.Widgets::", StringComparison.Ordinal)),
                 string.Join(",", pushers.Where(p => p.StartsWith("Verse.Widgets::", StringComparison.Ordinal))));
        }

        // ------------------------------------------------------------------ 2. graphics

        static void GraphicFacts()
        {
            var ms = Find("Verse.Graphic_Random", "get_MatSingle");
            var msIl = Body(ms);
            Fact("Graphic_Random.MatSingle = subGraphics[Rand.Range(0,len)].MatSingle",
                 Has(msIl, "Verse.Rand::Range") && Has(msIl, "subGraphics") && Has(msIl, "Verse.Graphic::get_MatSingle"));
            Dump("Graphic_Random.get_MatSingle", ms);

            var sub = Find("Verse.Graphic_Random", "SubGraphicFor");
            var subIl = Body(sub);
            Fact("Graphic_Random.SubGraphicFor = (OverrideGraphicIndex ?? thingIDNumber) % len; null -> [0]",
                 Has(subIl, "Verse.Thing::get_OverrideGraphicIndex") && Has(subIl, "Verse.Thing::thingIDNumber")
                 && subIl.Any(i => i.Name == "rem") && Has(subIl, "subGraphics"));
            Fact("Graphic_Random.SubGraphicFor never calls Rand", !Has(subIl, "Verse.Rand::Range"));
            Dump("Graphic_Random.SubGraphicFor", sub);

            var matAt = Find("Verse.Graphic_Random", "MatAt");
            var matAtIl = Body(matAt);
            Fact("Graphic_Random.MatAt: null thing -> MatSingle (Rand); non-null -> MatSingleFor(thing)",
                 Has(matAtIl, "Verse.Graphic::get_MatSingle") && Has(matAtIl, "Verse.Graphic_Random::MatSingleFor"));
            Dump("Graphic_Random.MatAt", matAt);

            var ex = Find("Verse.GraphicUtility", "ExtractInnerGraphicFor");
            var exAll = BodyWithClosures(ex);
            Fact("ExtractInnerGraphicFor: Graphic_Random -> SubGraphicAtIndex(index) or SubGraphicFor(thing), never MatSingle/Rand",
                 Has(exAll, "isinst") && Has(exAll, "get_HasValue") && Has(exAll, "SubGraphicAtIndex")
                 && Has(exAll, "Verse.Graphic_Random::SubGraphicFor") && !Has(exAll, "Verse.Rand::Range"));
            Dump("GraphicUtility.ExtractInnerGraphicFor", ex);
            foreach (var g in AllMethods.Where(h => A.Md.GetString(A.Md.GetMethodDefinition(h).Name).Contains("ResolveGraphicInner")))
                Dump("GraphicUtility.local ResolveGraphicInner", g);
        }

        // ------------------------------------------------------------------ 3. Widgets

        static void WidgetFacts()
        {
            var gi = Find("Verse.Widgets", "GetIconFor", h => A.Signature(h).StartsWith("Verse.Widgets::GetIconFor(Verse.Thing,", StringComparison.Ordinal));
            var giIl = Body(gi);
            var exIl = BodyWithClosures(Find("Verse.GraphicUtility", "ExtractInnerGraphicFor"));
            var subIl = Body(Find("Verse.Graphic_Random", "SubGraphicFor"));
            var chain = giIl.Concat(exIl).Concat(subIl).ToList();
            int exAt = giIl.FindIndex(i => (i.Name == "call" || i.Name == "callvirt")
                                           && Describe(i).Contains("Verse.GraphicUtility::ExtractInnerGraphicFor"));
            bool nullIndex = exAt > 0 && giIl.Skip(Math.Max(0, exAt - 9)).Take(9).Any(i => i.Name == "initobj");

            Fact("GetIconFor(Thing) calls ExtractInnerGraphicFor(thing.Graphic, thing, null index override)",
                 exAt >= 0 && nullIndex, "initobj before the call at IL_" + (exAt >= 0 ? giIl[exAt].Offset.ToString("X4") : "-"));
            Fact("GetIconFor(Thing) never calls Rand.Range or Graphic_Random.get_MatSingle",
                 !Has(chain, "Verse.Rand::Range") && !Has(chain, "Verse.Graphic_Random::get_MatSingle"));
            Dump("Widgets.GetIconFor(Thing,...)", gi, 520);

            var td = Find("Verse.Widgets", "ThingIcon", h => A.Signature(h).StartsWith("Verse.Widgets::ThingIcon(UnityEngine.Rect,Verse.ThingDef,", StringComparison.Ordinal));
            var tdIl = Body(td);
            int firstRet = tdIl.FindIndex(i => i.Name == "ret");
            int firstGet = tdIl.FindIndex(i => (i.Name == "call" || i.Name == "callvirt") && Describe(i).Contains("Verse.Widgets::GetIconFor"));
            Fact("ThingIcon(def): uiIcon == null / BadTex returns before any drawing",
                 firstRet >= 0 && firstGet > firstRet && Has(tdIl, "BuildableDef::uiIcon") && Has(tdIl, "BaseContent::BadTex"));
            Fact("ThingIcon(def) stores no cache", !tdIl.Any(i => i.Name == "stsfld"));
            Dump("Widgets.ThingIcon(def,...) head", td, 40);

            var tt = Find("Verse.Widgets", "ThingIcon", h => A.Signature(h).StartsWith("Verse.Widgets::ThingIcon(UnityEngine.Rect,Verse.Thing,", StringComparison.Ordinal));
            var ttIl = Body(tt);
            Fact("ThingIcon(thing) calls GetIconFor(thing) every time and stores no cache",
                 Has(ttIl, "Verse.Widgets::GetIconFor") && !ttIl.Any(i => i.Name == "stsfld"));
            Dump("Widgets.ThingIcon(thing,...) head", tt, 45);

            var fields = new List<string>();
            foreach (var th in A.Md.TypeDefinitions)
            {
                if (A.TypeName(th) != "Verse.Widgets") continue;
                foreach (var fh in A.Md.GetTypeDefinition(th).GetFields())
                {
                    var fd = A.Md.GetFieldDefinition(fh);
                    string t = null;
                    try { t = fd.DecodeSignature(new FieldSig(), null); } catch { }
                    fields.Add(A.Md.GetString(fd.Name) + ":" + (t ?? "?"));
                }
            }
            Console.WriteLine("[STABFACT] INFO widgets.fields=" + string.Join(",", fields));
            Fact("Widgets has no field that caches an icon by def/thing",
                 !fields.Any(f => f.IndexOf("Icon", StringComparison.OrdinalIgnoreCase) >= 0
                                  && f.IndexOf("Cache", StringComparison.OrdinalIgnoreCase) >= 0));
        }

        // ------------------------------------------------------------------ 4. Thing / BuildableDef

        static void ThingFacts()
        {
            string ft = A.FieldType("Verse.Thing", "overrideGraphicIndex");
            Fact("Thing.overrideGraphicIndex is Nullable<int>", ft != null && ft.Contains("Nullable") && ft.Contains("Int32"), ft);
            var get = A.Method("Verse.Thing", "get_OverrideGraphicIndex");
            Fact("Thing.OverrideGraphicIndex getter returns the field", Has(Body(get), "Verse.Thing::overrideGraphicIndex"));
            Dump("Thing.get_OverrideGraphicIndex", get);

            var writers = AllMethods
                .Where(h => Body(h).Any(i => i.Name == "stfld" && Describe(i) == "Verse.Thing::overrideGraphicIndex"))
                .Select(Disp).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
            Console.WriteLine("[STABFACT] INFO overrideGraphicIndex.writers=" + string.Join(",", writers));
            Fact("overrideGraphicIndex is written by gameplay systems, not by MakeThing/SpawnSetup",
                 writers.Count >= 5
                 && !writers.Any(w => w.Contains("Verse.Thing::MakeThing") || w.Contains("Verse.Thing::SpawnSetup")
                                      || w.Contains("Verse.GenSpawn::Spawn")));
            Fact("overrideGraphicIndex is scribed (ExposeData takes its address)",
                 Body(A.Method("Verse.Thing", "ExposeData")).Any(i => i.Name == "ldflda" && Describe(i) == "Verse.Thing::overrideGraphicIndex"));

            var ri = Find("Verse.BuildableDef", "ResolveIcon");
            var riIl = Body(ri);
            Fact("BuildableDef.ResolveIcon: ExtractInnerGraphicFor(null).MatAt, uiIcon = material.mainTexture",
                 Has(riIl, "Verse.GraphicUtility::ExtractInnerGraphicFor") && Has(riIl, "Verse.Graphic::MatAt")
                 && Has(riIl, "UnityEngine.Material::get_mainTexture")
                 && riIl.Any(i => i.Name == "stfld" && Describe(i) == "Verse.BuildableDef::uiIcon"));
            Dump("BuildableDef.ResolveIcon", ri);

            var pl = Find("Verse.BuildableDef", "PostLoad");
            var plAll = BodyWithClosures(pl);
            Fact("BuildableDef.PostLoad schedules ResolveIcon once via LongEventHandler.ExecuteWhenFinished",
                 Has(plAll, "Verse.LongEventHandler::ExecuteWhenFinished") && Has(plAll, "ResolveIcon"));
        }

        // ------------------------------------------------------------------ 5. callers

        static void CallerFacts()
        {
            var rangeCallers = AllMethods
                .Where(h => Body(h).Any(i => (i.Name == "call" || i.Name == "callvirt") && Describe(i) == "Verse.Rand::Range"))
                .Select(Disp).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
            Console.WriteLine("[STABFACT] INFO rand.range.callers=" + rangeCallers.Count + " " + string.Join(",", rangeCallers));
            Fact("Widgets icon methods are not Rand.Range callers",
                 !rangeCallers.Any(c => c.StartsWith("Verse.Widgets::GetIconFor", StringComparison.Ordinal)
                                        || c.StartsWith("Verse.Widgets::ThingIcon", StringComparison.Ordinal)));

            var matSingleCallers = AllMethods
                .Where(h => Body(h).Any(i => (i.Name == "call" || i.Name == "callvirt") && Describe(i) == "Verse.Graphic_Random::get_MatSingle"))
                .Select(Disp).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
            Console.WriteLine("[STABFACT] INFO graphic_random.matsingle.callers=" + matSingleCallers.Count + " " + string.Join(",", matSingleCallers));
            Fact("Widgets.GetIconFor is not a Graphic_Random.MatSingle caller",
                 !matSingleCallers.Any(c => c.StartsWith("Verse.Widgets::GetIconFor", StringComparison.Ordinal)));
        }

        // ------------------------------------------------------------------ helpers

        static MethodDefinitionHandle Find(string type, string name, Func<MethodDefinitionHandle, bool> pred = null)
        {
            foreach (var h in A.Methods(type, name))
                if (pred == null || pred(h)) return h;
            return default(MethodDefinitionHandle);
        }

        static List<Insn> Body(MethodDefinitionHandle h)
        {
            int t = MetadataTokens.GetToken(h);
            if (BodyCache.TryGetValue(t, out var cached)) return cached;
            List<Insn> il;
            try { il = A.Body(h); } catch { il = new List<Insn>(); }
            BodyCache[t] = il;
            return il;
        }

        static List<Insn> BodyWithClosures(MethodDefinitionHandle root)
        {
            var all = new List<Insn>();
            var seen = new HashSet<int>();
            Walk(root, 0);
            return all;

            void Walk(MethodDefinitionHandle h, int depth)
            {
                if (depth > 3 || !seen.Add(MetadataTokens.GetToken(h))) return;
                var il = Body(h);
                all.AddRange(il);
                foreach (var i in il)
                {
                    if (!i.HasToken || (i.Token >> 24) == 0x70) continue;
                    if (i.Name != "call" && i.Name != "callvirt" && i.Name != "ldftn") continue;
                    EntityHandle eh;
                    try { eh = MetadataTokens.EntityHandle(i.Token); } catch { continue; }
                    if (eh.Kind == HandleKind.MethodSpecification)
                        eh = A.Md.GetMethodSpecification((MethodSpecificationHandle)eh).Method;
                    if (eh.Kind != HandleKind.MethodDefinition) continue;
                    var mh = (MethodDefinitionHandle)eh;
                    string nm = A.Md.GetString(A.Md.GetMethodDefinition(mh).Name);
                    if (nm.Contains("b__") || nm.Contains("g__")) Walk(mh, depth + 1);
                }
            }
        }

        static string Describe(Insn i)
        {
            if (!i.HasToken) return "";
            if ((i.Token >> 24) == 0x70) return "ldstr";
            try { return A.Describe(MetadataTokens.EntityHandle(i.Token)); }
            catch { return "token"; }
        }

        static string Operand(Insn i)
        {
            if (i.HasToken)
            {
                if ((i.Token >> 24) == 0x70)
                {
                    try { return "\"" + A.Md.GetUserString(MetadataTokens.UserStringHandle(i.Token & 0x00FFFFFF)) + "\""; }
                    catch { return "str?"; }
                }
                return Describe(i);
            }
            if (i.HasImm) return i.Imm.ToString();
            if (i.Target >= 0) return "-> IL_" + i.Target.ToString("X4");
            return "";
        }

        static bool Has(List<Insn> il, string token)
        {
            return il.Any(i => i.HasToken && Describe(i).IndexOf(token, StringComparison.Ordinal) >= 0);
        }

        static bool IsStatic(string type, string field)
        {
            foreach (var th in A.Md.TypeDefinitions)
            {
                if (A.TypeName(th) != type) continue;
                foreach (var fh in A.Md.GetTypeDefinition(th).GetFields())
                {
                    var fd = A.Md.GetFieldDefinition(fh);
                    if (A.Md.GetString(fd.Name) == field) return (fd.Attributes & FieldAttributes.Static) != 0;
                }
            }
            return false;
        }

        static string Disp(MethodDefinitionHandle h)
        {
            var md = A.Md.GetMethodDefinition(h);
            return A.TypeName(md.GetDeclaringType()) + "::" + A.Md.GetString(md.Name);
        }

        static void Dump(string label, MethodDefinitionHandle h, int max = 220)
        {
            if (h.Equals(default(MethodDefinitionHandle))) { Console.WriteLine("[IL] " + label + " <missing>"); return; }
            var md = A.Md.GetMethodDefinition(h);
            Console.WriteLine("[IL] " + label + "  " + A.Signature(h) + "  rva=0x" + md.RelativeVirtualAddress.ToString("X"));
            int n = 0;
            foreach (var i in Body(h))
            {
                Console.WriteLine("     IL_" + i.Offset.ToString("X4") + "  " + i.Name + " " + Operand(i));
                if (++n >= max) { Console.WriteLine("     ... (" + Body(h).Count + " instructions)"); break; }
            }
        }

        static void Fact(string what, bool ok, string detail = "")
        {
            Console.WriteLine("[STABFACT] " + (ok ? "PASS " : "FAIL ") + what
                              + (string.IsNullOrEmpty(detail) ? "" : "  (" + detail + ")"));
            if (!ok) Fails.Add(what);
        }

        sealed class FieldSig : ISignatureTypeProvider<string, object>
        {
            public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[]";
            public string GetByReferenceType(string elementType) => "ref " + elementType;
            public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";
            public string GetGenericInstantiation(string genericType, System.Collections.Immutable.ImmutableArray<string> typeArguments)
                => genericType + "<" + string.Join(",", typeArguments) + ">";
            public string GetGenericMethodParameter(object genericContext, int index) => "!!" + index;
            public string GetGenericTypeParameter(object genericContext, int index) => "!" + index;
            public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
            public string GetPinnedType(string elementType) => elementType;
            public string GetPointerType(string elementType) => elementType + "*";
            public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
            public string GetSZArrayType(string elementType) => elementType + "[]";
            public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
            {
                var td = reader.GetTypeDefinition(handle);
                string ns = reader.GetString(td.Namespace), n = reader.GetString(td.Name);
                return string.IsNullOrEmpty(ns) ? n : ns + "." + n;
            }
            public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
            {
                var tr = reader.GetTypeReference(handle);
                string ns = reader.GetString(tr.Namespace), n = reader.GetString(tr.Name);
                return string.IsNullOrEmpty(ns) ? n : ns + "." + n;
            }
            public string GetTypeFromSpecification(MetadataReader reader, object genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
                => "typespec";
        }
    }
}
