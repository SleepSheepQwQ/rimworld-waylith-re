using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace Host
{
    // A repeatable, static assertion suite over the real assemblies. No game runtime, no GPU:
    // everything is read out of the CLI metadata in dlls/.
    //
    //   assert 1 (hard)  Verse.Graphic.DrawMeshInt's IL may only reach UnityEngine.Graphics.DrawMesh
    //   assert 2 (info)  does the UnityEngine.GUI.DrawTextureWithTexCoords chain read the GUI event
    //                    phase (Event.current.type) / check for a non-repaint phase?
    //   assert 3 (hard)  list every method that calls Waylith.Rendering.MeshDrawSubmission.Enqueue
    //
    // Exit code: 0 = all hard assertions pass, 1 = at least one failed, 2 = the harness could not run.
    static class Gate
    {
        const string DrawMesh = "UnityEngine.Graphics::DrawMesh";
        const string EnqueueOwner = "Waylith.Rendering.MeshDrawSubmission::";

        static List<Module> Mods;
        static Dictionary<long, string> EventTypeNames = new Dictionary<long, string>();

        public static int RunAll(string dir, bool forceFail)
        {
            if (!Directory.Exists(dir))
            {
                Console.WriteLine("[FATAL] dll directory not found: " + dir);
                return 2;
            }

            Mods = Meta.Load(dir);
            EventTypeNames = Meta.EnumValues(Mods, "EventType");
            Console.WriteLine("dlls: " + dir);
            Console.WriteLine("modules: " + string.Join(", ", Mods.Select(m => m.Name)));
            if (Mods.Count == 0)
            {
                Console.WriteLine("[FATAL] no managed modules with metadata found");
                return 2;
            }

            int fail = 0;
            int selfBroken = SelfTest();
            fail += selfBroken;
            fail += CheckDrawMeshInt(out int drawCalls);
            int guards = ReportGuiChain();
            fail += CheckEnqueueCallers(out int callers, out int sites, out int related);

            if (forceFail)
            {
                Console.WriteLine();
                Console.WriteLine("[DEMO] --force-fail: flipping assert 1 to FAIL to prove the exit code is the result");
                LastDrawOk = false;
                fail++;
            }

            Console.WriteLine();
            Console.WriteLine("[SUMMARY] assert1=" + Status(LastDrawOk)
                              + " (draw calls=" + drawCalls + ")"
                              + " assert2=INFO(phase-guards=" + guards + ")"
                              + " assert3=" + Status(LastEnqueueOk) + "(callers=" + callers + ", sites=" + sites
                              + ", related-instanced=" + related + ")"
                              + " selftest=" + (selfBroken == 0 ? "PASS" : "BROKEN"));
            Console.WriteLine(fail == 0 ? "RESULT: PASS" : "RESULT: FAIL (" + fail + " failing assertion(s))");
            return fail == 0 ? 0 : 1;
        }

        static string Status(bool ok) => ok ? "PASS" : "FAIL";

        static bool LastDrawOk;
        static bool LastEnqueueOk;

        // ---------------------------------------------------------------- self test

        // Proves the rule actually rejects a changed call graph (mutation test), so a green
        // run cannot be green by accident.
        static int SelfTest()
        {
            int broken = 0;
            Console.WriteLine();
            Console.WriteLine("=== SELFTEST: the assert-1 rule must fail on a mutated call graph ===");
            Try("unchanged shape", true, new[] { DrawMesh }, ref broken);
            Try("mutated to Graphics.DrawMeshNow", false, new[] { "UnityEngine.Graphics::DrawMeshNow" }, ref broken);
            Try("mutated to Graphics.Internal_DrawMesh", false, new[] { "UnityEngine.Graphics::Internal_DrawMesh" }, ref broken);
            Try("bypassing the queue for MeshDrawSubmission.Enqueue", false, new[] { DrawMesh, EnqueueOwner + "Enqueue" }, ref broken);
            Try("no draw call at all", false, new string[0], ref broken);
            Console.WriteLine("  [SELFTEST] " + (broken == 0 ? "PASS (rule is mutation-sensitive)" : "BROKEN"));
            return broken;
        }

        static void Try(string label, bool expectPass, string[] calls, ref int broken)
        {
            bool pass = DrawVerdict(calls, out string why);
            bool ok = pass == expectPass;
            if (!ok) broken++;
            Console.WriteLine("  [" + (ok ? "OK" : "BROKEN") + "] " + label + ": expected "
                              + (expectPass ? "PASS" : "FAIL") + ", got " + (pass ? "PASS" : "FAIL") + " (" + why + ")");
        }

        // ---------------------------------------------------------------- assert 1

        // The rule: of all drawing/submission APIs the method touches, the only one may be
        // UnityEngine.Graphics.DrawMesh. Any other draw path (DrawMeshNow, Internal_DrawMesh,
        // DrawMeshCore, MeshDrawSubmission.Enqueue, ...) or a missing draw call is a failure.
        static bool DrawVerdict(IEnumerable<string> calls, out string why)
        {
            var draw = calls.Where(IsDrawApi).Distinct().ToList();
            if (draw.Count == 0) { why = "no draw call found"; return false; }
            var wrong = draw.Where(c => c != DrawMesh).ToList();
            if (wrong.Count > 0) { why = "unexpected draw path(s): " + string.Join(", ", wrong); return false; }
            why = "only " + DrawMesh;
            return true;
        }

        static bool IsDrawApi(string target)
        {
            int sep = target.IndexOf("::", StringComparison.Ordinal);
            string type = sep < 0 ? target : target.Substring(0, sep);
            string name = sep < 0 ? "" : target.Substring(sep + 2);
            if (type == "UnityEngine.Graphics" || type.EndsWith(".UnityEngine.Graphics", StringComparison.Ordinal)) return true;
            foreach (var k in new[] { "DrawMesh", "Enqueue", "DrawTexture", "DrawInstanced" })
                if (name.IndexOf(k, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        static int CheckDrawMeshInt(out int callCount)
        {
            callCount = 0;
            Console.WriteLine();
            Console.WriteLine("=== ASSERT 1 (hard): Verse.Graphic.DrawMeshInt -> " + DrawMesh + " only ===");
            int bad = 0, found = 0;
            bool allOk = true;

            foreach (var m in Mods)
            {
                foreach (var h in Meta.Methods(m, "Verse.Graphic", "DrawMeshInt"))
                {
                    found++;
                    string sig = m.MethodSignature(h);
                    if (!Meta.TryBody(m, h, out byte[] il, out string err))
                    {
                        Console.WriteLine("  [FAIL] " + sig + " [" + m.Name + "]: no IL body (" + err + ")");
                        bad++;
                        continue;
                    }
                    var ins = Il.Decode(il, out string derr);
                    if (ins == null)
                    {
                        Console.WriteLine("  [FAIL] " + sig + " [" + m.Name + "]: IL undecodable (" + derr + ")");
                        bad++;
                        continue;
                    }
                    var calls = new List<string>();
                    foreach (var i in ins)
                    {
                        if (!Il.IsCall(i)) continue;
                        string t = Describe(m, i.Token);
                        if (t != null && !calls.Contains(t)) calls.Add(t);
                    }
                    callCount += calls.Count;
                    bool ok = DrawVerdict(calls, out string why);
                    Console.WriteLine("  " + (ok ? "[PASS]" : "[FAIL]") + " " + sig + "  [" + m.Name + "]  il=" + il.Length
                                      + "B  instructions=" + ins.Count);
                    Console.WriteLine("         calls (" + calls.Count + "): "
                                      + (calls.Count == 0 ? "(none)" : string.Join(", ", calls)));
                    Console.WriteLine("         verdict: " + why);
                    if (!ok) { bad++; allOk = false; }
                }
            }

            if (found == 0)
            {
                Console.WriteLine("  [FAIL] Verse.Graphic::DrawMeshInt not found in any module (the assertion would be vacuous)");
                bad++;
                allOk = false;
            }
            LastDrawOk = allOk;
            return bad;
        }

        // ---------------------------------------------------------------- assert 2

        static int ReportGuiChain()
        {
            Console.WriteLine();
            Console.WriteLine("=== ASSERT 2 (info): does the GUI.DrawTextureWithTexCoords chain check the drawing phase? ===");
            var roots = new List<KeyValuePair<Module, MethodDefinitionHandle>>();
            foreach (var m in Mods)
                foreach (var h in Meta.Methods(m, "UnityEngine.GUI", "DrawTextureWithTexCoords"))
                    roots.Add(new KeyValuePair<Module, MethodDefinitionHandle>(m, h));

            if (roots.Count == 0)
            {
                Console.WriteLine("  [INFO] UnityEngine.GUI::DrawTextureWithTexCoords not found in the loaded dlls");
                return 0;
            }

            var seen = new HashSet<string>();
            int guardNodes = 0;
            foreach (var root in roots) Walk(root.Key, root.Value, 0, seen, ref guardNodes);

            Console.WriteLine("  [INFO] chain nodes reading the GUI event phase: " + guardNodes);
            if (guardNodes > 0)
                Console.WriteLine("  [FINDING] the chain does read Event.current.type: see the GUARD lines above. "
                                  + "Those lines name the enum value it is compared against; whether the draw call is "
                                  + "skipped on other phases is visible from the callee listed right after the guard.");
            else
                Console.WriteLine("  [FINDING] no Event.current / repaint-phase read found in the traversed chain.");
            return guardNodes;
        }

        static void Walk(Module m, MethodDefinitionHandle h, int depth, HashSet<string> seen, ref int guardNodes)
        {
            string sig = m.MethodSignature(h);
            string pad = new string(' ', depth * 3) + "[" + m.Name + " d" + depth + "] ";
            if (depth > 3) return;
            // Overloads can share one body (same RVA); walk each body once.
            var mdh = m.Md.GetMethodDefinition(h);
            string key = m.Name + "#" + (mdh.RelativeVirtualAddress != 0 ? mdh.RelativeVirtualAddress.ToString() : sig);
            if (!seen.Add(key))
            {
                Console.WriteLine(pad + sig + "  (same body as an already walked overload)");
                return;
            }

            if (!Meta.TryBody(m, h, out byte[] il, out string err))
            {
                Console.WriteLine(pad + sig + "  (no body: " + err + ")");
                return;
            }
            var ins = Il.Decode(il, out string derr);
            if (ins == null)
            {
                Console.WriteLine(pad + sig + "  (IL undecodable: " + derr + ")");
                return;
            }

            var calls = new List<string>();
            var refs = new List<string>();
            var strings = new List<string>();
            var guards = new List<string>();
            for (int i = 0; i < ins.Count; i++)
            {
                if (ins[i].Op == 0x72 && !ins[i].Prefixed)
                {
                    string s = m.UserString(ins[i].Token);
                    if (!string.IsNullOrEmpty(s)) strings.Add(s);
                    continue;
                }
                string t = Describe(m, ins[i].Token);
                if (t == null) continue;
                if (Il.IsCall(ins[i]))
                {
                    if (!calls.Contains(t)) calls.Add(t);
                    DetectGuard(m, ins, i, t, guards);
                }
                else if (!refs.Contains(t)) refs.Add(t);
            }

            Console.WriteLine(pad + sig + "  il=" + il.Length + "B");
            Console.WriteLine(pad + "  calls: " + (calls.Count == 0 ? "(none)" : string.Join(", ", calls)));
            if (refs.Count > 0) Console.WriteLine(pad + "  field/type refs: " + string.Join(", ", refs));
            foreach (var g in guards)
            {
                Console.WriteLine(pad + "  GUARD: " + g);
                guardNodes++;
            }
            foreach (var s in strings.Distinct())
                Console.WriteLine(pad + "  message: \"" + s + "\"");

            foreach (var t in calls)
            {
                int sep = t.IndexOf("::", StringComparison.Ordinal);
                if (sep < 0) continue;
                string owner = t.Substring(0, sep);
                string name = t.Substring(sep + 2);
                foreach (var m2 in Mods)
                {
                    foreach (var h2 in Meta.Methods(m2, owner, name))
                    {
                        Walk(m2, h2, depth + 1, seen, ref guardNodes);
                        break;
                    }
                }
            }
        }

        // Flags the "is the GUI in a drawing phase?" idiom: a read of Event.current.type that is
        // compared against an EventType constant, plus the call it guards.
        static void DetectGuard(Module m, List<Insn> ins, int at, string target, List<string> guards)
        {
            if (target.IndexOf("CheckOnGUI", StringComparison.Ordinal) >= 0)
            {
                guards.Add("calls " + target + " (Unity's GUI phase check helper)");
                return;
            }

            bool isType = target.EndsWith("::get_type", StringComparison.Ordinal);
            bool isCurrent = target.EndsWith("::get_current", StringComparison.Ordinal);
            if (!(isType || isCurrent) || target.IndexOf("Event", StringComparison.Ordinal) < 0) return;

            // Walk only the straight-line code that follows the read, stopping at the next call:
            // that keeps the constant we report attached to THIS read.
            long? cst = null;
            string compare = null;
            string branch = null;
            int branchAt = -1;
            int stop = Math.Min(ins.Count, at + 8);
            for (int j = at + 1; j < stop; j++)
            {
                if (Il.IsCall(ins[j])) { stop = j; break; }
                long v;
                if (Il.TryLdcI4(ins[j], out v) && cst == null) cst = v;
                if (Il.IsCompare(ins[j]) && compare == null) compare = ins[j].OpName;
                if (Il.IsBranch(ins[j]) && branch == null)
                {
                    branch = ins[j].OpName + " @IL_" + ins[j].Offset.ToString("X4");
                    branchAt = j;
                }
            }

            if (isCurrent && !isType)
            {
                guards.Add("Event.current null check" + (branch != null ? " (" + branch + ")" : ""));
                return;
            }

            string cstText = cst.HasValue ? cst.Value.ToString() : "?";
            string enumName;
            if (cst.HasValue && EventTypeNames.TryGetValue(cst.Value, out enumName))
                cstText += " (EventType." + enumName + ")";

            // What the guarded branch leads to (the call right after the branch).
            string guarded = null;
            if (branchAt >= 0)
            {
                for (int j = branchAt + 1; j < ins.Count; j++)
                {
                    if (!Il.IsCall(ins[j])) continue;
                    string t2 = Describe(m, ins[j].Token);
                    if (t2 != null) { guarded = t2; break; }
                }
            }

            guards.Add("Event.current.type compared with " + cstText
                       + (compare != null ? " (" + compare + ")" : "")
                       + (branch != null ? ", " + branch : "")
                       + (guarded != null ? " -> guarded call: " + guarded : ""));
        }

        // ---------------------------------------------------------------- assert 3

        static int CheckEnqueueCallers(out int callers, out int sites, out int related)
        {
            Console.WriteLine();
            Console.WriteLine("=== ASSERT 3 (hard, list must be non-empty): methods referencing " + EnqueueOwner + "Enqueue ===");
            callers = 0;
            sites = 0;
            related = 0;
            int bad = 0;

            var overloads = new List<string>();
            foreach (var m in Mods)
                foreach (var h in Meta.Methods(m, "Waylith.Rendering.MeshDrawSubmission", "Enqueue"))
                    overloads.Add(m.Name + ": " + m.MethodSignature(h));

            if (overloads.Count == 0)
            {
                Console.WriteLine("  [FAIL] Waylith.Rendering.MeshDrawSubmission::Enqueue does not exist in the loaded dlls");
                LastEnqueueOk = false;
                return 1;
            }
            Console.WriteLine("  declared overloads (" + overloads.Count + "):");
            foreach (var o in overloads) Console.WriteLine("    * " + o);

            var perCaller = new Dictionary<string, List<string>>();
            var perCallerRel = new Dictionary<string, List<string>>();
            foreach (var m in Mods)
            {
                foreach (var th in m.Md.TypeDefinitions)
                {
                    string typeName = m.TypeName(th);
                    var td = m.Md.GetTypeDefinition(th);
                    foreach (var h in td.GetMethods())
                    {
                        if (!Meta.TryBody(m, h, out byte[] il, out _)) continue;
                        var ins = Il.Decode(il, out _);
                        if (ins == null) continue;
                        foreach (var i in ins)
                        {
                            if (!Il.IsCall(i)) continue;
                            string t = Describe(m, i.Token);
                            if (t == null || !t.StartsWith(EnqueueOwner + "Enqueue", StringComparison.Ordinal)) continue;
                            string member = t.Substring(EnqueueOwner.Length);
                            bool exact = member == "Enqueue";
                            var target = exact ? perCaller : perCallerRel;
                            if (exact) sites++;
                            string key = m.Name + " | " + typeName + "::" + m.Md.GetString(m.Md.GetMethodDefinition(h).Name);
                            if (!target.ContainsKey(key)) target[key] = new List<string>();
                            target[key].Add(t);
                        }
                    }
                }
            }

            callers = perCaller.Count;
            related = perCallerRel.Count;
            Console.WriteLine("  call sites to Enqueue: " + sites + "   distinct callers: " + callers);
            foreach (var kv in perCaller.OrderBy(k => k.Key, StringComparer.Ordinal))
                Console.WriteLine("  [HIT] " + kv.Key + "   -> " + string.Join(", ", kv.Value.Distinct()));

            if (perCallerRel.Count > 0)
            {
                Console.WriteLine("  related instanced/indirect Enqueue* paths (" + related + " callers, listed for context, not part of the assertion):");
                foreach (var kv in perCallerRel.OrderBy(k => k.Key, StringComparer.Ordinal))
                    Console.WriteLine("  [REL] " + kv.Key + "   -> " + string.Join(", ", kv.Value.Distinct()));
            }

            if (callers == 0)
            {
                Console.WriteLine("  [FAIL] nothing calls " + EnqueueOwner + "Enqueue any more (the queue would be dead code)");
                bad++;
            }
            LastEnqueueOk = bad == 0;
            return bad;
        }

        static string Describe(Module m, int token)
        {
            if (token == 0) return null;
            try { return m.Describe(MetadataTokens.EntityHandle(token)); }
            catch { return null; }
        }
    }
}
