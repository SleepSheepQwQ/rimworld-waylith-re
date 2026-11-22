using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Static IL audit: who writes UnityEngine.RenderTexture.active, and who calls the
// UnityStubs entry points that write it without restoring.
internal static class Program
{
    private static readonly string Dir =
        Directory.Exists(Path.Combine(Directory.GetCurrentDirectory(), "dlls")) ? "dlls" : "../dlls";

    private static readonly string[] Asms = Directory.GetFiles(
            Directory.Exists(Path.Combine(Directory.GetCurrentDirectory(), "dlls")) ? "dlls" : "../dlls",
            "*.dll")
        .Select(Path.GetFileName)
        .OrderBy(x => x, StringComparer.Ordinal)
        .ToArray();

    private sealed class Site
    {
        public string Asm, Method, Callee, Sig;
        public int Offset;
        public bool NullArg;
        public override string ToString() =>
            $"{Method}  {Sig}  ->  {Callee}  (IL_{Offset:x4}{(NullArg ? ", null" : "")})";
    }

    private static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--dump")
        {
            var asm = AssemblyDefinition.ReadAssembly(Path.Combine(Dir, args[1]));
            foreach (var t in AllTypes(asm.MainModule))
            foreach (var m in t.Methods)
                if (m.HasBody && m.Name.Contains(args[2]))
                {
                    Console.WriteLine($"### {t.FullName}::{m.Name}({string.Join(",", m.Parameters.Select(p => p.ParameterType.Name))})");
                    foreach (var i in m.Body.Instructions)
                        Console.WriteLine($"  IL_{i.Offset:x4} {i.OpCode,-12} {i.Operand}");
                }
            return;
        }

        var loaded = new List<(string Name, AssemblyDefinition Asm)>();
        foreach (var f in Asms)
        {
            string p = Path.Combine(Dir, f);
            if (!File.Exists(p)) { Console.WriteLine($"# missing {f}"); continue; }
            try { loaded.Add((f, AssemblyDefinition.ReadAssembly(p))); }
            catch (Exception e) { Console.WriteLine($"# cannot read {f}: {e.Message}"); }
        }

        var writes = new List<Site>();
        var callers = new List<Site>();

        foreach (var (name, asm) in loaded)
        {
            foreach (var type in AllTypes(asm.MainModule))
            foreach (var m in type.Methods)
            {
                if (!m.HasBody) continue;
                foreach (var ins in m.Body.Instructions)
                {
                    if (ins.Operand is not MethodReference mr) continue;
                    string dt = mr.DeclaringType?.Name ?? "";
                    string full = type.FullName + "::" + m.Name;
                    if (mr.Name == "set_active" && dt == "RenderTexture")
                    {
                        bool nul = ins.Previous != null && ins.Previous.OpCode == OpCodes.Ldnull;
                        writes.Add(new Site { Asm = name, Method = full, Callee = "RenderTexture.set_active", Offset = ins.Offset, NullArg = nul, Sig = SigOf(m) });
                    }
                    if (dt == "Graphics" && (mr.Name == "Blit" || mr.Name == "SetRenderTarget" || mr.Name == "CopyTexture"))
                    {
                        callers.Add(new Site { Asm = name, Method = full, Callee = mr.Name + "(" + string.Join(",", mr.Parameters.Select(p => p.ParameterType.Name)) + ")", Offset = ins.Offset, Sig = SigOf(m) });
                    }
                }
            }
        }

        Console.WriteLine("=== A. RenderTexture.set_active writes in every scanned assembly ===");
        foreach (var s in writes.OrderBy(s => s.Asm).ThenBy(s => s.Method))
            Console.WriteLine($"[{s.Asm}] {s}");

        Console.WriteLine();
        Console.WriteLine("=== B. callers of Graphics.Blit / SetRenderTarget / CopyTexture ===");
        foreach (var s in callers.OrderBy(s => s.Asm).ThenBy(s => s.Method))
            Console.WriteLine($"[{s.Asm}] {s}");

        Console.WriteLine();
        Console.WriteLine("=== C. save/restore shape of methods that write active ===");
        foreach (var g in writes.GroupBy(w => (w.Asm, w.Method)))
        {
            var asm = loaded.First(l => l.Name == g.Key.Asm).Asm;
            var m = FindMethod(asm.MainModule, g.Key.Method);
            if (m == null) continue;
            Console.WriteLine($"[{g.Key.Asm}] {g.Key.Method}");
            Console.WriteLine("    " + Shape(m));
        }

