using System;
using System.IO;
using System.Reflection;
using System.Threading;

namespace AgentTouchFix
{
    /// <summary>
    /// v11: files/game/agenttouchfix.texkeep + .texgrace + .texsmall - how much of the port's
    /// automatic texture eviction is suppressed.
    ///
    /// What the v10 counters proved (one minute at 03:04, budget 256 MiB, 4096 px limit):
    ///   decode ok=5512  shift&gt;0=0  fail=2  fallback=1  evict=6110  offload=0  hostevict=0
    /// i.e. no mip was ever reduced (the budget was never the problem) and about 120 textures per
    /// second were thrown away. That is the port's OnDemand residency working as written:
    ///
    ///   Waylith.Rendering.Texture2D.EvictWhenCached(Texture2D)  appends every texture that has a
    ///       SourcePath to the static _pendingEvictions list - with no check for "is it being drawn
    ///       this frame".
    ///   Waylith.Rendering.Texture2D.DrainPendingEvictions()     called every frame from
    ///       GpuTextureService.RetireDeferredDisposalsAfterIdle (and from TextureBuildBatch); for
    ///       each entry, as soon as GpuTextureService.HasValidTextureCache(...) is true, the GPU
    ///       resource is dropped via Texture2D.Evict().
    ///
    /// So a texture that is on screen right now gets evicted, the next sample finds no GPU resource
    /// and takes the 1x1 fallback colour, and while it re-hydrates only part of the mip chain is back
    /// - the "panel background colour plus an occasional incomplete texture" the user sees.
    ///
    /// Three modes (read once from Bootstrap):
    ///
    ///   grace (DEFAULT, also when the file is absent) - a prefix on Texture2D.Evict() decides per
    ///       eviction, on two independent grounds:
    ///         1. small textures: GpuResource.AllocatedBytes &lt;= .texsmall bytes (default 1048576,
    ///            i.e. 1 MiB, 0 disables the rule) are never evicted. Icons and small UI bits are
    ///            single small textures - evicting them only buys a re-read of the same bytes
    ///            (or a disk-cache hit) every time they are drawn again, and a few thousand of them
    ///            resident is tens of MiB.
    ///         2. hot textures: skipped while
    ///            GpuTextureService.RenderFrameSerial - GpuResource.LastUseFrame is below .texgrace
    ///            frames (default 900 = 15 s at 60 fps, clamped 60..3600). At or above it the
    ///            original runs untouched, so a cold large texture is still freed exactly as shipped.
    ///       Both numbers are read through reflection; whenever one cannot be read that rule lets the
    ///       eviction through (one Warn) - a missing number never pins memory.
    ///
    ///   on  - both EvictWhenCached and DrainPendingEvictions are short-circuited, so nothing is ever
    ///       queued and nothing queued is ever drained: every texture stays resident. Kept as the
    ///       control case for the experiment, not as a default (the resident set grows).
    ///
    ///   off - no eviction method is patched at all; the port behaves exactly as shipped.
    ///
    /// Not touched in any mode: Texture2D.Dispose / DisposeMovedAway / TryEvictForResidency /
    /// EnsureResident, the upload and release paths, TextureResidency.Offload / EvictHostResident,
    /// GpuTextureService's own eviction bookkeeping and AndroidMemoryAndGcGovernor. The v10 read-only
    /// counters stay installed in every mode.
    ///
    /// GpuResource is read through its backing field, not through the property: the property getter
    /// calls TextureBuildBatch.Resolve(this) when a build is pending, and a gate must not add a
    /// resolve to a path that did not have one.
    ///
    /// Log (at most one line per 2 s, only when a counter moved):
    ///   texkeep mode=grace grace=900 small=1.0MiB skipped=hot:N,small:N evicted=N
    /// </summary>
    internal static class TexKeep
    {
        internal const int ModeOff = 0;
        internal const int ModeGrace = 1;
        internal const int ModeOn = 2;

        internal const string ModePath = "/data/data/com.ports1de.waylith/files/game/agenttouchfix.texkeep";
        internal const string GracePath = "/data/data/com.ports1de.waylith/files/game/agenttouchfix.texgrace";
        internal const string SmallPath = "/data/data/com.ports1de.waylith/files/game/agenttouchfix.texsmall";

