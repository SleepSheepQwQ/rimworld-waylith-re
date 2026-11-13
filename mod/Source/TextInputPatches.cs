using System;
using System.Reflection;
using System.Threading;
using Verse;

namespace AgentTouchFix
{
    /// <summary>
    /// v7 C: text typed into the Android IME is silently dropped, which is what makes Chinese input
    /// look broken (an IME commit is long enough that the geometry usually changes in between).
    ///
    /// Ports1de.TextEditMailbox (PortUI.dll, internal sealed):
    ///
    ///   Begin(field)                    - called from NativeTextBridge.Postfix when a GUI.TextField
    ///                                     is focused; stores the whole TextEditTarget
    ///   Complete(request, text, now)    - the IME callback: pending = text; hasPending = true;
    ///                                     expires = now + 10000
    ///   TryTake(field, now, out text)   - the per-frame pump; `field != target` (a record struct
    ///                                     equality over Window, Control, X, Y, Width, Height)
    ///
    /// TextEditTarget is built every frame from GUIUtility.GUIToScreenPoint(rect.position) +
    /// rect.width/height + GUIUtility.keyboardControl + the currently drawn window id. So any
    /// layout change between the moment the editor opened and the moment the IME answers - a
    /// scrolled window, a re-laid-out dialog, the keyboard resizing the screen - changes X/Y or the
    /// control id, the equality fails, `return false`, and the text is dropped (it is only cleared
    /// on expiry). Exactly one IME answer can be pending at a time, so nothing is lost by matching
    /// more loosely.
    ///
    /// Fix: when the exact match fails, accept the pending text if it belongs to the *same window*
    /// (and, when there is no window id at all, only if the control id matches too). The pending
    /// entry is consumed, so a given answer can be taken exactly once, and the exact path is always
    /// tried first (the original method already returned true -> this postfix is a no-op).
    /// </summary>
    internal static class TextInputPatches
    {
        private const string TypeName = "Ports1de.TextEditMailbox";

        private static bool _resolved;
        private static FieldInfo _fPending;      // private string pending
        private static FieldInfo _fHasPending;   // private bool hasPending
        private static FieldInfo _fExpires;      // private long expires
        private static FieldInfo _fTarget;       // private TextEditTarget target
        private static FieldInfo _fGate;         // private readonly object gate

        private static PropertyInfo _pWindow, _pControl, _pX, _pY, _pWidth, _pHeight;

        private static bool _warned;
        private static long _taken;

        private static Type _type;

        /// <summary>PortUI.dll carries the mailbox NativeTextBridge actually uses.</summary>
        internal static Type MailboxType()
        {
            if (_type == null) _type = Refl.TPrefer("PortUI", TypeName);
            return _type;
        }

        private static void Resolve(Type mailbox)
        {
            if (_resolved) return;
            _resolved = true;
            try
            {
                _fPending = Refl.F(mailbox, "pending");
                _fHasPending = Refl.F(mailbox, "hasPending");
                _fExpires = Refl.F(mailbox, "expires");
                _fTarget = Refl.F(mailbox, "target");
                _fGate = Refl.F(mailbox, "gate");
            }
            catch { }

            if (_fPending == null || _fHasPending == null || _fExpires == null || _fTarget == null)
            {
                Refl.Warn("TextEditMailbox.pending/hasPending/expires/target not found -"
                    + " dropped IME text NOT recovered (raw PortUI behaviour)");
                return;
            }

            try
            {
                Type targetType = _fTarget.FieldType;
                _pWindow = Member(targetType, "Window");
                _pControl = Member(targetType, "Control");
                _pX = Member(targetType, "X");
                _pY = Member(targetType, "Y");
                _pWidth = Member(targetType, "Width");
                _pHeight = Member(targetType, "Height");
            }
            catch { }

            if (_pWindow == null)
            {
                Refl.Warn("TextEditTarget.Window not found - dropped IME text NOT recovered");
                _fPending = null;   // inert from here on
            }
        }

        /// <summary>TextEditTarget is a positional record struct: properties first, fields as fallback.</summary>
        private static PropertyInfo Member(Type t, string name)
        {
            try
            {
                PropertyInfo p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (p != null) return p;
            }
            catch { }
            return null;
        }

        private static int IntOf(object t, PropertyInfo p)
        {
            if (t == null || p == null) return int.MinValue;
            try { return Convert.ToInt32(p.GetValue(t, null)); }
            catch { return int.MinValue; }
        }

        private static float FloatOf(object t, PropertyInfo p)
        {
            if (t == null || p == null) return float.NaN;
            try { return Convert.ToSingle(p.GetValue(t, null)); }
            catch { return float.NaN; }
        }

        private static string Rect(object t)
        {
            return "(" + FloatOf(t, _pX).ToString("0.#") + "," + FloatOf(t, _pY).ToString("0.#")
                + "," + FloatOf(t, _pWidth).ToString("0.#") + "," + FloatOf(t, _pHeight).ToString("0.#") + ")";
        }

        /// <summary>
        /// Runs after PortUI's own TryTake. A false result means the exact TextEditTarget match
        /// failed; only then do we look at the mailbox contents, and only through the same lock the
        /// mailbox itself uses.
        /// </summary>
        internal static void TryTake_Postfix(object __instance, object[] __args, ref string text, ref bool __result)
        {
            if (__result || __instance == null || __args == null || __args.Length < 3) return;

            try
            {
                Resolve(__instance.GetType());
                if (_fPending == null) return;

                object gate = null;
                if (_fGate != null)
                {
                    try { gate = _fGate.GetValue(__instance); } catch { }
                }
                if (gate != null) Monitor.Enter(gate);
                try
                {
                    long now = __args[1] is long ? (long)__args[1] : Environment.TickCount64;

                    if (!(bool)_fHasPending.GetValue(__instance)) return;
                    long expires = Convert.ToInt64(_fExpires.GetValue(__instance));
                    if (now > expires) return;      // the original already expired it

                    string s = _fPending.GetValue(__instance) as string;
                    if (s == null) return;

                    object want = _fTarget.GetValue(__instance);     // the target the editor opened on
                    object got = __args[0];                          // the target being pumped now
                    if (want == null || got == null) return;

                    int w1 = IntOf(want, _pWindow), w2 = IntOf(got, _pWindow);
                    if (w1 != w2 || w1 == int.MinValue) return;

                    int c1 = IntOf(want, _pControl), c2 = IntOf(got, _pControl);
                    // No window at all (id -1): the "same window" test proves nothing, so require the
                    // control id as well. With a real window id, the control id may legitimately have
                    // changed (refocus / re-layout) and is only reported.
                    if (w1 < 0 && c1 != c2) return;

                    text = s;
                    __args[2] = s;                  // keep both views in sync whatever Harmony copies back
                    __result = true;
                    _fHasPending.SetValue(__instance, false);
                    _fPending.SetValue(__instance, null);

                    long n = Interlocked.Increment(ref _taken);
                    Refl.Info("ime text recovered: window " + w1 + " control " + c1 + " -> " + c2
                        + (c1 == c2 ? " (same)" : " (changed)") + " rect " + Rect(want) + " -> " + Rect(got)
                        + " chars=" + s.Length + " relaxed=" + n);
                }
                finally
                {
                    if (gate != null) Monitor.Exit(gate);
                }
            }
            catch (Exception e)
            {
                if (!_warned)
                {
                    _warned = true;
                    Refl.Err("ime text recovery failed (raw PortUI behaviour kept): " + e.Message);
                }
            }
        }
    }
}
