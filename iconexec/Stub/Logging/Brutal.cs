using System;

// Brutal.Logging is the engine's logger, and Verse.Log's static ctor is:
//
//     logCategory = Brutal.Logging.LogCategory.Make("Verse");
//     Application.add_logMessageReceivedThreaded(new LogCallback(Notify_LogMessageReceived));
//
// Without it every Verse.Log.* call throws TypeInitializationException, which turns ordinary
// "asset not found" reports into a crash and hides the reason. This stub makes Log real enough to
// print, so a diagnostic in the game code survives to the CI log instead of being swallowed.
//
// Only the members Verse.Log touches are declared. Messages go to stdout with a [Verse] prefix.
namespace Brutal.Logging.Internal
{
    public struct Error { }
    public struct Warning { }
    public struct Message { }

    /// <summary>Brutal's structured log string: implicitly built from a plain string.</summary>
    public struct LogString<T>
    {
        public string Value;

        public LogString(string v) { Value = v; }

        public static implicit operator LogString<T>(string s) { return new LogString<T>(s); }

        public override string ToString() { return Value ?? ""; }
    }
}

namespace Brutal.Logging
{
    public class LogCategory
    {
        public readonly string Name;

        LogCategory(string name) { Name = name; }

        public static LogCategory Make(string name) { return new LogCategory(name); }

        static void Emit(string level, string text)
        {
            Console.WriteLine("[" + level + "] " + text);
            Console.Out.Flush();
        }

        // Verse.Log.Error calls the 4-argument shape (message, file, member, line); the shorter
        // ones are declared too because other call sites use them.
        public void Error(Internal.LogString<Internal.Error> s) { Emit("ERROR", s.Value); }
        public void Error(Internal.LogString<Internal.Error> s, string stackTrace) { Emit("ERROR", s.Value + (string.IsNullOrEmpty(stackTrace) ? "" : "\n" + stackTrace)); }
        public void Error(Internal.LogString<Internal.Error> s, string file, string member, int line) { Emit("ERROR", s.Value + "  (" + file + ":" + line + ")"); }
        public void Warning(Internal.LogString<Internal.Warning> s) { Emit("WARN", s.Value); }
        public void Warning(Internal.LogString<Internal.Warning> s, string stackTrace) { Emit("WARN", s.Value); }
        public void Warning(Internal.LogString<Internal.Warning> s, string file, string member, int line) { Emit("WARN", s.Value + "  (" + file + ":" + line + ")"); }
        public void Message(Internal.LogString<Internal.Message> s) { Emit("INFO", s.Value); }
        public void Message(Internal.LogString<Internal.Message> s, string stackTrace) { Emit("INFO", s.Value); }
        public void Message(Internal.LogString<Internal.Message> s, string file, string member, int line) { Emit("INFO", s.Value); }
    }
}
