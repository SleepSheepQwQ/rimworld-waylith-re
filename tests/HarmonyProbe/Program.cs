using System;
using System.Reflection;
using HarmonyLib;

namespace HarmonyProbe
{
    /// <summary>Stand-in for UnityEngine.Vector3: a value type returned by value.</summary>
    internal struct Vec3
    {
        public float x, y, z;
        public Vec3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
    }

    internal static class Target
    {
        // Same shape as Verse.UI.MouseMapPosition(): static, no arguments, struct return.
        public static Vec3 MouseMapPosition() { return new Vec3(1f, 0f, 2f); }
    }

    internal static class Patches
    {
        internal static bool Override;
        internal static object Value;

        // Same shape as ToolPatches.MouseMapPosition_Prefix<T>.
        internal static bool Prefix<T>(ref T __result)
        {
            if (!Override) return true;
            object v = Value;
            if (v == null) return true;
            try { __result = (T)v; return false; }
            catch { return true; }
        }
    }

    internal static class Program
    {
        private static int Main()
        {
            int fail = 0;

            Harmony harmony = new Harmony("probe.sleepsheep.agenttouchfix.genericprefix");
            MethodInfo target = typeof(Target).GetMethod("MouseMapPosition");
            MethodInfo open = typeof(Patches).GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic);

            if (target == null || open == null || !open.IsGenericMethodDefinition)
            {
                Console.WriteLine("FAIL: probe scaffolding not resolvable");
                return 1;
            }

            MethodInfo closed = open.MakeGenericMethod(target.ReturnType);
            try
            {
                harmony.Patch(target, new HarmonyMethod(closed, 700, null, null, null), null);
                Console.WriteLine("installed: closed generic prefix on " + target.DeclaringType.FullName + "." + target.Name
                    + " (param " + closed.GetParameters()[0].ParameterType.FullName + ")");
            }
            catch (Exception e)
            {
                Console.WriteLine("PATCH FAILED: " + e);
                return 1;
            }

            Vec3 a = Target.MouseMapPosition();
            if (a.x != 1f || a.z != 2f) { Console.WriteLine("FAIL passthrough x=" + a.x + " z=" + a.z); fail++; }

            Patches.Override = true;
            Patches.Value = new Vec3(7f, 0f, 9f);
            Vec3 b = Target.MouseMapPosition();
            if (b.x != 7f || b.z != 9f) { Console.WriteLine("FAIL override x=" + b.x + " z=" + b.z); fail++; }

            Patches.Override = false;
            Vec3 c = Target.MouseMapPosition();
            if (c.x != 1f || c.z != 2f) { Console.WriteLine("FAIL restore x=" + c.x + " z=" + c.z); fail++; }

            // A wrong-typed boxed value must fall through to the original, not throw.
            Patches.Override = true;
            Patches.Value = "not a vector";
            Vec3 d = Target.MouseMapPosition();
            if (d.x != 1f || d.z != 2f) { Console.WriteLine("FAIL fallback x=" + d.x + " z=" + d.z); fail++; }

            Console.WriteLine(fail == 0 ? "PROBE OK" : "PROBE FAILED (" + fail + ")");
            return fail == 0 ? 0 : 1;
        }
    }
}