        internal const string TypeName = "Waylith.Rendering.Texture2D";
        internal const string ServiceName = "Waylith.Rendering.GpuTextureService";
        internal const string EngineAssembly = "Waylith";

        internal const int DefaultGrace = 900;          // 15 s at 60 fps
        internal const int MinGrace = 60;               // 1 s
        internal const int MaxGrace = 3600;             // 60 s

        internal const long DefaultSmallBytes = 1048576L;   // 1 MiB
        internal const long MaxSmallBytes = 16777216L;      // 16 MiB

        private static int _mode = ModeGrace;           // grace == ship behaviour, minus hot/small evictions
        private static int _grace = DefaultGrace;
        private static int _smallBytes = (int)DefaultSmallBytes;

        private static int _skipHot;                    // grace: suppressed because recently used
        private static int _skipSmall;                  // grace: suppressed because the allocation is small
        private static int _evicted;                    // grace: evictions let through
        private static int _queueBlocked;               // on: EvictWhenCached calls answered false
        private static int _drainBlocked;               // on: DrainPendingEvictions calls answered false

        private static long _lastLogMs = -100000L;
        private static int _warnedMeasure;              // 0 = the notice is still due

        // reflection handles, resolved once at install
        private static FieldInfo _fGpuResource;     // Texture._gpuResource
        private static PropertyInfo _pLastUse;      // GpuTextureAllocation.LastUseFrame (long)
        private static PropertyInfo _pAllocated;    // GpuTextureAllocation.AllocatedBytes (long)
        private static PropertyInfo _pFrameSerial;  // GpuTextureService.RenderFrameSerial (long, static)

        internal static int Mode { get { return _mode; } }

        internal static string ModeName(int m)
        {
            return m == ModeOn ? "on" : m == ModeOff ? "off" : "grace";
        }

        private static string Short(string s)
        {
            if (s == null) return "";
            s = s.Trim();
            return s.Length <= 20 ? s : s.Substring(0, 20) + "...";
        }

        private static string MiB(long bytes)
        {
            try { return (bytes / 1048576.0).ToString("0.0") + "MiB"; }
            catch { return "?"; }
        }

        private static string SmallName()
        {
            return _smallBytes <= 0 ? "off" : MiB(_smallBytes);
        }

        // ================================================================ switches

        /// <summary>Read once from Bootstrap, before the first texture can be evicted.</summary>
        internal static void ReadMode()
        {
            string raw = null;
            try
            {
                if (File.Exists(ModePath)) raw = File.ReadAllText(ModePath);
            }
            catch (Exception e) { Refl.Warn("texkeep switch unreadable (" + ModePath + "): " + e.Message); }

            string s = raw == null ? "" : raw.Trim().ToLowerInvariant();
            int m = ModeGrace;
            if (s.StartsWith("on") || s.StartsWith("all") || s.StartsWith("keep") || s == "1") m = ModeOn;
            else if (s.StartsWith("off") || s.StartsWith("none") || s.StartsWith("default") || s == "0") m = ModeOff;
            else if (s.StartsWith("grace")) m = ModeGrace;
            else if (s.Length > 0) Refl.Warn("texkeep switch '" + Short(s) + "' not understood (grace/on/off) - using grace");

            _mode = m;
            _grace = ReadGrace();
            _smallBytes = ReadSmall();

            string what;
            if (_mode == ModeGrace)
            {
                what = "allocations up to " + SmallName() + " and textures used within " + _grace
                    + " frames are not evicted, everything else runs as shipped ("
                    + GracePath + " " + MinGrace + ".." + MaxGrace + " frames, " + SmallPath + " 0.." + MiB(MaxSmallBytes) + ")";
            }
            else if (_mode == ModeOn)
            {
                what = "EvictWhenCached + DrainPendingEvictions short-circuited, every texture stays resident"
                    + " (control case, the resident set grows)";
            }
            else
            {
                what = "no eviction method patched, the port behaves as shipped";
            }

            Refl.Info("texkeep mode=" + ModeName(m) + " grace=" + (_mode == ModeGrace ? _grace : 0)
                + " small=" + (_mode == ModeGrace ? SmallName() : "off")
                + " (" + (raw == null ? "no " + ModePath + ", default grace" : ModePath + " = '" + Short(s) + "'")
                + "; " + what + ")");
        }

