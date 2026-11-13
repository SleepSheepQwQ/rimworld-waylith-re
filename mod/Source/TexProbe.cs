using System;
using System.Reflection;
using System.Threading;

namespace AgentTouchFix
{
    /// <summary>
    /// v10: READ-ONLY texture instrumentation, used to find out which of the port's texture paths
    /// actually runs when the "grey / flickering / scrambled texture" fault reproduces.
    ///
    /// Not one of these patches changes behaviour: every one of them is a prefix or a postfix that
    /// only reads values and bumps a counter. No __result is written, no argument is touched, no
    /// governor / memory method is patched (the v9 rule still holds - see BUILD.md).
    ///
    /// Sites and what they answer:
    ///   Ports1de.DdsFallback.TryPlan       (prefix)  budget + maxDimension the caller asked for.
    ///                                      (postfix) whether a plan was produced, and its
    ///                                      Format / Width / Height / Shift / SourceMip.
    ///   Ports1de.DdsFallback.TryConvert    (postfix) did the software decode succeed, how many bytes
    ///                                      did the converted DDS take (used), and - when the plan
    ///                                      postfix could not be installed - the shift implied by the
    ///                                      input vs. converted pixel width.
    ///   Waylith.Rendering.Texture2D.Evict  (prefix)  how often a texture is actually evicted.
    ///   Waylith.Rendering.GpuTextureService.GetOrCreateFallbackTexture
    ///                                      (prefix)  how often the 1x1 white/grey/black stand-in is
    ///                                      handed out - the "this texture is gone" signal.
    ///   Waylith.Rendering.TextureResidency.Offload / EvictHostResident
    ///                                      (prefix)  residency pressure: how often the engine moves
    ///                                      textures to system memory and how often it evicts the
    ///                                      host-resident copies.
    ///
    /// Every 30 s one summary line is appended to files/game/agenttouchfix.log:
    ///   tex: decode ok=N shift>0=N fail=N fallback=N evict=N offload=N budget=&lt;MiB&gt; limit=&lt;px&gt; hostevict=N
    /// plus a one-off detail line the first time a down-shifted plan is produced and the first time
    /// a decode fails. Nothing here calls Verse.Log - the file sink in Refl does the writing.
    ///
    /// The counters are always on (they cost an increment on cold paths); unlike DiagPatches they
    /// are not gated by files/game/agenttouchfix.diag.
    /// </summary>
    internal static class TexProbe
    {
        // ---------------------------------------------------------------- counters
        private static int _planCalls, _planOk, _planFail;
        private static int _convCalls, _convOk, _convFail;
        private static int _shiftFromPlan, _shiftFromConvert;
        private static int _fallback, _evict, _offload, _hostEvict;
        private static int _budget, _limit;
        private static int _firstShift, _firstFail;          // 0 = the detail line is still due
        private static bool _planPostfix;                    // plan values are readable per call

        // internal, not private: the timer must stay rooted for its whole life (a collected Timer
        // stops firing), and an internal field is also never reported as an unused private field.
        internal static Timer SummaryTimer;

        private const int PeriodMs = 30000;

        // ---------------------------------------------------------------- patch methods

        /// <summary>Ports1de.DdsFallback.TryPlan(data, budget, out plan, maxDimension) - the budget.</summary>
        internal static void TryPlan_Prefix(int budget, int maxDimension)
        {
            try
            {
                Interlocked.Increment(ref _planCalls);
                if (budget > 0) _budget = budget;
                if (maxDimension > 0) _limit = maxDimension;
            }
            catch { }
        }

        /// <summary>
        /// The plan the decoder settled on: its own Width/Height/Shift answer "did the budget force
        /// a smaller mip" directly, without guessing from the converted buffer.
        /// </summary>
        internal static void TryPlan_Postfix(bool __result, object plan)
        {
            try
            {
                if (!__result || plan == null)
                {
                    Interlocked.Increment(ref _planFail);
                    return;
                }
                Interlocked.Increment(ref _planOk);

                ResolvePlan(plan.GetType());
                int fmt = Int(plan, _pFormat, _fFormat);
                int w = Int(plan, _pWidth, _fWidth);
                int h = Int(plan, _pHeight, _fHeight);
                int shift = Int(plan, _pShift, _fShift);
                int srcMip = Int(plan, _pSourceMip, _fSourceMip);
                if (shift < 0) return;

                if (shift > 0)
                {
                    Interlocked.Increment(ref _shiftFromPlan);
                    if (Interlocked.Exchange(ref _firstShift, 1) == 0)
                    {
                        Refl.Info("tex detail shift>0: plan format=" + fmt + " " + w + "x" + h
                            + " shift=" + shift + " srcMip=" + srcMip
                            + " budget=" + _budget + "B (" + MiB(_budget) + " MiB) limit=" + _limit + "px");
                    }
                }
            }
            catch (Exception e) { Refl.Warn("tex probe TryPlan postfix: " + e.Message); }
        }

