using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Static gate for the dev-mode translation mod. It answers three questions without running the game:
//   1. do the two patched methods still exist with the expected signatures?
//   2. does every English key we translate actually occur in the shipped assembly?
//   3. is the built mod assembly shaped the way the runtime expects?
internal static class Program
{
    private static int _failures;

    private static void Check(bool ok, string what, string detail = null)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + what + (detail == null ? "" : "  <- " + detail));
        if (!ok) _failures++;
    }

    private static int Main(string[] args)
    {
        var asmPath = args.Length > 0 ? args[0] : "dlls/Assembly-CSharp.dll";
        var dictPath = args.Length > 1 ? args[1] : "devzh/src/Dict.g.cs";
        var modPath = args.Length > 2 ? args[2] : "out/DevModeZh.dll";

        var asm = AssemblyDefinition.ReadAssembly(asmPath);

        // 1. patch targets
        var nodeType = asm.MainModule.GetType("Verse.DebugActionNode");
        Check(nodeType != null, "Verse.DebugActionNode exists");
        var getter = nodeType?.Properties.FirstOrDefault(p => p.Name == "LabelNow")?.GetMethod;
        Check(getter != null, "DebugActionNode.LabelNow has a getter", getter == null ? "missing property" : null);

        var dialog = asm.MainModule.GetType("Verse.Dialog_Debug");
        Check(dialog != null, "Verse.Dialog_Debug exists");
        var doLabel = dialog?.Methods.FirstOrDefault(m =>
            m.Name == "DoLabel" && m.Parameters.Count == 2 &&
            m.Parameters[0].ParameterType.FullName == "System.String" &&
            m.Parameters[1].ParameterType.FullName == "System.Single");
        Check(doLabel != null, "Dialog_Debug.DoLabel(string,float) exists");

        // 2. every translated key must be a real string literal
        var literals = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in asm.MainModule.Types)
        {
            CollectLiterals(type, literals);
        }
        Console.WriteLine($"INFO string literals in assembly: {literals.Count}");

        var dict = ParseDict(dictPath);
        Check(dict.Count == 227, "dictionary has 227 entries", "got " + dict.Count);
        Check(dict.Keys.Distinct(StringComparer.Ordinal).Count() == dict.Count, "dictionary has no duplicate keys");

        var missing = dict.Keys.Where(k => !literals.Contains(k)).ToList();
        Check(missing.Count == 0, "every key occurs in Assembly-CSharp",
            missing.Count == 0 ? null : string.Join(" | ", missing.Take(8)));

        // 3. the built mod must carry the two postfixes and the dictionary
        if (File.Exists(modPath))
        {
            var mod = AssemblyDefinition.ReadAssembly(modPath);
            var boot = mod.MainModule.GetType("DevModeZh.Bootstrap");
            Check(boot != null, "DevModeZh.Bootstrap present in the built dll");
            Check(boot?.Methods.Any(m => m.Name == "LabelNowPostfix") == true, "LabelNowPostfix present");
            Check(boot?.Methods.Any(m => m.Name == "DoLabelPostfix") == true, "DoLabelPostfix present");
            Check(mod.MainModule.GetType("DevModeZh.Dict") != null, "Dict type present");
        }
        else
        {
            Check(false, "built mod dll exists", modPath);
        }

        Console.WriteLine(_failures == 0 ? "DEVZH GATE: PASS" : $"DEVZH GATE: FAIL ({_failures})");
        return _failures == 0 ? 0 : 1;
    }

    private static void CollectLiterals(TypeDefinition type, HashSet<string> into)
    {
        foreach (var m in type.Methods)
        {
            if (!m.HasBody) continue;
            foreach (var ins in m.Body.Instructions)
            {
                if (ins.OpCode == OpCodes.Ldstr && ins.Operand is string s) into.Add(s);
            }
        }
        foreach (var nested in type.NestedTypes) CollectLiterals(nested, into);
    }

    private static Dictionary<string, string> ParseDict(string path)
    {
        var text = File.ReadAllText(path);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(text, "\\[\"(?<k>(?:[^\"\\\\]|\\\\.)*)\"\\]\\s*=\\s*\"(?<v>(?:[^\"\\\\]|\\\\.)*)\""))
        {
            var k = Regex.Unescape(m.Groups["k"].Value);
            var v = Regex.Unescape(m.Groups["v"].Value);
            map[k] = v;
        }
        return map;
    }
}
