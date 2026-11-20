using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace AtlasPreloadProbe
{
    /// <summary>
    /// No-game, no-GPU probe for the pre-bake selection. Loads the shipped Assembly-CSharp and
    /// UnityStubs from --dlls, then runs the same AtlasPreloadSelfTest the mod runs at startup.
    /// Exit 0 = every assertion held; 1 = a case failed; 2 = the assemblies could not be loaded.
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            string dir = Arg(args, "--dlls") ?? Path.Combine(Directory.GetCurrentDirectory(), "dlls");
            dir = Path.GetFullPath(dir);
            if (!Directory.Exists(dir))
            {
                Console.Error.WriteLine($"[ATLASFIX] probe FATAL dlls missing: {dir}");
                return 2;
            }

            AssemblyLoadContext.Default.Resolving += (context, name) =>
            {
                string candidate = Path.Combine(dir, name.Name + ".dll");
                return File.Exists(candidate)
                    ? AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate)
                    : null;
            };

            try
            {
                Console.WriteLine($"[ATLASFIX] probe dlls={dir}");
                int failures = AtlasPreloadFix.AtlasPreloadSelfTest.Run(Console.WriteLine);
                Console.WriteLine($"[ATLASFIX] probe result={(failures == 0 ? "PASS" : "FAIL")} fail={failures}");
                return failures == 0 ? 0 : 1;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[ATLASFIX] probe FATAL {e.GetType().Name}: {e.Message}");
                Console.Error.WriteLine(e.StackTrace);
                return 2;
            }
        }

        private static string Arg(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == name)
                {
                    return args[i + 1];
                }
            }
            return null;
        }
    }
}