        /// <summary>frames of protection; unreadable / out of range falls back to the default.</summary>
        private static int ReadGrace()
        {
            string raw = ReadFlag(GracePath, "texgrace");
            if (raw == null) return DefaultGrace;

            int v;
            if (!int.TryParse(raw, out v))
            {
                Refl.Warn("texgrace '" + Short(raw) + "' is not an integer frame count - using " + DefaultGrace);
                return DefaultGrace;
            }
            if (v < MinGrace || v > MaxGrace)
            {
                int c = v < MinGrace ? MinGrace : MaxGrace;
                Refl.Warn("texgrace " + v + " out of range " + MinGrace + ".." + MaxGrace + " - using " + c);
                return c;
            }
            return v;
        }

        /// <summary>byte threshold for the small-texture exemption; 0 switches the rule off.</summary>
        private static int ReadSmall()
        {
            string raw = ReadFlag(SmallPath, "texsmall");
            if (raw == null) return (int)DefaultSmallBytes;

            long v;
            if (!long.TryParse(raw, out v))
            {
                Refl.Warn("texsmall '" + Short(raw) + "' is not a byte count - using " + DefaultSmallBytes);
                return (int)DefaultSmallBytes;
            }
            if (v < 0L || v > MaxSmallBytes)
            {
                long c = v < 0L ? 0L : MaxSmallBytes;
                Refl.Warn("texsmall " + v + " out of range 0.." + MaxSmallBytes + " - using " + c + " (" + MiB(c) + ")");
                return (int)c;
            }
            return (int)v;
        }

        private static string ReadFlag(string path, string label)
        {
            try
            {
                if (!File.Exists(path)) return null;
                string raw = File.ReadAllText(path);
                if (raw == null) return null;
                raw = raw.Trim();
                return raw.Length == 0 ? null : raw;
            }
            catch (Exception e)
            {
                Refl.Warn(label + " unreadable (" + path + "): " + e.Message);
                return null;
            }
        }

        // ================================================================ grace: Texture2D.Evict

        /// <summary>
        /// Waylith.Rendering.Texture2D.Evict() - public instance, no parameters, void. Skipped for a
        /// small allocation (GpuResource.AllocatedBytes &lt;= texsmall) and, independently, while the
        /// texture was drawn within the last texgrace frames. Evict() is the single place a GPU
        /// resource is dropped for eviction (DrainPendingEvictions and TryEvictForResidency both call
        /// it), so one gate covers the queue path and the residency path without patching either.
        ///
        /// Every measurement failure returns true: a number that cannot be read must never pin memory.
        /// Priority is High (see install), so this prefix runs before the v10 counter prefix and a
        /// suppressed eviction is not counted as one.
        /// </summary>
        internal static bool Evict_Prefix(object __instance)
        {
            if (_mode != ModeGrace) return true;
            try
            {
                object alloc = (_fGpuResource == null || __instance == null) ? null : _fGpuResource.GetValue(__instance);
                if (alloc == null) return true;                  // nothing to free: the original no-ops

                // 1) small textures are never evicted (they are cheap to keep, pointless to reload)
                long bytes = ReadAllocated(alloc);
                if (_smallBytes > 0 && bytes >= 0L && bytes <= _smallBytes)
                {
                    Interlocked.Increment(ref _skipSmall);
                    LogThrottled();
                    return false;
                }

                // 2) textures drawn within the grace window are not evicted either
                long frame, last;
                if (!TryMeasure(alloc, out frame, out last)) return true;
                if (frame - last < _grace)
                {
                    Interlocked.Increment(ref _skipHot);
                    LogThrottled();
                    return false;
                }

                Interlocked.Increment(ref _evicted);
                LogThrottled();
                return true;
            }
            catch { return true; }
        }

