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
        /// <summary>Stand-in for Verse.TooltipHandler: overloads that differ in arity and parameter types.</summary>
        internal static class Tooltip
        {
            internal static int Registered;

            public static void TipRegion(RectLike r, string text) { Registered++; }
            public static void TipRegion(RectLike r, string text, int priority) { Registered++; }
            public static void TipRegionByKey(RectLike r, string key) { Registered++; }
            public static void TipRegionByKey(RectLike r, string key, object a, object b, object c) { Registered++; }
        }

        internal struct RectLike
        {
            public float x, y, w, h;
        }

        internal static class TooltipPatches
        {
            internal static bool Suppress;

            /// <summary>Same shape as TooltipPatches.Suppress_Prefix: no parameters at all.</summary>
            internal static bool Prefix()
            {
                return !Suppress;    // false -> the original is skipped
            }
        }

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

            fail += ProbeOverloadPrefix();

            Console.WriteLine(fail == 0 ? "PROBE OK" : "PROBE FAILED (" + fail + ")");
            return fail == 0 ? 0 : 1;
        }

        /// <summary>
        /// v6 mechanism: one parameterless bool prefix installed on every overload of a method name
        /// (Verse.TooltipHandler has 6 TipRegion/TipRegionByKey shapes). Verifies that Harmony binds
        /// the same prefix to all of them and that returning false skips only the call, while true
        /// still runs it - the mod relies on this to drop tooltips without touching Event.current.
        /// </summary>
        private static int ProbeOverloadPrefix()
        {
            int fail = 0;
            Harmony harmony = new Harmony("probe.sleepsheep.agenttouchfix.overloadprefix");
            MethodInfo prefix = typeof(TooltipPatches).GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic);
            if (prefix == null) { Console.WriteLine("FAIL: overload prefix scaffolding missing"); return 1; }

            int patched = 0;
            foreach (MethodInfo m in typeof(Tooltip).GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name != "TipRegion" && m.Name != "TipRegionByKey") continue;
                try
                {
                    harmony.Patch(m, new HarmonyMethod(prefix), null);
                    patched++;
                }
                catch (Exception e)
                {
                    Console.WriteLine("FAIL: cannot patch " + m.Name + "/" + m.GetParameters().Length + ": " + e.Message);
                    fail++;
                }
            }
            if (patched != 4) { Console.WriteLine("FAIL: patched " + patched + "/4 overloads"); fail++; }
            else Console.WriteLine("installed: one parameterless prefix on 4 differently shaped overloads");

            Tooltip.Registered = 0;
            TooltipPatches.Suppress = false;
            Tooltip.TipRegion(new RectLike(), "a");
            Tooltip.TipRegion(new RectLike(), "a", 1);
            Tooltip.TipRegionByKey(new RectLike(), "k");
            Tooltip.TipRegionByKey(new RectLike(), "k", 1, 2, 3);
            if (Tooltip.Registered != 4) { Console.WriteLine("FAIL: passthrough registered " + Tooltip.Registered + "/4"); fail++; }

            Tooltip.Registered = 0;
            TooltipPatches.Suppress = true;
            Tooltip.TipRegion(new RectLike(), "a");
            Tooltip.TipRegion(new RectLike(), "a", 1);
            Tooltip.TipRegionByKey(new RectLike(), "k");
            Tooltip.TipRegionByKey(new RectLike(), "k", 1, 2, 3);
            if (Tooltip.Registered != 0) { Console.WriteLine("FAIL: suppress still registered " + Tooltip.Registered); fail++; }

            Tooltip.Registered = 0;
            TooltipPatches.Suppress = false;
            Tooltip.TipRegion(new RectLike(), "a");
            if (Tooltip.Registered != 1) { Console.WriteLine("FAIL: restore registered " + Tooltip.Registered + "/1"); fail++; }

            Console.WriteLine(fail == 0 ? "OVERLOAD PROBE OK" : "OVERLOAD PROBE FAILED (" + fail + ")");
            return fail;
        }
    }
}