        /// <summary>
        /// TryConvert(input, length, budget, decoder, out converted, out used, pool, maxDimension).
        ///
        /// Parameters are bound BY NAME and the two out values are declared 'ref' only so that
        /// Harmony hands over the post-call values. They are never assigned, so the write-back
        /// Harmony performs for a ref parameter puts the same reference / the same number back.
        ///
        /// NOT object[] __args: tests/HarmonyProbe shows that Harmony builds __args *before* the
        /// call and copies it back over the arguments afterwards, so a postfix that does not
        /// rewrite __args hands the caller the pre-call values - converted = null, used = 0, i.e.
        /// the texture would silently lose its decoded buffer. Never use __args on a method with
        /// out parameters.
        /// </summary>
        internal static void TryConvert_Postfix(bool __result, byte[] input, int length, int budget,
            ref byte[] converted, ref int used, int maxDimension)
        {
            try
            {
                Interlocked.Increment(ref _convCalls);

                if (budget > 0) _budget = budget;
                if (maxDimension > 0) _limit = maxDimension;

                int inFmt, inW, inH, inMips;
                Header(input, length, out inFmt, out inW, out inH, out inMips);

                if (__result)
                {
                    Interlocked.Increment(ref _convOk);
                    if (_planPostfix) return;                  // the plan already reported the shift

                    int outFmt, outW, outH, outMips;
                    Header(converted, used > 0 ? used : -1, out outFmt, out outW, out outH, out outMips);
                    int shift = ShiftOf(inW, outW);
                    if (shift > 0)
                    {
                        Interlocked.Increment(ref _shiftFromConvert);
                        if (Interlocked.Exchange(ref _firstShift, 1) == 0)
                        {
                            Refl.Info("tex detail shift>0: convert format=" + inFmt + " " + inW + "x" + inH
                                + " -> " + outW + "x" + outH + " shift=" + shift
                                + " used=" + used + "B budget=" + _budget + "B (" + MiB(_budget) + " MiB) limit=" + _limit + "px");
                        }
                    }
                }
                else
                {
                    Interlocked.Increment(ref _convFail);
                    if (Interlocked.Exchange(ref _firstFail, 1) == 0)
                    {
                        Refl.Info("tex detail decode-fail: format=" + inFmt + " " + inW + "x" + inH
                            + " mips=" + inMips + " bytes=" + length
                            + " budget=" + _budget + "B (" + MiB(_budget) + " MiB) limit=" + _limit + "px"
                            + " (texture stays BC/DXT - this is the path that needs the fallback colour)");
                    }
                }
            }
            catch (Exception e) { Refl.Warn("tex probe TryConvert postfix: " + e.Message); }
        }

        /// <summary>Waylith.Rendering.Texture2D.Evict() - a real eviction (GpuResource dropped).</summary>
        internal static void Evict_Prefix()
        {
            Interlocked.Increment(ref _evict);
        }

        /// <summary>GpuTextureService.GetOrCreateFallbackTexture() - the solid-colour stand-in was used.</summary>
        internal static void Fallback_Prefix()
        {
            Interlocked.Increment(ref _fallback);
        }

        /// <summary>TextureResidency.Offload() - textures moved out (disk cache / system memory).</summary>
        internal static void Offload_Prefix()
        {
            Interlocked.Increment(ref _offload);
        }

        /// <summary>TextureResidency.EvictHostResident() - host-resident copies evicted.</summary>
        internal static void HostEvict_Prefix()
        {
            Interlocked.Increment(ref _hostEvict);
        }

        // ---------------------------------------------------------------- install