        /// <summary>RenderFrameSerial - LastUseFrame; false means "cannot tell" (evict as shipped).</summary>
        private static bool TryMeasure(object alloc, out long frame, out long last)
        {
            frame = 0L; last = -1L;
            try
            {
                if (alloc == null || _pLastUse == null || _pFrameSerial == null)
                {
                    WarnMeasureOnce("LastUseFrame/RenderFrameSerial not resolvable");
                    return false;
                }
                object f = _pFrameSerial.GetValue(null, null);
                object l = _pLastUse.GetValue(alloc, null);
                if (f == null || l == null)
                {
                    WarnMeasureOnce("LastUseFrame/RenderFrameSerial returned null");
                    return false;
                }
                frame = Convert.ToInt64(f);
                last = Convert.ToInt64(l);
                if (last < 0L) return false;                     // never used -> not hot
                return true;
            }
            catch (Exception e)
            {
                WarnMeasureOnce("LastUseFrame/RenderFrameSerial: " + e.Message);
                return false;
            }
        }

        /// <summary>GpuResource.AllocatedBytes, or -1 when it cannot be read (the rule then does not fire).</summary>
        private static long ReadAllocated(object alloc)
        {
            try
            {
                if (alloc == null || _pAllocated == null) return -1L;
                object v = _pAllocated.GetValue(alloc, null);
                if (v == null) return -1L;
                return Convert.ToInt64(v);
            }
            catch (Exception e)
            {
                WarnMeasureOnce("AllocatedBytes: " + e.Message);
                return -1L;
            }
        }

        private static void WarnMeasureOnce(string why)
        {
            if (Interlocked.Exchange(ref _warnedMeasure, 1) == 0)
                Refl.Warn("texkeep: " + why + " - that rule is off, its evictions are let through");
        }

        // ================================================================ on: the two short circuits

        /// <summary>
        /// EvictWhenCached(Texture2D) - static, one parameter, void. The original only does "if
        /// OnDemand and SourcePath != null: queue for eviction", so skipping it keeps the texture out
        /// of _pendingEvictions entirely.
        /// </summary>
        internal static bool EvictWhenCached_Prefix()
        {
            if (_mode != ModeOn) return true;
            try
            {
                Interlocked.Increment(ref _queueBlocked);
                LogThrottled();
            }
            catch { }
            return false;
        }

        /// <summary>
        /// DrainPendingEvictions() - static, no parameters, void. Skips the per-frame drain, so a list
        /// that still holds entries cannot be turned into Evict() calls.
        /// </summary>
        internal static bool DrainPendingEvictions_Prefix()
        {
            if (_mode != ModeOn) return true;
            try
            {
                Interlocked.Increment(ref _drainBlocked);
                LogThrottled();
            }
            catch { }
            return false;
        }

        // ================================================================ log

        /// <summary>
        /// At most one line per 2 s, and only when a counter actually moved:
        ///   texkeep mode=grace grace=900 small=1.0MiB skipped=hot:N,small:N evicted=N
        ///   texkeep mode=on grace=0 small=off skipped=hot:0,small:0 evicted=0 queue-blocked=N drain-blocked=N
        /// </summary>
        private static void LogThrottled()
        {
            long now;
            try { now = Environment.TickCount64; } catch { now = 0L; }
            if (now - _lastLogMs < 2000L) return;
            _lastLogMs = now;

            Refl.Info("texkeep mode=" + ModeName(_mode)
                + " grace=" + (_mode == ModeGrace ? _grace : 0)
                + " small=" + (_mode == ModeGrace ? SmallName() : "off")
                + " skipped=hot:" + Volatile.Read(ref _skipHot) + ",small:" + Volatile.Read(ref _skipSmall)
                + " evicted=" + Volatile.Read(ref _evicted)
                + (_mode == ModeOn
                    ? " queue-blocked=" + Volatile.Read(ref _queueBlocked)
                      + " drain-blocked=" + Volatile.Read(ref _drainBlocked)
                    : ""));
        }

        // ================================================================ install

