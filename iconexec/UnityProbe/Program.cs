using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace UnityProbe
{
    /// <summary>
    /// Can the SHIPPED dlls/UnityStubs.dll do the three things the icon chain needs head-less?
    ///
    ///     1. allocate a Texture2D(w, h) and give it a name
    ///     2. resolve a shader by name (Shader.Find / ShaderDatabase)
    ///     3. build a Material from that shader and set _MainTex
    ///
    /// Each step is attempted for real. The probe PASSes when the step fails with the documented
    /// reason (the port needs the Waylith engine and its native Vulkan layer), and FAILs when the
    /// step succeeds or fails some other way - both are changes worth knowing about.
    /// </summary>
    static class Program
    {
        sealed class Expectation
        {
            public string Step;
            public bool ShouldWork;
            public Func<Exception, bool> IsDocumented;
            public string Why;
        }

        static readonly List<string> Failures = new List<string>();

        static int Main(string[] args)
        {
            string dlls = args.FirstOrDefault(a => !a.StartsWith("--")) ?? "dlls";
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                var p = Path.Combine(dlls, new AssemblyName(e.Name).Name + ".dll");
                return File.Exists(p) ? Assembly.LoadFrom(p) : null;
            };

            Console.WriteLine("[INFO] UnityStubs: " + typeof(Texture2D).Assembly.GetName().Version
                + " @ " + typeof(Texture2D).Assembly.Location);

            var steps = new List<Expectation>
            {
                new Expectation
                {
                    Step = "new Texture2D(128, 128) + SetPixel/GetPixel",
                    ShouldWork = true,
                    Why = "Texture2D is CPU-side in the port: no device is needed to allocate or address pixels",
                    IsDocumented = e => false,
                },
                new Expectation
                {
                    Step = "Shader.Find(\"CutoutComplex\")",
                    ShouldWork = false,
                    Why = "Waylith.Rendering.ShaderRegistry's static ctor needs the GLSL source tree",
                    IsDocumented = e => Mentions(e, "ShaderRegistry") || Mentions(e, ".glsl"),
                },
                new Expectation
                {
                    Step = "new Material(Shader.CreateLateBound(\"CutoutComplex\")) + SetTexture(_MainTex)",
                    ShouldWork = false,
                    Why = "Material..ctor reads Shader.WaitingForEngineRegistry -> ShaderRegistry..cctor, which needs the GLSL sources",
                    IsDocumented = e => Mentions(e, "ShaderRegistry") || Mentions(e, ".glsl"),
                },
            };

            Run(steps[0], () =>
            {
                var t = new Texture2D(128, 128) { name = "UniqueRevolverA" };
                t.SetPixel(1, 2, new Color(1f, 0f, 0f, 1f));
                var px = t.GetPixel(1, 2);
                return "w=" + t.width + " h=" + t.height + " name=" + t.name + " pixel(1,2)=" + px;
            });
            Run(steps[1], () => { var s = Shader.Find("CutoutComplex"); return s == null ? "null" : s.name; });
            Run(steps[2], () =>
            {
                var s = Shader.CreateLateBound("CutoutComplex");
                var m = new Material(s);
                m.SetTexture("_MainTex", new Texture2D(4, 4) { name = "X" });
                return "material " + m.name;
            });

            Console.WriteLine();
            if (Failures.Count == 0)
            {
                Console.WriteLine("[SUMMARY] Texture2D allocates for real on the shipped stub, but no Shader/Material can be built,");
                Console.WriteLine("[SUMMARY] so Graphic_Single.Init cannot run on it; iconexec uses the minimal stub in iconexec/Stub.");
                Console.WriteLine("RESULT: PASS");
                return 0;
            }

            foreach (var f in Failures) Console.WriteLine("[FAIL] " + f);
            Console.WriteLine("[SUMMARY] the shipped stub behaved differently than this probe documents - re-check whether iconexec/Stub is still needed");
            Console.WriteLine("RESULT: FAIL");
            return 1;
        }

        static void Run(Expectation e, Func<string> action)
        {
            Console.WriteLine();
            Console.WriteLine("## " + e.Step);
            try
            {
                string result = action();
                if (e.ShouldWork)
                {
                    Console.WriteLine("[PASS] works: " + result);
                    return;
                }
                Console.WriteLine("[FAIL] expected this to fail head-less, but it returned " + result);
                Failures.Add(e.Step + " unexpectedly succeeded");
            }
            catch (Exception ex)
            {
                var inner = (ex as TargetInvocationException)?.InnerException ?? ex;
                bool documented = e.IsDocumented(inner);
                Console.WriteLine((documented ? "[PASS] " : "[FAIL] ") + "refused: " + inner.GetType().FullName + ": " + inner.Message);
                foreach (var line in (inner.StackTrace ?? "").Split('\n').Take(4)) Console.WriteLine("        " + line.Trim());
                for (var x = inner.InnerException; x != null; x = x.InnerException)
                    Console.WriteLine("        inner: " + x.GetType().FullName + ": " + x.Message);
                if (documented) Console.WriteLine("[PASS] " + e.Why);
                else Failures.Add(e.Step + " failed for an undocumented reason: " + inner.Message);
            }
        }

        static bool Mentions(Exception e, string needle)
        {
            for (var x = e; x != null; x = x.InnerException)
                if ((x.Message ?? "").Contains(needle) || (x.GetType().FullName ?? "").Contains(needle)) return true;
            return false;
        }
    }
}