        /// <summary>Called last from Bootstrap. Every target is independent; a missing one is a Warn.</summary>
        internal static void Install()
        {
            int ok = 0, skipped = 0, failed = 0;

            // --- the two decode entry points (Ports1de.DdsFallback lives in RwHost.dll) ---
            Type dds = Refl.TPrefer("RwHost", "Ports1de.DdsFallback");
            if (dds == null) dds = Refl.T("Ports1de.DdsFallback");

            Patch("Ports1de.DdsFallback.TryPlan (prefix)",
                () => Refl.M(dds, "TryPlan"), MI("TryPlan_Prefix"), null, ref ok, ref skipped, ref failed);

            // object plan: Harmony binds the out parameter by name and hands the reference over
            // as-is. If this shape is rejected, only the plan detail is lost - the summary keeps
            // working from TryConvert (tests/HarmonyProbe proves the shape).
            bool planPost = Patch("Ports1de.DdsFallback.TryPlan (postfix)",
                () => Refl.M(dds, "TryPlan"), null, MI("TryPlan_Postfix"), ref ok, ref skipped, ref failed);
            _planPostfix = planPost;
            if (!planPost && ok > 0)
                Refl.Warn("tex: TryPlan postfix not installed - shift is derived from the converted width instead");

            Patch("Ports1de.DdsFallback.TryConvert",
                () => Refl.M(dds, "TryConvert"), null, MI("TryConvert_Postfix"), ref ok, ref skipped, ref failed);

            // --- the engine side (Waylith.dll - resolved by name, never referenced) ---
            Patch("Waylith.Rendering.Texture2D.Evict",
                () => Refl.M(Refl.T("Waylith.Rendering.Texture2D"), "Evict"),
                MI("Evict_Prefix"), null, ref ok, ref skipped, ref failed);

            Patch("Waylith.Rendering.GpuTextureService.GetOrCreateFallbackTexture",
                () => Refl.M(Refl.T("Waylith.Rendering.GpuTextureService"), "GetOrCreateFallbackTexture"),
                MI("Fallback_Prefix"), null, ref ok, ref skipped, ref failed);

            Patch("Waylith.Rendering.TextureResidency.Offload",
                () => Refl.M(Refl.T("Waylith.Rendering.TextureResidency"), "Offload"),
                MI("Offload_Prefix"), null, ref ok, ref skipped, ref failed);

            Patch("Waylith.Rendering.TextureResidency.EvictHostResident",
                () => Refl.M(Refl.T("Waylith.Rendering.TextureResidency"), "EvictHostResident"),
                MI("HostEvict_Prefix"), null, ref ok, ref skipped, ref failed);

            Refl.Info("tex: probes ok=" + ok + " skipped=" + skipped + " failed=" + failed
                + " planpostfix=" + (_planPostfix ? "on" : "off")
                + (ok > 0 ? " - read-only counters, every 30 s one summary line" : " - nothing installed, no texture data will be logged"));

            if (ok <= 0) return;

            try
            {
                // 30 s repeating summary; a background timer, so the log keeps a heartbeat even when
                // the game sits in a menu and no texture event happens.
                SummaryTimer = new Timer(OnTick, null, PeriodMs, PeriodMs);
                Refl.Info("tex: summary timer started (" + (PeriodMs / 1000) + " s)");
            }
            catch (Exception e) { Refl.Warn("tex: summary timer could not start: " + e.Message); }
        }

        private static MethodInfo MI(string name)
        {
            try { return typeof(TexProbe).GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static); }
            catch { return null; }
        }

        /// <summary>Returns true when the patch was installed (false for skipped or failed).</summary>
        private static bool Patch(string label, Func<MethodBase> target, MethodInfo prefix, MethodInfo postfix,
            ref int ok, ref int skipped, ref int failed)
        {
            try
            {
                MethodBase original = target();
                if (original == null)
                {
                    Refl.Warn("tex: skipped (target not found): " + label);
                    skipped++;
                    return false;
                }
                if (prefix == null && postfix == null)
                {
                    Refl.Warn("tex: skipped (patch method missing): " + label);
                    skipped++;
                    return false;
                }
                Bootstrap.H.Patch(original,
                    prefix == null ? null : new HarmonyLib.HarmonyMethod(prefix),
                    postfix == null ? null : new HarmonyLib.HarmonyMethod(postfix));
                Refl.Info("tex: patched " + label + " -> " + original.DeclaringType.Assembly.GetName().Name
                    + "!" + original.DeclaringType.FullName + "." + original.Name);
                ok++;
                return true;
            }
            catch (Exception e)
            {
                Refl.Warn("tex: patch SKIPPED " + label + ": " + e.Message);
                failed++;
                return false;
            }
        }

        // ---------------------------------------------------------------- summary

        private static void OnTick(object state)
        {
            try { Summary(); }
            catch (Exception e) { try { Refl.Warn("tex probe summary: " + e.Message); } catch { } }
        }