        /// <summary>
        /// Called last from Bootstrap. Every candidate target is resolved and shape-checked in every
        /// mode (so the log always states whether the switch is applicable to this build); only the
        /// current mode's methods are patched.
        /// </summary>
        internal static void Install()
        {
            Type tex = Resolve(TypeName);
            Type service = Resolve(ServiceName);

            MethodInfo evict = tex == null ? null : Refl.M(tex, "Evict");
            MethodInfo whenCached = tex == null ? null : Refl.M(tex, "EvictWhenCached");
            MethodInfo drain = tex == null ? null : Refl.M(tex, "DrainPendingEvictions");

            bool evictOk = Check(evict, false, 0, tex, "Evict");
            bool whenOk = Check(whenCached, true, 1, tex, "EvictWhenCached");
            bool drainOk = Check(drain, true, 0, null, "DrainPendingEvictions");

            // grace prerequisites: the numbers the gate decides on
            bool hotOk, smallOk;
            ResolveMeasure(tex, service, out hotOk, out smallOk);

            int want = _mode == ModeGrace ? 1 : _mode == ModeOn ? 2 : 0;
            int ok = 0;
            if (_mode == ModeGrace)
            {
                if (!hotOk && !smallOk)
                {
                    Refl.Warn("texkeep: mode=grace but neither the small-size nor the frame/usage numbers are"
                        + " resolvable - Evict is NOT patched, evictions run exactly as shipped");
                }
                else if (Patch("Waylith.Rendering.Texture2D.Evict (grace gate)", evict, "Evict_Prefix", 700))
                {
                    ok++;
                }
            }
            else if (_mode == ModeOn)
            {
                if (whenOk && Patch("Waylith.Rendering.Texture2D.EvictWhenCached (no queue)", whenCached, "EvictWhenCached_Prefix", -1)) ok++;
                if (drainOk && Patch("Waylith.Rendering.Texture2D.DrainPendingEvictions (no drain)", drain, "DrainPendingEvictions_Prefix", -1)) ok++;
            }

            Refl.Info("texkeep: mode=" + ModeName(_mode) + " patched=" + ok + "/" + want
                + " (Evict " + (evictOk ? "ok" : "-")
                + ", EvictWhenCached " + (whenOk ? "ok" : "-")
                + ", DrainPendingEvictions " + (drainOk ? "ok" : "-")
                + ", LastUseFrame/RenderFrameSerial " + (hotOk ? "ok" : "-")
                + ", AllocatedBytes " + (smallOk ? "ok" : "-") + ")"
                + (_mode == ModeOff ? " - nothing patched by the texkeep switch" : ""));
        }

        /// <summary>Waylith!&lt;full name&gt;, or null (a Warn, never a guess).</summary>
        private static Type Resolve(string fullName)
        {
            Type t = null;
            try
            {
                Assembly a = Assembly.Load(EngineAssembly);
                if (a != null) t = a.GetType(fullName, false);
            }
            catch (Exception e) { Refl.Warn("texkeep: Assembly.Load(" + EngineAssembly + ") failed: " + e.Message); }

            if (t == null) t = Refl.T(fullName);

            if (t == null)
            {
                Refl.Warn("texkeep: type " + fullName + " not found - switch skipped");
                return null;
            }
            if (t.FullName != fullName)
            {
                Refl.Warn("texkeep: resolved " + t.FullName + " instead of " + fullName + " - switch skipped");
                return null;
            }
            return t;
        }

