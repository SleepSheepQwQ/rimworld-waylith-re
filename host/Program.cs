using System;
using System.IO;
using System.Linq;

namespace Host
{
    // Entry point for the static assertion suite. Runs entirely on metadata from dlls/:
    // no game runtime, no GPU, no Unity. Exit code is the test result (see Gate.RunAll).
    static class Program
    {
        static int Main(string[] args)
        {
            string dir = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
            bool forceFail = args.Contains("--force-fail");
            if (string.IsNullOrEmpty(dir)) dir = FindDllsDir();
            if (dir == null)
            {
                Console.WriteLine("[FATAL] could not locate dlls/Assembly-CSharp.dll; pass the dll directory as the first argument");
                return 2;
            }
            return Gate.RunAll(Path.GetFullPath(dir), forceFail);
        }

        static string FindDllsDir()
        {
            foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            {
                string d = start;
                for (int i = 0; i < 6 && !string.IsNullOrEmpty(d); i++)
                {
                    string cand = Path.Combine(d, "dlls");
                    if (File.Exists(Path.Combine(cand, "Assembly-CSharp.dll"))) return cand;
                    d = Path.GetDirectoryName(d);
                }
            }
            return null;
        }
    }
}
