using System;
using System.Reflection;
using System.Threading;
using Verse;

namespace AgentTouchFix
{
    /// <summary>
    /// v7 A: a single memory-pressure event is currently answered twice, and the second answer is
    /// what makes textures blink / turn grey.
    ///
    /// RwHost.AndroidMemoryAndGcGovernor (private static int pending / renderTrim, private static
    /// Action release):
    ///
    ///   GameBoundary()   [game thread, once per frame via AppContext "Ports1de.MemoryPressureAtFrame"]
    ///       ... release?.Invoke();                     // 1st: drops every decoded staging buffer
    ///           Volatile.Write(ref renderTrim, 1);
    ///   RenderBoundary() [render thread, frame start, from VulkanLifecycleAndSwapchainGuard.BeforeFrame]
    ///       if (Interlocked.Exchange(ref renderTrim, 0) != 0) release?.Invoke();   // 2nd
    ///
    /// The first release is fine: it happens at the game boundary, before the render thread picks up
    /// the texture uploads that were queued earlier in the frame. The second one fires on the *next*
    /// render frame, i.e. exactly when those queued uploads are being consumed - so the staging
    /// buffers that were already decoded but not yet uploaded get evicted, the upload fails or is
    /// dropped, and the texture shows as a grey block or flickers until it is re-decoded.
    ///
    /// Fix: keep the GameBoundary release, and skip a RenderBoundary release that arrives within
    /// 500 ms of one. renderTrim is NOT touched by the skip, so the render-side trim is not lost -
    /// it is deferred to the first render frame that is more than 500 ms after the pressure event,
    /// by which time the uploads of that frame have been consumed. Pause / surface / device-loss
    /// paths are untouched (a RenderBoundary with no recent GameBoundary always runs).
    /// </summary>
    internal static class GovernorPatches
    {
        private const int DuplicateWindowMs = 500;

        private static bool _resolved;
        private static FieldInfo _fPending;      // private static int pending
        private static FieldInfo _fRenderTrim;   // private static int renderTrim
        private static bool _warned;

        // Written on the game thread (GameBoundary), read on the render thread (RenderBoundary).
        private static long _lastGameBoundaryMs;   // 0 == never
        private static long _lastSkipLogMs;
        private static long _skips;

        internal const string TypeName = "RwHost.AndroidMemoryAndGcGovernor";

        private static Type _type;

        // --- v8 D: TRIM_MEMORY_UI_HIDDEN ------------------------------------------------
        // The UI-hidden callback (level 20) is the only trim level the port ignores, in both
        // MainActivity.OnTrimMemory and here. 20 is not a small level: the activity is gone from
        // the screen, which is exactly when Android expects the app to hand back everything it
        // does not need (Background == 40 is already answered). Here it is answered the same way
        // Background is - Request(1), i.e. the pending flag GameBoundary consumes - so the trim
        // goes through the one existing channel with its existing throttles, instead of a second
        // path with its own timing.
        private const int LevelUiHidden = 20;
        private const int UiHiddenWindowMs = 5000;
        private static long _lastUiHiddenMs;     // 0 == never
        private static MethodInfo _mRequest;     // public static void Request(int severity)

        /// <summary>RwHost.dll holds the copy that actually runs, so pin the assembly.</summary>
        internal static Type T()
        {
            if (_type == null) _type = Refl.TPrefer("RwHost", TypeName);
            return _type;
        }

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;
            try
            {
                Type governor = T();
                _fPending = Refl.F(governor, "pending");
                _fRenderTrim = Refl.F(governor, "renderTrim");
                _mRequest = Refl.M(governor, "Request");
            }
            catch { }
            if (_fPending == null || _fRenderTrim == null)
            {
                Refl.Warn("AndroidMemoryAndGcGovernor.pending/renderTrim not found - duplicate render trim NOT suppressed (v6 behaviour)");
            }
        }

        private static int ReadInt(FieldInfo f, int fallback)
        {
            if (f == null) return fallback;
            try { return Convert.ToInt32(f.GetValue(null)); }
            catch { return fallback; }
        }