        /// <summary>
        /// Caches Texture._gpuResource (backing field, side-effect free),
        /// GpuTextureAllocation.LastUseFrame / AllocatedBytes and GpuTextureService.RenderFrameSerial.
        /// The two rules are resolved independently: losing AllocatedBytes must not cost the hot rule.
        /// </summary>
        private static void ResolveMeasure(Type tex, Type service, out bool hotOk, out bool smallOk)
        {
            hotOk = false;
            smallOk = false;
            try
            {
                _fGpuResource = tex == null ? null : FieldUp(tex, "_gpuResource");
                if (_fGpuResource == null)
                {
                    Refl.Warn("texkeep: Texture._gpuResource not found - cannot read a texture's allocation");
                    return;
                }

                Type alloc = _fGpuResource.FieldType;
                if (alloc == null)
                {
                    Refl.Warn("texkeep: _gpuResource has no field type");
                    return;
                }

                _pAllocated = alloc.GetProperty("AllocatedBytes",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (_pAllocated == null)
                    Refl.Warn("texkeep: " + alloc.FullName + ".AllocatedBytes not found - small-texture exemption off");
                else smallOk = true;

                _pLastUse = alloc.GetProperty("LastUseFrame",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (_pLastUse == null)
                    Refl.Warn("texkeep: " + alloc.FullName + ".LastUseFrame not found - no hot-texture protection");

                _pFrameSerial = service == null
                    ? null
                    : service.GetProperty("RenderFrameSerial",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (_pFrameSerial == null)
                    Refl.Warn("texkeep: " + ServiceName + ".RenderFrameSerial not found - no hot-texture protection");

                hotOk = _pLastUse != null && _pFrameSerial != null;

                if (hotOk || smallOk)
                {
                    Refl.Info("texkeep: grace numbers (Texture._gpuResource -> " + alloc.FullName
                        + ": LastUseFrame " + (hotOk ? "ok" : "-")
                        + ", AllocatedBytes " + (smallOk ? "ok" : "-")
                        + "; " + ServiceName + ".RenderFrameSerial " + (_pFrameSerial != null ? "ok" : "-") + ")");
                }
            }
            catch (Exception e)
            {
                Refl.Warn("texkeep: grace numbers could not be resolved: " + e.Message);
            }
        }

        /// <summary>Private field, walking up the base types (Texture._gpuResource is not on Texture2D).</summary>
        private static FieldInfo FieldUp(Type t, string name)
        {
            try
            {
                for (Type c = t; c != null; c = c.BaseType)
                {
                    FieldInfo f = c.GetField(name,
                        BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                    if (f != null) return f;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Static/instance, parameter count, and - for the two single-texture methods - that the
        /// parameter is the very type resolved above (a texture from another assembly would mean the
        /// name resolution landed somewhere else entirely).
        /// </summary>
        private static bool Check(MethodInfo m, bool isStatic, int parameters, Type selfType, string label)
        {
            if (m == null)
            {
                Refl.Warn("texkeep: " + label + " not found on " + TypeName + " - not patched");
                return false;
            }
            try
            {
                if (m.IsStatic != isStatic)
                {
                    Refl.Warn("texkeep: " + label + " is " + (m.IsStatic ? "static" : "instance")
                        + " (" + (isStatic ? "static" : "instance") + " expected) - not patched");
                    return false;
                }
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length != parameters)
                {
                    Refl.Warn("texkeep: " + label + " has " + ps.Length + " parameters (" + parameters
                        + " expected) - not patched");
                    return false;
                }
                if (selfType != null && ps.Length == 1 && ps[0].ParameterType != selfType)
                {
                    Refl.Warn("texkeep: " + label + " takes " + ps[0].ParameterType.FullName + " ("
                        + TypeName + " expected) - not patched");
                    return false;
                }
                Refl.Info("texkeep: " + label + " signature ok (" + (m.IsStatic ? "static" : "instance")
                    + ", " + ps.Length + " param" + (ps.Length == 1 ? " " + ps[0].ParameterType.FullName : "")
                    + ", returns " + m.ReturnType.Name + ")");
                return true;
            }
            catch (Exception e)
            {
                Refl.Warn("texkeep: " + label + " could not be inspected: " + e.Message);
                return false;
            }
        }

        private static bool Patch(string label, MethodBase original, string prefixName, int priority)
        {
            try
            {
                if (original == null) { Refl.Warn("texkeep: no target for " + label); return false; }
                MethodInfo prefix = typeof(TexKeep).GetMethod(prefixName,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (prefix == null)
                {
                    Refl.Warn("texkeep: patch method " + prefixName + " missing - not patched");
                    return false;
                }
                Bootstrap.H.Patch(original, Refl.At(prefix, priority), null);
                Refl.Info("texkeep: patched " + label + " -> "
                    + original.DeclaringType.Assembly.GetName().Name + "!" + original.DeclaringType.FullName
                    + "." + original.Name);
                return true;
            }
            catch (Exception e)
            {
                Refl.Warn("texkeep: patch SKIPPED " + label + ": " + e.Message);
                return false;
            }
        }
    }
}
