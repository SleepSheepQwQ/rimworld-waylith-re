using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class Program
{
    private static readonly HashSet<string> Wanted = new HashSet<string>(StringComparer.Ordinal)
    {
        "IsPackedSource", "HasFile", "TryReadFile", "TryGetFileLength", "TryGetEntryName",
        "TryGetCurrentTextureEntry", "TryGetCurrentEntry", "TryReadTexture", "TryRentTexture",
        "LoadFromFile", "LoadFromTextureCache", "FileExists", "MountLoadFolder", "Mount", "Use",
    };

    private static int Main(string[] args)
    {
        string dir = args.Length > 0 ? args[0] : "dlls";
        Console.WriteLine($"ilsites: scanning {Path.GetFullPath(dir)}");
        List<string> lines = new List<string>();

        foreach (string path in Directory.GetFiles(dir, "*.dll").OrderBy(p => p, StringComparer.Ordinal))
        {
            AssemblyDefinition asm;
            try
            {
                asm = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { InMemory = true, ReadSymbols = false });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SKIP] {Path.GetFileName(path)}: {ex.GetType().Name}");
                continue;
            }

            string asmName = asm.Name.Name;
            foreach (ModuleDefinition module in asm.Modules)
            {
                foreach (TypeDefinition type in AllTypes(module.Types))
                {
                    foreach (MethodDefinition method in type.Methods)
                    {
                        if (!method.HasBody) continue;
                        IList<Instruction> body = method.Body.Instructions;
                        for (int i = 0; i < body.Count; i++)
                        {
                            Instruction ins = body[i];
                            if (ins.OpCode.Code != Code.Call && ins.OpCode.Code != Code.Callvirt && ins.OpCode.Code != Code.Newobj) continue;
                            if (!(ins.Operand is MethodReference mr)) continue;
                            string decl = DeclaringName(mr);
                            if (!Wanted.Contains(mr.Name)) continue;
                            if (!IsInteresting(decl, mr.Name)) continue;

                            lines.Add($"[CALLSITE] {asmName} :: {type.FullName}::{method.Name} +IL_{ins.Offset:X4} -> {decl}::{mr.Name}, args={mr.Parameters.Count}");
                            // a small window so the branch / pushed flag is visible
                            for (int k = Math.Max(0, i - 10); k <= Math.Min(body.Count - 1, i + 6); k++)
                            {
                                Instruction w = body[k];
                                string mark = k == i ? " >>" : "   ";
                                lines.Add($"{mark} IL_{w.Offset:X4} {w.OpCode.Name,-10} {FormatOperand(w)}");
                            }
                            lines.Add("");
                        }
                    }
                }
            }
        }

        foreach (string line in lines) Console.WriteLine(line);
        Console.WriteLine($"[SUMMARY] {lines.Count(l => l.StartsWith("[CALLSITE]"))} call sites");
        return 0;
    }

    private static bool IsInteresting(string decl, string name)
    {
        if (name == "LoadFromFile" || name == "LoadFromTextureCache")
            return decl is "UnityEngine.Texture2D" or "Waylith.Rendering.Texture2D";
        if (name == "FileExists")
            return true;
        // pack predicates: only the ones that live in the pack / bridge layer
        return decl == "UnityStubs.ContentPacks" || decl == "Waylith.Assets.ContentPack" || decl == "Waylith.Utils.ResourceFiles";
    }

    private static string DeclaringName(MethodReference mr)
    {
        try { return mr.DeclaringType?.FullName ?? "?"; }
        catch { return "?"; }
    }

    private static string FormatOperand(Instruction ins)
    {
        switch (ins.Operand)
        {
            case null: return "";
            case string s: return "\"" + s + "\"";
            case MethodReference m:
                try { return DeclaringName(m) + "::" + m.Name; } catch { return m.Name; }
            case FieldReference f:
                try { return f.DeclaringType?.FullName + "::" + f.Name; } catch { return f.Name; }
            case Instruction target: return "IL_" + target.Offset.ToString("X4");
            default: return ins.Operand.ToString();
        }
    }

    private static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types)
    {
        foreach (TypeDefinition t in types)
        {
            yield return t;
            foreach (TypeDefinition nested in AllTypes(t.NestedTypes)) yield return nested;
        }
    }
}