        /// <summary>
        /// Game boundary: remember *when* a pressure trim actually ran. GameBoundary runs every frame
        /// and returns immediately when nothing is pending, so the timestamp must only be taken when
        /// the method is really going to release (pending != 0) - otherwise the render side would be
        /// suppressed on every single frame.
        /// </summary>
        internal static bool GameBoundary_Prefix()
        {
            try
            {
                Resolve();
                if (_fPending == null) return true;
                if (ReadInt(_fPending, 0) == 0) return true;
                Volatile.Write(ref _lastGameBoundaryMs, Environment.TickCount64);
            }
            catch (Exception e)
            {
                if (!_warned) { _warned = true; Refl.Warn("governor prefix failed (suppression off): " + e.Message); }
            }
            return true;
        }

        /// <summary>
        /// Render boundary: drop the second release of the same pressure event. Only an actual trim
        /// (renderTrim == 1) is skipped; when renderTrim is 0 the original method is a no-op anyway,
        /// so it is left alone and nothing is logged.
        /// </summary>
        internal static bool RenderBoundary_Prefix()
        {
            try
            {
                Resolve();
                if (_fRenderTrim == null || _fPending == null) return true;
                if (ReadInt(_fRenderTrim, 0) == 0) return true;

                long last = Volatile.Read(ref _lastGameBoundaryMs);
                if (last == 0) return true;
                long now = Environment.TickCount64;
                long delta = now - last;
                if (delta < 0 || delta >= DuplicateWindowMs) return true;

                long n = Interlocked.Increment(ref _skips);
                if (now - Volatile.Read(ref _lastSkipLogMs) >= 1000)
                {
                    Volatile.Write(ref _lastSkipLogMs, now);
                    Refl.Info("render trim skipped: " + delta + " ms after the game boundary already released the textures"
                        + " (renderTrim left set, retried after " + DuplicateWindowMs + " ms) skips=" + n);
                }
                return false;
            }
            catch (Exception e)
            {
                if (!_warned) { _warned = true; Refl.Warn("governor prefix failed (suppression off): " + e.Message); }
                return true;
            }
        }

        /// <summary>
        /// TRIM_MEMORY_UI_HIDDEN (20): the activity left the screen. The original method answers 15,
        /// 80, 10, 5, 60 and 40 but returns for 20, so the release has to be requested here - the
        /// same way 40 (Background) is, i.e. Request(1), which only sets the pending flag that
        /// GameBoundary consumes. That keeps the one existing trim channel (and its 5 s / 30 s
        /// throttles, and v7's single-release rule) instead of opening a second, differently timed one.
        ///
        /// Rate limited to one request per 5 s: the level can be delivered again while the process
        /// sits in the cached state, and Request(1) alone would then keep asking for a trim that has
        /// nothing left to release. The original method still runs (return true) - it is a no-op for
        /// this level, and suppressing it for other levels is not this patch's business.
        /// </summary>
        internal static bool OnTrimMemory_Prefix(object[] __args)
        {
            try
            {
                if (__args == null || __args.Length < 1 || __args[0] == null) return true;

                int level;
                try { level = Convert.ToInt32(__args[0]); }
                catch { return true; }
                if (level != LevelUiHidden) return true;

                long now = Environment.TickCount64;
                long last = Volatile.Read(ref _lastUiHiddenMs);
                if (last != 0 && now - last < UiHiddenWindowMs) return true;
                Volatile.Write(ref _lastUiHiddenMs, now);

                Resolve();
                if (_mRequest == null)
                {
                    if (!_warned) { _warned = true; Refl.Warn("AndroidMemoryAndGcGovernor.Request not found - TRIM_MEMORY_UI_HIDDEN(20) is not answered"); }
                    return true;
                }
                _mRequest.Invoke(null, new object[] { 1 });
                Refl.Info("TRIM_MEMORY_UI_HIDDEN(20) treated as background: pending=1, engine textures release at the next game boundary");
            }
            catch (Exception e)
            {
                if (!_warned) { _warned = true; Refl.Warn("UI-hidden trim prefix failed: " + e.Message); }
            }
            return true;
        }
    }
}
