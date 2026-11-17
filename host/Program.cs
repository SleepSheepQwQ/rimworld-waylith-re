using System;
using System.Linq;
using System.Reflection;

static class Program
{
    static void Dump(Type t, string name)
    {
        if (t == null) { Console.WriteLine($"MISSING type {name}"); return; }
        Console.WriteLine($"type {t.FullName}  ({t.Assembly.GetName().Name})");
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                            .Where(m => m.Name == name))
        {
            var ps = string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name));
            Console.WriteLine($"   {(m.IsStatic ? "static" : "inst")} {(m.IsVirtual ? "virtual" : "")} {m.ReturnType.Name} {m.Name}({ps})");
        }
    }

    static string Find(string name)
    {
        foreach (var root in new[] { AppContext.BaseDirectory, "dlls", "../dlls", "../../dlls" })
        {
            var p = System.IO.Path.Combine(root, name);
            if (System.IO.File.Exists(p)) return System.IO.Path.GetFullPath(p);
        }
        throw new System.IO.FileNotFoundException("not found: " + name + " (cwd=" + System.IO.Directory.GetCurrentDirectory() + ")");
    }

    static Assembly Load(string name) => Assembly.LoadFrom(Find(name));

    static void Main()
    {
        var acs = Load("Assembly-CSharp.dll");
        var way = Load("Waylith.dll");
        var uni = Load("UnityStubs.dll");

        IlScan.Run(Find("Assembly-CSharp.dll"), Find("UnityStubs.dll"));

        Console.WriteLine("== Graphic.DrawMeshInt ==");
        Dump(acs.GetType("Verse.Graphic"), "DrawMeshInt");
        Console.WriteLine("== Graphic.DrawWorker ==");
        Dump(acs.GetType("Verse.Graphic"), "DrawWorker");
        Console.WriteLine("== GenDraw.DrawMeshNowOrLater ==");
        Dump(acs.GetType("Verse.GenDraw"), "DrawMeshNowOrLater");
        Console.WriteLine("== Graphics.DrawMesh (stub) ==");
        Dump(uni.GetType("UnityEngine.Graphics"), "DrawMesh");
        Console.WriteLine("== Graphics.DrawMeshNow (stub) ==");
        Dump(uni.GetType("UnityEngine.Graphics"), "DrawMeshNow");
        Console.WriteLine("== PawnRenderUtility ==");
        Dump(acs.GetType("Verse.PawnRenderUtility"), "DrawCarriedThing");
        Dump(acs.GetType("Verse.PawnRenderUtility"), "DrawEquipmentAndApparelExtras");
        Console.WriteLine("== ThingIcon overloads ==");
        var w = acs.GetType("Verse.Widgets");
        foreach (var m in w.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name == "ThingIcon"))
            Console.WriteLine("   ThingIcon(" + string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name)) + ")");
        Console.WriteLine("== GpuTextureService cache entry points ==");
        var svc = way.GetType("Waylith.Rendering.GpuTextureService");
        foreach (var n in new[] { "TryOpenTextureCache", "HasValidTextureCache", "WriteTextureCache" })
            Dump(svc, n);
    }
}