        /// <summary>
        /// tex: decode ok=N shift>0=N fail=N fallback=N evict=N offload=N budget=&lt;MiB&gt; limit=&lt;px&gt; hostevict=N
        /// Written every 30 s whether or not anything happened (the line is also a heartbeat).
        /// </summary>
        internal static void Summary()
        {
            int shifted = Volatile.Read(ref _shiftFromPlan) + Volatile.Read(ref _shiftFromConvert);
            Refl.Info("tex: decode ok=" + Volatile.Read(ref _convOk)
                + " shift>0=" + shifted
                + " fail=" + Volatile.Read(ref _convFail)
                + " fallback=" + Volatile.Read(ref _fallback)
                + " evict=" + Volatile.Read(ref _evict)
                + " offload=" + Volatile.Read(ref _offload)
                + " budget=" + MiB(_budget)
                + " limit=" + _limit
                + " hostevict=" + Volatile.Read(ref _hostEvict)
                + " plan=" + Volatile.Read(ref _planOk) + "/" + Volatile.Read(ref _planFail));
        }

        // ---------------------------------------------------------------- helpers

        private static string MiB(int bytes)
        {
            try
            {
                if (bytes <= 0) return "0";
                return (bytes / 1048576.0).ToString("0.0");
            }
            catch { return "?"; }
        }

        private static int Atoi(object v)
        {
            try { return v == null ? 0 : Convert.ToInt32(v); } catch { return 0; }
        }

        /// <summary>DDS header fields the decoder itself reads: format code 128, width 16, height 12, mips 28.</summary>
        private static void Header(byte[] b, int len, out int fmt, out int w, out int h, out int mips)
        {
            fmt = -1; w = -1; h = -1; mips = -1;
            try
            {
                if (b == null) return;
                int n = len;
                if (n <= 0 || n > b.Length) n = b.Length;
                if (n < 148) return;
                if (BitConverter.ToInt32(b, 0) != 542327876) return;      // 'DDS '
                h = BitConverter.ToInt32(b, 12);
                w = BitConverter.ToInt32(b, 16);
                mips = BitConverter.ToInt32(b, 28);
                fmt = BitConverter.ToInt32(b, 128);
            }
            catch { }
        }

        /// <summary>How many mip steps the output was reduced by (0 when the size did not change).</summary>
        private static int ShiftOf(int inW, int outW)
        {
            if (inW <= 0 || outW <= 0 || outW >= inW) return 0;
            int s = 0, w = inW;
            while (w > outW && s < 16) { w >>= 1; s++; }
            return s;
        }

        // ---------------------------------------------------------------- plan members

        private static bool _planResolved;
        private static Type _planType;
        private static PropertyInfo _pFormat, _pWidth, _pHeight, _pShift, _pSourceMip;
        private static FieldInfo _fFormat, _fWidth, _fHeight, _fShift, _fSourceMip;

        /// <summary>Plan is a record (properties); fall back to fields for a plain class.</summary>
        private static void ResolvePlan(Type t)
        {
            if (_planResolved && _planType == t) return;
            _planType = t;
            _planResolved = true;

            _pFormat = Refl.Prop(t, "Format");
            _pWidth = Refl.Prop(t, "Width");
            _pHeight = Refl.Prop(t, "Height");
            _pShift = Refl.Prop(t, "Shift");
            _pSourceMip = Refl.Prop(t, "SourceMip");

            _fFormat = Refl.F(t, "Format");
            _fWidth = Refl.F(t, "Width");
            _fHeight = Refl.F(t, "Height");
            _fShift = Refl.F(t, "Shift");
            _fSourceMip = Refl.F(t, "SourceMip");

            int found = 0;
            if (_pFormat != null || _fFormat != null) found++;
            if (_pWidth != null || _fWidth != null) found++;
            if (_pHeight != null || _fHeight != null) found++;
            if (_pShift != null || _fShift != null) found++;
            if (_pSourceMip != null || _fSourceMip != null) found++;
            Refl.Info("tex: plan type " + t.FullName + " members " + found + "/5"
                + " (Shift " + (_pShift != null || _fShift != null ? "ok" : "MISSING") + ")");
        }

        private static int Int(object inst, PropertyInfo p, FieldInfo f)
        {
            try
            {
                if (p != null) return Convert.ToInt32(p.GetValue(inst, null));
                if (f != null) return Convert.ToInt32(f.GetValue(inst));
            }
            catch { }
            return -1;
        }
    }
}