        Console.WriteLine();
        Console.WriteLine("=== E. callers of UnityStubs.FrameStart.Run and GuiBackend.set_Current ===");
        foreach (var (name, asm) in loaded)
        {
            foreach (var t in AllTypes(asm.MainModule))
            foreach (var m in t.Methods)
            {
                if (!m.HasBody) continue;
                foreach (var ins in m.Body.Instructions)
                {
                    if (ins.Operand is not MethodReference mr) continue;
                    bool frame = mr.Name == "Run" && mr.DeclaringType?.Name == "FrameStart";
                    bool gui = mr.Name == "set_Current" && mr.DeclaringType?.Name == "GuiBackend";
                    bool shader = mr.Name == "set_Current" && mr.DeclaringType?.Name == "CameraTextureRenderer";
                    if (frame || gui || shader)
                        Console.WriteLine($"[{name}] {t.FullName}::{m.Name} -> {mr.DeclaringType?.Name}.{mr.Name} (IL_{ins.Offset:x4})");
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine("=== D. reverse call chains (depth 4) for every method containing a Graphics.Blit/SetRenderTarget call ===");
        foreach (var (name, asm) in loaded)
        {
            var index = ReverseIndex(asm.MainModule);
            foreach (var root in callers.Where(c => c.Asm == name).Select(c => c.Method).Distinct())
            {
                Console.WriteLine($"[{name}] {root}");
                Walk(index, root, 1, 4, new HashSet<string>());
            }
        }
    }

    private static string SigOf(MethodDefinition m) =>
        m.Name + "(" + string.Join(",", m.Parameters.Select(p => p.ParameterType.Name)) + ")";

    private static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition m)
    {
        foreach (var t in m.Types)
            foreach (var x in WalkType(t)) yield return x;
    }

    private static IEnumerable<TypeDefinition> WalkType(TypeDefinition t)
    {
        yield return t;
        foreach (var n in t.NestedTypes)
            foreach (var x in WalkType(n)) yield return x;
    }

    private static MethodDefinition FindMethod(ModuleDefinition m, string full)
    {
        foreach (var t in AllTypes(m))
            foreach (var x in t.Methods)
                if (t.FullName + "::" + x.Name == full && x.HasBody) return x;
        return null;
    }

    // Summarize the local-save / restore pattern around each set_active in a method.
    private static string Shape(MethodDefinition m)
    {
        var ins = m.Body.Instructions;
        var parts = new List<string>();
        for (int i = 0; i < ins.Count; i++)
        {
            var x = ins[i];
            if (x.Operand is not MethodReference mr || mr.Name != "set_active" || mr.DeclaringType.Name != "RenderTexture")
                continue;
            string arg = x.Previous == null ? "?" : x.Previous.OpCode == OpCodes.Ldnull ? "null" : x.Previous.OpCode.Name;
            // look 1-3 instructions back for a get_active whose result went to a local, then the
            // same local is loaded for this setter: the canonical save/restore.
            string restoredFrom = "no";
            if (arg.StartsWith("ldloc"))
            {
                int at = -1;
                for (int j = i - 1; j >= Math.Max(0, i - 6); j--)
                    if (ins[j].OpCode == OpCodes.Call && ins[j].Operand is MethodReference g && g.Name == "get_active") { at = j; break; }
                if (at >= 0) restoredFrom = "ldloc(after get_active)";
                else
                {
                    // find the earlier stloc for the same local
                    for (int j = i - 1; j >= 0; j--)
                        if (ins[j].OpCode == OpCodes.Stloc_0 || ins[j].OpCode == OpCodes.Stloc_1 || ins[j].OpCode == OpCodes.Stloc_2 ||
                            ins[j].OpCode == OpCodes.Stloc_3 || ins[j].OpCode == OpCodes.Stloc_S || ins[j].OpCode == OpCodes.Stloc)
                        { restoredFrom = "ldloc(from earlier store at IL_" + ins[j].Offset.ToString("x4") + ")"; break; }
                }
            }
            parts.Add($"IL_{x.Offset:x4} arg={arg} restore={restoredFrom}");
        }
        return parts.Count == 0 ? "(no set_active)" : string.Join(" | ", parts);
    }

    private static Dictionary<string, List<string>> ReverseIndex(ModuleDefinition m)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var t in AllTypes(m))
        foreach (var x in t.Methods)
        {
            if (!x.HasBody) continue;
            string caller = t.FullName + "::" + x.Name;
            foreach (var ins in x.Body.Instructions)
            {
                if (ins.Operand is not MethodReference mr) continue;
                string callee = mr.DeclaringType?.FullName + "::" + mr.Name;
                if (!map.TryGetValue(callee, out var l)) map[callee] = l = new List<string>();
                if (!l.Contains(caller)) l.Add(caller);
            }
        }
        return map;
    }

    private static void Walk(Dictionary<string, List<string>> index, string name, int depth, int max, HashSet<string> seen)
    {
        if (depth > max) return;
        if (!index.TryGetValue(name, out var parents)) return;
        foreach (var p in parents)
        {
            Console.WriteLine(new string(' ', depth * 2) + "<- " + p);
            if (seen.Add(p)) Walk(index, p, depth + 1, max, seen);
        }
    }
}
