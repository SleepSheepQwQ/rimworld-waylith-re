using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace MatPool
{
    /// <summary>
    /// What the shipped dll says about the material cache, read with metadata + IL only.
    ///
    /// These are not comments: each one is asserted against dlls/Assembly-CSharp.dll at run time.
    /// A build whose MaterialPool keys on something else turns the run BROKEN (exit 2) instead of
    /// letting the scenarios below "pass" against a method that no longer has this shape.
    /// </summary>
    sealed class Facts
    {
        public readonly List<string> Ok = new List<string>();
        public readonly List<string> Broken = new List<string>();

        readonly Dll _d;

        public Facts(Dll d) { _d = d; }

        public void Run()
        {
            var mp = _d.Methods("Verse.MaterialPool", "MatFrom").ToList();
            var matFrom = mp.FirstOrDefault(h => _d.MethodSignature(h).EndsWith("MatFrom(Verse.MaterialRequest)"));
            if (matFrom.IsNil) { Broken.Add("Verse.MaterialPool::MatFrom(Verse.MaterialRequest) not found"); return; }
            var il = Steps(matFrom);

            // ---------------------------------------------------------------- the two tables
            Fact("matDictionary : Dictionary<MaterialRequest, Material>",
                 _d.FieldType("Verse.MaterialPool", "matDictionary") == "System.Collections.Generic.Dictionary`2<Verse.MaterialRequest,UnityEngine.Material>");
            Fact("matDictionaryReverse : Dictionary<Material, MaterialRequest>",
                 _d.FieldType("Verse.MaterialPool", "matDictionaryReverse") == "System.Collections.Generic.Dictionary`2<UnityEngine.Material,Verse.MaterialRequest>");
            Fact("shaderParameters is List<ShaderParameter>",
                 _d.FieldType("Verse.MaterialRequest", "shaderParameters") == "System.Collections.Generic.List`1<Verse.ShaderParameter>");

            // ---------------------------------------------------------------- MatFrom: lookup
            int iTry = Index(il, "TryGetValue");
            int iCreate = Index(il, "Verse.MaterialAllocator::Create");
            int iAdd = Index(il, "::Add");
            int iAddRev = Index(il, "Dictionary`2<UnityEngine.Material,Verse.MaterialRequest>::Add");
            Fact("MatFrom: matDictionary.TryGetValue before MaterialAllocator.Create before both Add() calls",
                 iTry >= 0 && iCreate > iTry && iAdd > iCreate && iAddRev > iAdd);

            // The mask is dropped when the shader cannot use it, *before* the key is built, so a
            // mask difference that cannot render must not create a second material.
            int iMaskProbe = Index(il, "Verse.ShaderUtility::SupportsMaskTex");
            int iMaskStore = Index(il, "stfld Verse.MaterialRequest::maskTex");
            Fact("MatFrom: maskTex is nulled on a shader without _MaskTex, before the lookup",
                 iMaskProbe >= 0 && iMaskStore > iMaskProbe && iTry > iMaskStore);

            // Colours are quantised through Color32 on the request copy before it is used as a key.
            int iQ1 = Index(il, "Verse.MaterialRequest::color");
            int iQ2 = Index(il, "Verse.MaterialRequest::colorTwo");
            int quant = Count(il, "UnityEngine.Color32::op_Implicit");
            Fact("MatFrom: key colours are quantised through Color32 (8 bit/channel) before the lookup",
                 quant == 4 && iQ1 >= 0 && iQ2 > iQ1 && iTry > iQ2);

            // ---------------------------------------------------------------- Equals
            var eq = _d.Methods("Verse.MaterialRequest", "Equals")
                        .FirstOrDefault(h => _d.MethodSignature(h) == "Verse.MaterialRequest::Equals(Verse.MaterialRequest)");
            if (eq.IsNil) { Broken.Add("MaterialRequest::Equals(MaterialRequest) not found"); return; }
            var eqIl = Steps(eq);
            var eqFields = FieldsTouched(eqIl);
            Fact("Equals compares shader, mainTex, color, colorTwo, maskTex, renderQueue, secondaryTex, shaderParameters",
                 Join(eqFields) == "shader,mainTex,color,colorTwo,maskTex,renderQueue,secondaryTex,shaderParameters");

            // The whole point of case D: the list is compared by reference (`ceq`), never by content.
            Fact("Equals compares shaderParameters with a bare `ceq` (reference identity), not List.Equals/SequenceEqual",
                 ComparesByReferenceOnly(eqIl, "shaderParameters")
                 && !eqIl.Any(s => s.Contains("ShaderParameter>::Equals") || s.Contains("SequenceEqual")));

            Fact("Equals ignores needsMainTex (a request flag that changes no pixel)",
                 !eqIl.Any(s => s.Contains("needsMainTex")));

            // ---------------------------------------------------------------- GetHashCode
            var gh = _d.Methods("Verse.MaterialRequest", "GetHashCode").FirstOrDefault();
            if (gh.IsNil) { Broken.Add("MaterialRequest::GetHashCode not found"); return; }
            var ghIl = Steps(gh);
            var ghFields = FieldsTouched(ghIl);
            Fact("GetHashCode hashes the same 8 fields as Equals (no field in one and not the other)",
                 Join(ghFields.OrderBy(x => x, StringComparer.Ordinal)) == Join(eqFields.OrderBy(x => x, StringComparer.Ordinal)));
            Fact("GetHashCode hashes shaderParameters through Gen.HashCombine (identity hash), not element-wise",
                 ghIl.Any(s => s.Contains("Verse.Gen::HashCombine") && PrevField(ghIl, "Verse.MaterialRequest::shaderParameters"))
                 && !ghIl.Any(s => s.Contains("ShaderParameter>::GetHashCode")));
            Fact("GetHashCode ignores needsMainTex",
                 !ghIl.Any(s => s.Contains("needsMainTex")));
        }

        public void RunUnity(string unityPath, string waylithPath)
        {
            if (!System.IO.File.Exists(unityPath)) { Broken.Add("UnityStubs.dll not found at " + unityPath); return; }
            var u = Dll.Open(unityPath);

            // ---------------------------------------------------------------- Destroy's two effects
            var destroy = One(u, "UnityEngine.Object", "Destroy");
            if (destroy.IsNil) { Broken.Add("UnityEngine.Object::Destroy not found"); return; }
            var dIl = Steps(u, destroy);
            Fact("Object.Destroy disposes the IDisposable and then sets _destroyed",
                 dIl.Any(s => s.Contains("System.IDisposable::Dispose"))
                 && dIl.Any(s => s.Contains("stfld UnityEngine.Object::_destroyed")));

            var texDispose = One(u, "UnityEngine.Texture", "Dispose", "Dispose(Boolean)");
            if (texDispose.IsNil) { Broken.Add("UnityEngine.Texture::Dispose(bool) not found"); return; }
            var tdIl = Steps(u, texDispose);
            Fact("Texture.Dispose(bool) calls the core Waylith texture Dispose and sets _disposed",
                 tdIl.Any(s => s.Contains("Waylith.Rendering.Texture::Dispose"))
                 && tdIl.Any(s => s.Contains("stfld UnityEngine.Texture::_disposed")));

            // Texture's operator decides null-ness by _disposed, not by Object's _destroyed.
            var texEq = One(u, "UnityEngine.Texture", "op_Equality");
            if (texEq.IsNil) { Broken.Add("UnityEngine.Texture::op_Equality not found"); return; }
            var teIl = Steps(u, texEq);
            Fact("Texture.op_Equality reads _disposed and never _destroyed (a Destroy()ed texture is fake-null)",
                 teIl.Any(s => s.Contains("ldfld UnityEngine.Texture::_disposed"))
                 && !teIl.Any(s => s.Contains("_destroyed")));

            var objEq = One(u, "UnityEngine.Object", "op_Equality");
            if (!objEq.IsNil)
            {
                var oeIl = Steps(u, objEq);
                Fact("Object.op_Equality reads _destroyed", oeIl.Any(s => s.Contains("ldfld UnityEngine.Object::_destroyed")));
            }
            else Broken.Add("UnityEngine.Object::op_Equality not found");

            // ---------------------------------------------------------------- the GPU/CPU split
            if (!System.IO.File.Exists(waylithPath)) { Broken.Add("Waylith.dll not found at " + waylithPath); return; }
            var w = Dll.Open(waylithPath);
            var coreDispose = One(w, "Waylith.Rendering.Texture", "Dispose");
            if (coreDispose.IsNil) { Broken.Add("Waylith.Rendering.Texture::Dispose not found"); return; }
            var cdIl = Steps(w, coreDispose);
            Fact("Waylith.Rendering.Texture.Dispose disposes GpuResource and nulls it, and does not touch CpuData",
                 cdIl.Any(s => s.Contains("get_GpuResource"))
                 && cdIl.Any(s => s.Contains("GpuTextureAllocation::Dispose"))
                 && cdIl.Any(s => s.Contains("set_GpuResource"))
                 && !cdIl.Any(s => s.Contains("CpuData")));

            // ---------------------------------------------------------------- ST routes
            var setVec = One(u, "UnityEngine.Material", "SetVector", "SetVector(Int32");
            var setScale = One(u, "UnityEngine.Material", "SetTextureScale", "SetTextureScale(Int32");
            var trySt = One(u, "UnityEngine.Material", "TrySetTextureST");
            var engineTransform = One(w, "Waylith.Rendering.Material", "SetTextureTransform", "SetTextureTransform(Int32");
            if (setVec.IsNil || setScale.IsNil || trySt.IsNil || engineTransform.IsNil)
                Broken.Add("Material.SetVector / SetTextureScale / TrySetTextureST / SetTextureTransform missing");
            else
            {
                var sv = Steps(u, setVec);
                var ss = Steps(u, setScale);
                var st = Steps(u, trySt);
                var et = Steps(w, engineTransform);
                Fact("Material.SetVector routes _ST names through TrySetTextureST",
                     sv.Any(s => s.Contains("TrySetTextureST")));
                Fact("Material.SetTextureScale writes the same textureTransforms slot that TrySetTextureST reaches",
                     ss.Any(s => s.Contains("GetTextureTransform"))
                     && ss.Any(s => s.Contains("SetTextureSTProperty"))
                     && st.Any(s => s.Contains("SetTextureTransform"))
                     && et.Any(s => s.Contains("textureTransforms"))
                     && et.Any(s => s.Contains("SetTextureSTProperty")));
            }
        }

        static MethodDefinitionHandle One(Dll d, string type, string method, string sig = null)
        {
            foreach (var h in d.Methods(type, method))
                if (sig == null || d.MethodSignature(h).Contains(sig)) return h;
            return default(MethodDefinitionHandle);
        }

        // ------------------------------------------------------------------ helpers

        void Fact(string what, bool holds)
        {
            if (holds) Ok.Add(what);
            else Broken.Add(what);
        }

        static List<string> Steps(Dll d, MethodDefinitionHandle h)
        {
            var list = new List<string>();
            foreach (var i in d.Body(h))
            {
                string s = i.Name;
                if (i.HasImm) s += " " + i.Imm;
                if (i.HasToken)
                {
                    try { s += " " + d.Describe(MetadataTokens.EntityHandle(i.Token)); }
                    catch { s += " tok"; }
                }
                list.Add(s);
            }
            return list;
        }

        List<string> Steps(MethodDefinitionHandle h) { return Steps(_d, h); }

        static int Index(List<string> il, string needle)
        {
            for (int i = 0; i < il.Count; i++) if (il[i].Contains(needle)) return i;
            return -1;
        }

        static int Count(List<string> il, string needle) { return il.Count(s => s.Contains(needle)); }

        // field names touched by ldfld, in first-appearance order
        static List<string> FieldsTouched(List<string> il)
        {
            var seen = new List<string>();
            foreach (var s in il)
            {
                if (!s.StartsWith("ldfld Verse.MaterialRequest::")) continue;
                string name = s.Substring("ldfld Verse.MaterialRequest::".Length).Trim();
                if (!seen.Contains(name)) seen.Add(name);
            }
            return seen;
        }

        static bool AdjacentPair(List<string> il, string a, string b, string then)
        {
            for (int i = 0; i + 2 < il.Count; i++)
                if (il[i].Contains(a) && il[i + 1].Contains(b) && il[i + 2].Trim() == then) return true;
            return false;
        }

        // `ldarg.1; ldfld X; ldarg.0; ldfld X; ceq` - the two loads of `field` are the operands of a
        // bare ceq, i.e. compared as object references and nothing else.
        static bool ComparesByReferenceOnly(List<string> il, string field)
        {
            for (int i = 0; i < il.Count; i++)
            {
                if (il[i].Trim() != "ceq") continue;
                var operands = new List<string>();
                for (int j = i - 1; j >= 0 && operands.Count < 2; j--)
                {
                    string s = il[j].Trim();
                    if (s.StartsWith("ldarg")) continue;
                    if (s.StartsWith("ldfld Verse.MaterialRequest::")) { operands.Add(s.Substring("ldfld Verse.MaterialRequest::".Length)); continue; }
                    break;
                }
                if (operands.Count == 2 && operands[0] == field && operands[1] == field) return true;
            }
            return false;
        }

        static bool PrevField(List<string> il, string field)
        {
            for (int i = 1; i < il.Count; i++)
                if (il[i].Contains("Gen::HashCombine") && il[i - 1].Contains(field)) return true;
            return false;
        }

        static string Join(IEnumerable<string> xs) { return string.Join(",", xs); }
    }
}
