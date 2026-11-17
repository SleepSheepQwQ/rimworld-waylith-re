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

    static void Main()
    {
        var acs = Assembly.LoadFrom("../dlls/Assembly-CSharp.dll");
        var way = Assembly.LoadFrom("../dlls/Waylith.dll");
        var uni = Assembly.LoadFrom("../dlls/UnityStubs.dll");

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
