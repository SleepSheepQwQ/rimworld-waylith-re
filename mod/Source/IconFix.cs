using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;

namespace AgentTouchFix
{
    /// <summary>
    /// v16: build-menu icons are blank because Verse.BuildableDef.ResolveIcon() ends with
    ///
    ///     uiIcon = (Texture2D)val.mainTexture;          // BuildableDef.cs:333
    ///
    /// where 'val' is graphic.ExtractInnerGraphicFor(null).MatAt(rot). On this port Material.mainTexture
    /// comes back null for the game's own materials, so uiIcon stays null for every buildable that does
    /// not define an explicit uiIconPath. The map still draws those buildings (it uses the material and
    /// mesh, not mainTexture), which is exactly the reported split: buildings look fine in the world,
    /// but their build-menu tiles are empty frames.
    ///
    /// This postfix fills uiIcon from the graphic's texture path when the game failed to:
    ///   graphic.path  ->  ContentFinder&lt;Texture2D&gt;.Get(path, false)
    /// Nothing is changed when uiIcon is already set, and any reflection failure is a no-op.
    /// </summary>
    internal static class IconFix
    {
        private static int _fixed;
        private static int _missed;
        private static int _warned;

        private static FieldInfo _fUiIcon, _fGraphic, _fDefName;
        private static MethodInfo _finder;          // ContentFinder<Texture2D>.Get(string, bool)
        private static FieldInfo _fPath;            // Graphic.path

        internal static void Postfix(object __instance)
        {
            if (__instance == null) return;
            try
            {
                if (_fUiIcon == null) Resolve(__instance.GetType());
                if (_fUiIcon == null) return;

                object cur = _fUiIcon.GetValue(__instance);
                if (cur != null) return;                       // the game resolved it

                string path = null;
                object graphic = _fGraphic == null ? null : _fGraphic.GetValue(__instance);
                if (graphic != null)
                {
                    if (_fPath == null) _fPath = graphic.GetType().GetField("path", BindingFlags.Public | BindingFlags.Instance);
                    if (_fPath != null) path = _fPath.GetValue(graphic) as string;
                }
                if (string.IsNullOrEmpty(path)) { Interlocked.Increment(ref _missed); return; }

                object tex = _finder == null ? null : _finder.Invoke(null, new object[] { path, false });
                if (tex == null)
                {
                    int m = Interlocked.Increment(ref _missed);
                    if (m <= 5) Refl.Info("iconfix: no texture for path '" + path + "'");
                    return;
                }

                _fUiIcon.SetValue(__instance, tex);
                int n = Interlocked.Increment(ref _fixed);
                if (n <= 10)
                {
                    string defName = _fDefName == null ? "?" : (_fDefName.GetValue(__instance) as string);
                    Refl.Info("iconfix: uiIcon <- " + path + " (" + defName + ")");
                }
                else if (n % 500 == 0)
                {
                    Refl.Info("iconfix: filled " + n + " buildable icons, still-null=" + Volatile.Read(ref _missed));
                }
            }
            catch (Exception e) { WarnOnce("ResolveIcon postfix: " + e.Message); }
        }

        private static void Resolve(Type t)
        {
            _fUiIcon = t.GetField("uiIcon", BindingFlags.Public | BindingFlags.Instance);
            _fGraphic = t.GetField("graphic", BindingFlags.Public | BindingFlags.Instance);
            _fDefName = t.GetField("defName", BindingFlags.Public | BindingFlags.Instance);

            try
            {
                Type open = Type.GetType("Verse.ContentFinder`1, Assembly-CSharp");
                Type texType = Refl.T("Waylith.Rendering.Texture2D");
                if (open != null && texType != null)
                {
                    Type closed = open.MakeGenericType(texType);
                    _finder = closed.GetMethod("Get", BindingFlags.Public | BindingFlags.Static, null,
                        new[] { typeof(string), typeof(bool) }, null);
                }
            }
            catch (Exception e) { WarnOnce("ContentFinder resolve: " + e.Message); }
        }

        private static void WarnOnce(string m)
        {
            if (Interlocked.Exchange(ref _warned, 1) == 0) Refl.Warn("iconfix: " + m);
        }

        internal static void Install()
        {
            Type t = Refl.T("Verse.BuildableDef");
            if (t == null) { Refl.Warn("iconfix: Verse.BuildableDef not found"); return; }

            MethodInfo target = t.GetMethod("ResolveIcon",
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (target == null) { Refl.Warn("iconfix: ResolveIcon not found"); return; }

            try
            {
                MethodInfo post = typeof(IconFix).GetMethod("Postfix", BindingFlags.Static | BindingFlags.NonPublic);
                Harmony h = new Harmony("sleepsheep.waylith.agenttouchfix.iconfix");
                h.Patch(target, null, new HarmonyMethod(post));
                Refl.Info("iconfix: postfix on Verse.BuildableDef.ResolveIcon (uiIcon from graphic.path when mainTexture is null)");
            }
            catch (Exception e) { Refl.Warn("iconfix: patch failed: " + e.Message); }
        }
    }
}
