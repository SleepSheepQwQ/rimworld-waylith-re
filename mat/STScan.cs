using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace MatPool
{
    /// <summary>
    /// Every `_MainTex_ST` writer in the shipped Assembly-CSharp, read from IL (no decompiled tree,
    /// no game). A write is one of:
    ///   Material.set_mainTextureScale / set_mainTextureOffset
    ///   Material.SetTextureScale / SetTextureOffset
    ///   Material.SetVector("&lt;something&gt;_ST", ...)
    /// The containing method is printed so the call site can be classified (pooled material vs a
    /// fresh clone). MaterialPool.MatFrom call sites are counted separately.
    /// </summary>
    sealed class STScan
    {
        sealed class Hit
        {
            public string Site;
            public string Api;
        }

        public static void Run(Dll asm)
        {
            var hits = new List<Hit>();
            var matFrom = new HashSet<string>(StringComparer.Ordinal);
            int methods = 0;

            foreach (var th in asm.Md.TypeDefinitions)
            {
                var td = asm.Md.GetTypeDefinition(th);
                string type = asm.TypeName(th);
                foreach (var mh in td.GetMethods())
                {
                    methods++;
                    string method = asm.Md.GetString(asm.Md.GetMethodDefinition(mh).Name);
                    string site = type + "::" + method;
                    var il = asm.BodyWithClosures(mh);

                    for (int i = 0; i < il.Count; i++)
                    {
                        if (!il[i].HasToken) continue;
                        EntityHandle h;
                        try { h = MetadataTokens.EntityHandle(il[i].Token); } catch { continue; }
                        string name;
                        try { name = asm.Describe(h); } catch { continue; }

                        if (name == "Verse.MaterialPool::MatFrom")
                        {
                            matFrom.Add(site);
                            continue;
                        }

                        string api = null;
                        if (name == "UnityEngine.Material::set_mainTextureScale") api = "mainTextureScale =";
                        else if (name == "UnityEngine.Material::set_mainTextureOffset") api = "mainTextureOffset =";
                        else if (name == "UnityEngine.Material::SetTextureScale") api = "SetTextureScale";
                        else if (name == "UnityEngine.Material::SetTextureOffset") api = "SetTextureOffset";
                        else if (name == "UnityEngine.Material::SetVector")
                        {
                            for (int j = i - 1; j >= 0 && j >= i - 4; j--)
                            {
                                if (il[j].Name != "ldstr" || !il[j].HasToken) continue;
                                string s;
                                try { s = asm.UserString(il[j].Token); } catch { s = null; }
                                if (s != null && s.EndsWith("_ST", StringComparison.Ordinal))
                                {
                                    api = "SetVector(\"" + s + "\")";
                                    break;
                                }
                            }
                        }
                        if (api != null) hits.Add(new Hit { Site = site, Api = api });
                    }
                }
            }

            var ordered = hits.OrderBy(h => h.Site, StringComparer.Ordinal).ThenBy(h => h.Api, StringComparer.Ordinal).ToList();
            Console.WriteLine("[SCAN] Assembly-CSharp methods=" + methods
                + ", MaterialPool.MatFrom call-site methods=" + matFrom.Count
                + ", _MainTex_ST write sites=" + ordered.Count);
            foreach (var h in ordered)
                Console.WriteLine("[SITE] " + h.Site + " -> " + h.Api);

            // The interesting negative: no shipped code writes the constant "_MainTex_ST" through
            // SetVector. Everything else is mainTextureScale/Offset or a dynamic property name.
            int constSt = ordered.Count(h => h.Api.StartsWith("SetVector(\"_", StringComparison.Ordinal));
            Console.WriteLine("[SCAN] SetVector with a literal _ST name=" + constSt
                + ", distinct writing methods=" + ordered.Select(h => h.Site).Distinct().Count());
        }
    }
}
