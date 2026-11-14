using System;
using System.Reflection;
using System.Threading;
using HarmonyLib;

namespace AgentTouchFix
{
    /// <summary>
    /// v40: repair the two fields the build-menu icon draw depends on, at the moment the icon is drawn.
    ///
    /// Verse.Widgets.ThingIcon(Rect, ThingDef, ...) draws whatever Verse.Widgets.GetIconFor() returns:
    ///
    ///     result   = thingDef.GetUIIconForStuff(stuffDef);      // == thingDef.uiIcon
    ///     material = thingDef.uiIconMaterial;                   // when set
    ///     if (result == null || result == BaseContent.BadTex) return;   // nothing drawn at all
    ///
    /// Both fields are filled once, in BuildableDef.ResolveIcon():
    ///
    ///     uiIconMaterial = MaterialPool.MatFrom(request);
    ///     uiIcon = (Texture2D)val.mainTexture;                  // BuildableDef.cs:333
    ///
    /// On this port Material.mainTexture comes back null, so uiIcon stays null (or BadTex) and the tile
    /// is an empty frame forever; and when the UI material IS created, CutoutComplexUI can discard the
    /// whole tile silently (verified on CI: a non-default _MainTex_ST renders 0 of 16384 pixels).
    ///
    /// This prefix fixes both at draw time - no reliance on ResolveIcon running, on patch ordering, or
    /// on the material path at all:
    ///   1. uiIcon null / BadTex  -> load the graphic's texture from its path and store it on the def
    ///   2. uiIconMaterial is a CutoutComplexUI material -> drop it, so GetIconFor takes the plain path
    /// Both writes are idempotent and cost nothing once the field is healthy.
    /// </summary>
    internal static class IconFieldFix
    {
        private static int _filled, _cleared, _warned;
        private static FieldInfo _fIcon, _fMat, _fGraphic, _fGraphicData, _fTexPath, _fPath, _fDefName;
        private static Type _defType, _badTexType;
        private static object _badTex;
        private static MethodInfo _finder;
        private static string _mode = IconPath.Plain;

        internal static void Init(string mode)
        {
            _mode = string.IsNullOrEmpty(mode) ? IconPath.Plain : mode.Trim().ToLowerInvariant();
        }

        internal static void Prefix(object[] __args)
        {
            try
            {
                if (__args == null || __args.Length < 2) return;
                object def = __args[1];
                if (def == null) return;
                if (_defType != def.GetType()) Resolve(def.GetType());

                object icon = _fIcon.GetValue(def);
                if (icon == null || IsBad(icon))
                    Fill(def);

                if (_fMat != null)
                {
                    object mat = _fMat.GetValue(def);
                    if (mat != null && IconPath.UsePlain(NameOf(mat), _mode))
                    {
                        _fMat.SetValue(def, null);
                        int c = Interlocked.Increment(ref _cleared);
                        if (c <= 5) Refl.Info("drawfix: dropped icon material '" + NameOf(mat) + "' on " + DefName(def));
                    }
                }
            }
            catch (Exception e) { WarnOnce(e.Message); }
        }

        private static void Fill(object def)
        {
            object graphic = _fGraphic == null ? null : _fGraphic.GetValue(def);
            string path = null;
            if (graphic != null && _fPath != null) path = _fPath.GetValue(graphic) as string;
            if (string.IsNullOrEmpty(path) && _fGraphicData != null && _fTexPath != null)
            {
                object gd = _fGraphicData.GetValue(def);
                if (gd != null) path = _fTexPath.GetValue(gd) as string;
            }
            if (string.IsNullOrEmpty(path)) return;

            object tex = Load(path);
            if (tex == null) tex = Load(path + "_south");
            if (tex == null) return;

            _fIcon.SetValue(def, tex);
            int n = Interlocked.Increment(ref _filled);
            if (n <= 5) Refl.Info("drawfix: uiIcon <- " + path + " on " + DefName(def));
            else if (n % 100 == 0) Refl.Info("drawfix: filled " + n + " icons at draw time");
        }

        private static object Load(string path)
        {
            try { return _finder == null ? null : _finder.Invoke(null, new object[] { path, false }); }
            catch { return null; }
        }

        private static bool IsBad(object tex)
        {
            if (_badTex == null) return false;
            return ReferenceEquals(tex, _badTex);
        }

        private static string DefName(object def)
        {
            return _fDefName == null ? "?" : (_fDefName.GetValue(def) as string) ?? "?";
        }

        private static string NameOf(object mat)
        {
            try
            {
                foreach (PropertyInfo p in mat.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    if (p.Name == "name" && p.GetIndexParameters().Length == 0) return p.GetValue(mat, null) as string;
            }
            catch { }
            return null;
        }

        private static void Resolve(Type defType)
        {
            _defType = defType;
            _fIcon = Field(defType, "uiIcon");
            _fMat = Field(defType, "uiIconMaterial");
            _fGraphic = Field(defType, "graphic");
            _fGraphicData = Field(defType, "graphicData");
            _fDefName = Field(defType, "defName");

            try
            {
                Type gdType = _fGraphicData == null ? null : _fGraphicData.FieldType;
                if (gdType != null) _fTexPath = Field(gdType, "texPath");
                Type gType = _fGraphic == null ? null : _fGraphic.FieldType;
                if (gType != null) _fPath = Field(gType, "path");
            }
            catch { }

            try
            {
                _badTexType = Refl.T("Verse.BaseContent");
                if (_badTexType != null)
                {
                    FieldInfo f = _badTexType.GetField("BadTex", BindingFlags.Public | BindingFlags.Static)
                                  ?? _badTexType.GetField("BadTex", BindingFlags.NonPublic | BindingFlags.Static);
                    if (f != null) _badTex = f.GetValue(null);
                }
            }
            catch { }

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
            catch (Exception e) { WarnOnce("ContentFinder: " + e.Message); }
        }

        private static FieldInfo Field(Type t, string name)
        {
            for (Type c = t; c != null; c = c.BaseType)
            {
                FieldInfo f = c.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (f != null) return f;
            }
            return null;
        }

        private static void WarnOnce(string m)
        {
            if (Interlocked.Exchange(ref _warned, 1) == 0) Refl.Warn("drawfix: " + m);
        }

        internal static void Install()
        {
            Type widgets = Refl.T("Verse.Widgets");
            if (widgets == null) { Refl.Warn("drawfix: Verse.Widgets not found"); return; }

            MethodInfo target = null;
            foreach (MethodInfo m in widgets.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name != "ThingIcon") continue;
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length == 8 && ps[1].ParameterType.Name == "ThingDef") { target = m; break; }
            }
            if (target == null) { Refl.Warn("drawfix: ThingIcon(Rect, ThingDef, ...) not found"); return; }

            try
            {
                MethodInfo pre = typeof(IconFieldFix).GetMethod("Prefix",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                new Harmony("sleepsheep.waylith.agenttouchfix.drawfix").Patch(target, new HarmonyMethod(pre));
                Refl.Info("drawfix: prefix on Verse.Widgets.ThingIcon (uiIcon repaired, icon material dropped)");
            }
            catch (Exception e) { Refl.Warn("drawfix: patch failed: " + e.Message); }
        }
    }
}
