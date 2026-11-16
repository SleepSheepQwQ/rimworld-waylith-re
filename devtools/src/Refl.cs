using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using Verse;

namespace AgentDevTools
{
    /// <summary>
    /// File-only logging plus the reflection helpers the engine layer uses.
    ///
    /// Rationale (part of the design philosophy): this mod must never produce dialogs or console spam.
    /// Verse.Log.Warning/Error open the in-game "there were errors" dialog on this port and Log.Message
    /// floods the console, so every routine line goes to a file and in-game output is reserved for a
    /// single genuine failure per process.
    /// </summary>
    internal static class Refl
    {
        internal const string P = "[AgentDevTools] ";

        internal static readonly string LogPath =
            "/data/data/com.ports1de.waylith/files/game/agentdevtools.log";

        private static readonly object _lock = new object();
        private static StreamWriter _w;
        private static long _written;
        private static int _errLogged;

        internal static void Info(string m) { Write("INFO", m); }
        internal static void Warn(string m) { Write("WARN", m); }

        internal static void Err(string m) { Write("ERROR", m); }

        internal static void ErrOnce(string m)
        {
            Write("ERROR", m);
            if (Interlocked.Exchange(ref _errLogged, 1) == 0)
            {
                // Log.Error opens the port's "there were errors" dialog; a single Log.Message is
                // visible in the console without interrupting the player.
                try { Log.Message(P + m); } catch { }
            }
        }

        private static void Write(string level, string m)
        {
            try
            {
                lock (_lock)
                {
                    if (_w == null) Open();
                    if (_w == null) return;
                    string line = DateTime.Now.ToString("HH:mm:ss.fff") + " " + level + " " + m;
                    _w.WriteLine(line);
                    _w.Flush();
                    _written += line.Length + 2;
                    if (_written > 1024L * 1024L) { Rotate(); }
                }
            }
            catch { }
        }

        private static void Open()
        {
            try
            {
                string dir = Path.GetDirectoryName(LogPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var fs = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                _w = new StreamWriter(fs, new UTF8Encoding(false));
                _written = fs.Length;
            }
            catch { _w = null; }
        }

        /// <summary>Keep exactly one previous log; the current file never grows without bound.</summary>
        private static void Rotate()
        {
            Close();
            try
            {
                if (File.Exists(LogPath))
                {
                    string prev = LogPath + ".1";
                    if (File.Exists(prev)) File.Delete(prev);
                    File.Move(LogPath, prev);
                }
            }
            catch { }
            _written = 0;
        }

        private static void Close()
        {
            try { _w?.Flush(); _w?.Dispose(); } catch { }
            _w = null;
            _written = 0;
        }

        internal static Type T(string fullName)
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    Type t = a.GetType(fullName, false);
                    if (t != null) return t;
                }
                catch { }   // one broken assembly must not abort the whole scan
            }
            return null;
        }

        /// <summary>Resolve a type in a named assembly first, then anywhere.</summary>
        internal static Type TPrefer(string assemblyName, string fullName)
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (!string.Equals(a.GetName().Name, assemblyName, StringComparison.Ordinal)) continue;
                    Type t = a.GetType(fullName, false);
                    if (t != null) return t;
                }
                catch { }
            }
            return T(fullName);
        }

        internal static MethodInfo M(Type t, string name, int argc = -1)
        {
            if (t == null) return null;
            foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                                  BindingFlags.Instance | BindingFlags.Static))
            {
                if (m.Name != name) continue;
                if (argc >= 0 && m.GetParameters().Length != argc) continue;
                return m;
            }
            return null;
        }

        /// <summary>
        /// Strict lookup: name + parameter type names (short names, e.g. "Vector3", "Boolean").
        /// Used so a new overload in a future port version cannot be patched by accident.
        /// </summary>
        internal static MethodInfo M(Type t, string name, string[] paramTypeNames)
        {
            if (t == null) return null;
            foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                                  BindingFlags.Instance | BindingFlags.Static))
            {
                if (m.Name != name) continue;
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length != paramTypeNames.Length) continue;
                bool okAll = true;
                for (int i = 0; i < ps.Length; i++)
                {
                    if (ps[i].ParameterType.Name != paramTypeNames[i]) { okAll = false; break; }
                }
                if (okAll) return m;
            }
            return null;
        }

        internal static FieldInfo F(Type t, string name)
        {
            if (t == null) return null;
            for (Type c = t; c != null; c = c.BaseType)
            {
                FieldInfo f = c.GetField(name, BindingFlags.Public | BindingFlags.NonPublic |
                                               BindingFlags.Instance | BindingFlags.Static);
                if (f != null) return f;
            }
            return null;
        }

        internal static PropertyInfo Prop(Type t, string name)
        {
            if (t == null) return null;
            for (Type c = t; c != null; c = c.BaseType)
            {
                PropertyInfo p = c.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic |
                                                     BindingFlags.Instance | BindingFlags.Static);
                if (p != null) return p;
            }
            return null;
        }
    }
}
