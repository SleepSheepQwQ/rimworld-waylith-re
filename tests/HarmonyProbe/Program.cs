using System;
using System.Collections.Generic;
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

            // v10 shapes: the read-only texture counters and the pick switch rely on these.
            fail += ProbeSpanPlan();
            fail += ProbeArgsPostfix();
            fail += ProbeGenericSkipPrefix();
            fail += ProbeParameterlessInstancePrefix();

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

        // ================================================================ v10 probe stand-ins
        //
        // Every shape below is copied from the real target, so a change in Harmony's binding rules
        // (or a wrong assumption about them) fails the build instead of failing silently on a phone.

        /// <summary>Shape of Ports1de.DdsFallback.Plan (a record: properties, not fields).</summary>
        internal sealed class PlanStub
        {
            public int Format { get; private set; }
            public int Width { get; private set; }
            public int Height { get; private set; }
            public int Shift { get; private set; }
            public int SourceMip { get; private set; }

            public PlanStub(int format, int width, int height, int shift, int sourceMip)
            {
                Format = format; Width = width; Height = height; Shift = shift; SourceMip = sourceMip;
            }
        }

        /// <summary>Shape of Ports1de.DdsFallback.TryPlan (first parameter is a byref-like span).</summary>
        internal static class DdsStub
        {
            public static bool TryPlan(ReadOnlySpan<byte> data, int budget, out PlanStub plan, int maxDimension = 8192)
            {
                plan = null;
                if (data.Length < 3) return false;
                plan = new PlanStub(98, data[0], data[1], data[2], 0);
                return true;
            }
        }

        internal static class PlanPatches
        {
            internal static int Calls, Budget = -1, Dim = -1;
            internal static object Seen;
            internal static bool Result;

            // Same shape as TexProbe.TryPlan_Prefix.
            internal static void Prefix(int budget, int maxDimension)
            {
                Calls++;
                Budget = budget;
                Dim = maxDimension;
            }

            // Same shape as TexProbe.TryPlan_Postfix: 'object' for an out parameter of a type the
            // mod cannot name at compile time.
            internal static void Postfix(bool __result, object plan)
            {
                Result = __result;
                Seen = plan;
            }
        }

        /// <summary>Shape of Ports1de.DdsFallback.TryConvert (two out parameters).</summary>
        internal static class ConvStub
        {
            public static bool TryConvert(byte[] input, int length, int budget, Func<int, int> decoder,
                out byte[] converted, out int used, object pool = null, int maxDimension = 8192)
            {
                converted = null;
                used = 0;
                if (input == null || length <= 0 || length > input.Length) return false;
                converted = new byte[length];
                used = length;
                return true;
            }
        }

        internal static class ConvPatches
        {
            internal static int Calls;
            internal static object[] Args;
            internal static bool Result;

            // Same shape as TexProbe.TryConvert_Postfix.
            internal static void Postfix(object[] __args, bool __result)
            {
                Calls++;
                Args = __args;
                Result = __result;
            }
        }

        /// <summary>Shape of PortUI.MobileSelectionController.Candidates.</summary>
        internal static class PickStub
        {
            public static List<object> Candidates(Vec3 position)
            {
                List<object> list = new List<object>();
                list.Add("cell");
                return list;
            }
        }

        internal static class PickPatches
        {
            internal static bool Exact;
            internal static float SeenX, SeenZ;
            internal static int Calls;

            // Same shape as PickMode.Candidates_Prefix: generic over the position type (closed over
            // UnityEngine.Vector3 at runtime), writes the result and skips the original.
            internal static bool Prefix<T>(T position, ref List<object> __result)
            {
                Calls++;
                if (!Exact) return true;
                object boxed = position;
                Type t = boxed.GetType();
                FieldInfo fx = t.GetField("x");
                FieldInfo fz = t.GetField("z");
                if (fx != null) SeenX = Convert.ToSingle(fx.GetValue(boxed));
                if (fz != null) SeenZ = Convert.ToSingle(fz.GetValue(boxed));
                __result = new List<object>();
                return false;
            }
        }

        /// <summary>Shape of Waylith.Rendering.Texture2D.Evict (instance, no parameters).</summary>
        internal sealed class TextureStub
        {
            internal int Evicted;
            public void Evict() { Evicted++; }
        }

        internal static class EvictPatches
        {
            internal static int Hits;
            internal static void Prefix() { Hits++; }
        }

        /// <summary>
        /// Prefix on a method whose first parameter is ReadOnlySpan&lt;byte&gt;, a postfix that reads
        /// the out parameter as object, and a postfix that reads every argument through __args.
        /// These are the two decode probes; if Harmony cannot bind them the log would be empty.
        /// </summary>
        private static int ProbeSpanPlan()
        {
            int fail = 0;
            Harmony harmony = new Harmony("probe.sleepsheep.agenttouchfix.spanplan");
            MethodInfo target = typeof(DdsStub).GetMethod("TryPlan");
            MethodInfo prefix = typeof(PlanPatches).GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo postfix = typeof(PlanPatches).GetMethod("Postfix", BindingFlags.Static | BindingFlags.NonPublic);
            if (target == null || prefix == null || postfix == null)
            {
                Console.WriteLine("FAIL: span/plan probe scaffolding missing");
                return 1;
            }
            try
            {
                harmony.Patch(target, new HarmonyMethod(prefix), new HarmonyMethod(postfix));
                Console.WriteLine("installed: prefix+postfix on a ReadOnlySpan<byte> method (postfix takes 'object plan' for the out parameter)");
            }
            catch (Exception e)
            {
                Console.WriteLine("FAIL: cannot patch a ReadOnlySpan<byte> method: " + e.Message);
                return 1;
            }

            PlanPatches.Calls = 0; PlanPatches.Budget = -1; PlanPatches.Dim = -1;
            PlanPatches.Seen = null; PlanPatches.Result = false;

            bool ok;
            try { ok = DdsStub.TryPlan(new byte[] { 64, 32, 2 }, 1234); }
            catch (Exception e)
            {
                Console.WriteLine("FAIL: the patched call threw " + e.GetType().Name + ": " + e.Message);
                return 1;
            }

            if (PlanPatches.Calls != 1) { Console.WriteLine("FAIL: prefix not called"); fail++; }
            if (PlanPatches.Budget != 1234 || PlanPatches.Dim != 8192)
            {
                Console.WriteLine("FAIL: named ints not bound (budget=" + PlanPatches.Budget + " maxDimension=" + PlanPatches.Dim + ")");
                fail++;
            }
            if (!ok || !PlanPatches.Result) { Console.WriteLine("FAIL: __result not visible to the postfix"); fail++; }
            PlanStub p = PlanPatches.Seen as PlanStub;
            if (p == null) Console.WriteLine("FAIL: out plan did not reach the postfix as object");
            else if (p.Width != 64 || p.Height != 32 || p.Shift != 2)
            {
                Console.WriteLine("FAIL: plan values w=" + p.Width + " h=" + p.Height + " shift=" + p.Shift);
                fail++;
            }
            else Console.WriteLine("verified: out plan (object) w=" + p.Width + " h=" + p.Height + " shift=" + p.Shift);
            if (p == null) fail++;

            Console.WriteLine(fail == 0 ? "SPAN/PLAN PROBE OK" : "SPAN/PLAN PROBE FAILED (" + fail + ")");
            return fail;
        }

        /// <summary>Postfix reading every argument (including both out values) through __args.</summary>
        private static int ProbeArgsPostfix()
        {
            int fail = 0;
            Harmony harmony = new Harmony("probe.sleepsheep.agenttouchfix.argspostfix");
            MethodInfo target = typeof(ConvStub).GetMethod("TryConvert");
            MethodInfo postfix = typeof(ConvPatches).GetMethod("Postfix", BindingFlags.Static | BindingFlags.NonPublic);
            if (target == null || postfix == null) { Console.WriteLine("FAIL: __args probe scaffolding missing"); return 1; }
            try
            {
                harmony.Patch(target, null, new HarmonyMethod(postfix));
                Console.WriteLine("installed: __args postfix on TryConvert(byte[], int, int, delegate, out byte[], out int, object, int)");
            }
            catch (Exception e)
            {
                Console.WriteLine("FAIL: cannot install the __args postfix: " + e.Message);
                return 1;
            }

            ConvPatches.Calls = 0; ConvPatches.Args = null; ConvPatches.Result = false;
            bool ok;
            try { ok = ConvStub.TryConvert(new byte[] { 1, 2, 3, 4 }, 4, 2048, null); }
            catch (Exception e)
            {
                Console.WriteLine("FAIL: the patched call threw " + e.GetType().Name + ": " + e.Message);
                return 1;
            }

            object[] a = ConvPatches.Args;
            if (ConvPatches.Calls != 1 || a == null) { Console.WriteLine("FAIL: postfix not called"); return 1; }
            if (a.Length != 8) { Console.WriteLine("FAIL: __args has " + a.Length + " entries (8 expected)"); fail++; }
            if (!ok || !ConvPatches.Result) { Console.WriteLine("FAIL: __result not visible"); fail++; }
            byte[] converted = a.Length > 4 ? a[4] as byte[] : null;
            int used = a.Length > 5 ? Convert.ToInt32(a[5]) : -1;
            int budget = a.Length > 2 ? Convert.ToInt32(a[2]) : -1;
            int dim = a.Length > 7 ? Convert.ToInt32(a[7]) : -1;
            if (converted == null || converted.Length != 4) { Console.WriteLine("FAIL: out byte[] not visible through __args"); fail++; }
            if (used != 4) { Console.WriteLine("FAIL: out int used=" + used + " (4 expected)"); fail++; }
            if (budget != 2048) { Console.WriteLine("FAIL: budget=" + budget + " (2048 expected)"); fail++; }
            if (dim != 8192) { Console.WriteLine("FAIL: maxDimension=" + dim + " (8192 expected)"); fail++; }
            if (fail == 0) Console.WriteLine("verified: __args postfix sees in args and both out values (used=" + used + ")");

            Console.WriteLine(fail == 0 ? "ARGS PROBE OK" : "ARGS PROBE FAILED (" + fail + ")");
            return fail;
        }

        /// <summary>Generic prefix closed over the position type: replace the result, skip the original.</summary>
        private static int ProbeGenericSkipPrefix()
        {
            int fail = 0;
            Harmony harmony = new Harmony("probe.sleepsheep.agenttouchfix.genericskip");
            MethodInfo target = typeof(PickStub).GetMethod("Candidates");
            MethodInfo open = typeof(PickPatches).GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic);
            if (target == null || open == null || !open.IsGenericMethodDefinition)
            {
                Console.WriteLine("FAIL: pick probe scaffolding missing");
                return 1;
            }
            MethodInfo closed;
            try { closed = open.MakeGenericMethod(target.GetParameters()[0].ParameterType); }
            catch (Exception e) { Console.WriteLine("FAIL: generic close failed: " + e.Message); return 1; }

            try
            {
                harmony.Patch(target, new HarmonyMethod(closed), null);
                Console.WriteLine("installed: closed generic skip-prefix on Candidates(Vec3) -> List<object>");
            }
            catch (Exception e)
            {
                Console.WriteLine("FAIL: cannot install the skip-prefix: " + e.Message);
                return 1;
            }

            PickPatches.Exact = false;
            List<object> a;
            try { a = PickStub.Candidates(new Vec3(4f, 0f, 6f)); }
            catch (Exception e) { Console.WriteLine("FAIL: patched call threw " + e.GetType().Name + ": " + e.Message); return 1; }
            if (a == null || a.Count != 1) { Console.WriteLine("FAIL: port mode changed the result"); fail++; }

            PickPatches.Exact = true;
            PickPatches.SeenX = -1f; PickPatches.SeenZ = -1f;
            List<object> b;
            try { b = PickStub.Candidates(new Vec3(4f, 0f, 6f)); }
            catch (Exception e) { Console.WriteLine("FAIL: skip call threw " + e.GetType().Name + ": " + e.Message); return 1; }
            if (b == null || b.Count != 0) { Console.WriteLine("FAIL: exact mode did not replace the result"); fail++; }
            if (PickPatches.SeenX != 4f || PickPatches.SeenZ != 6f)
            {
                Console.WriteLine("FAIL: boxed position not readable x=" + PickPatches.SeenX + " z=" + PickPatches.SeenZ);
                fail++;
            }
            PickPatches.Exact = false;
            List<object> c = PickStub.Candidates(new Vec3(4f, 0f, 6f));
            if (c == null || c.Count != 1) { Console.WriteLine("FAIL: port mode not restored"); fail++; }
            if (fail == 0) Console.WriteLine("verified: skip-prefix keeps port mode untouched and reads the boxed position");

            Console.WriteLine(fail == 0 ? "PICK PROBE OK" : "PICK PROBE FAILED (" + fail + ")");
            return fail;
        }

        /// <summary>Parameterless prefix on an instance method: one increment, behaviour unchanged.</summary>
        private static int ProbeParameterlessInstancePrefix()
        {
            int fail = 0;
            Harmony harmony = new Harmony("probe.sleepsheep.agenttouchfix.evictprefix");
            MethodInfo target = typeof(TextureStub).GetMethod("Evict");
            MethodInfo prefix = typeof(EvictPatches).GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic);
            if (target == null || prefix == null) { Console.WriteLine("FAIL: evict probe scaffolding missing"); return 1; }
            try
            {
                harmony.Patch(target, new HarmonyMethod(prefix), null);
                Console.WriteLine("installed: parameterless prefix on Texture2D.Evict()");
            }
            catch (Exception e)
            {
                Console.WriteLine("FAIL: cannot patch an instance method: " + e.Message);
                return 1;
            }
            TextureStub tex = new TextureStub();
            EvictPatches.Hits = 0;
            tex.Evict();
            tex.Evict();
            if (EvictPatches.Hits != 2) { Console.WriteLine("FAIL: prefix hits=" + EvictPatches.Hits + " (2 expected)"); fail++; }
            if (tex.Evicted != 2) { Console.WriteLine("FAIL: original body did not run"); fail++; }
            if (fail == 0) Console.WriteLine("verified: counting prefix leaves the original body alone");

            Console.WriteLine(fail == 0 ? "EVICT PROBE OK" : "EVICT PROBE FAILED (" + fail + ")");
            return fail;
        }
    }
}
