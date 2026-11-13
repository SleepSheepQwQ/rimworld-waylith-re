using System;
using System.IO;
using System.Reflection;
using HarmonyLib;

namespace AgentTouchFix
{
    /// <summary>
    /// v14: bypass RimWorld 1.7's static texture atlas.
    ///
    /// Verse.StaticTextureAtlas.BlitTexturesToColorAtlas (StaticTextureAtlas.cs:527) bakes every UI
    /// icon into a page by rendering into a RenderTexture with
    ///   RenderTexture + Graphics.SetRenderTarget + GL.Clear + GL.PushMatrix + GL.LoadPixelMatrix
    ///   + Graphics.DrawTexture(..., Material(Shader.Find("Custom/BlitExact")))
    /// and only then copies the page into colorTexture. On this port the stage reports
    /// "UI stage OK: texture-atlas", yet the pages come out blank, so every icon that was baked
    /// into an atlas draws as an empty frame while the surrounding UI (drawn directly) is fine.
    ///
    /// Verse.GlobalTextureAtlasManager.TryInsertStatic is the only door into an atlas. Answering
    /// "false" leaves the texture out of the build queue, so LeafSourceEvicted() stays false, the
    /// original Texture2D keeps its GPU data and the game draws it directly.
    ///
    /// Switch: files/game/agenttouchfix.atlas
    ///   off (DEFAULT, also when the file is absent) - TryInsertStatic returns false, no atlas is built
    ///   on                                        - shipped behaviour, textures are baked into atlases
    /// </summary>
    internal static class AtlasOff
    {
        internal const string Path = "/data/data/com.ports1de.waylith/files/game/agenttouchfix.atlas";

        private static bool _disable = true;      // off == do not build atlases
        private static int _skipped;
        private static int _lastLogMs = -100000;

        internal static bool Disabled { get { return _disable; } }

        internal static void ReadSwitch()
        {
            try
            {
                if (File.Exists(Path))
                {
                    string s = (File.ReadAllText(Path) ?? "").Trim().ToLowerInvariant();
                    if (s.StartsWith("on") || s == "1" || s.StartsWith("ship")) _disable = false;
                    else if (s.StartsWith("off") || s == "0" || s.StartsWith("noatlas")) _disable = true;
                }
            }
            catch (Exception e) { Refl.Warn("atlas switch unreadable: " + e.Message); }
            Refl.Info("atlas: mode=" + (_disable ? "off (atlases disabled)" : "on (shipped behaviour)"));
        }

        /// <summary>Verse.GlobalTextureAtlasManager.TryInsertStatic(TextureAtlasGroup, Texture2D, Texture2D).</summary>
        internal static bool TryInsertStatic_Prefix(ref bool __result)
        {
            if (!_disable) return true;
            __result = false;
            int n = System.Threading.Interlocked.Increment(ref _skipped);
            long now;
            try { now = Environment.TickCount64; } catch { now = 0L; }
            if (now - _lastLogMs >= 2000L)
            {
                _lastLogMs = now;
                Refl.Info("atlas: kept " + n + " texture(s) out of the static atlas (drawn directly)");
            }
            return false;
        }

        internal static void Install()
        {
            ReadSwitch();
            if (!_disable) return;

            Type t = Refl.T("Verse.GlobalTextureAtlasManager");
            if (t == null) { Refl.Warn("atlas: Verse.GlobalTextureAtlasManager not found"); return; }

            MethodInfo target = Refl.M(t, "TryInsertStatic");
            if (target == null) { Refl.Warn("atlas: TryInsertStatic not found"); return; }

            try
            {
                MethodInfo prefix = typeof(AtlasOff).GetMethod("TryInsertStatic_Prefix",
                    BindingFlags.Static | BindingFlags.NonPublic);
                Harmony h = new Harmony("sleepsheep.waylith.agenttouchfix.atlasoff");
                h.Patch(target, new HarmonyMethod(prefix), null);
                Refl.Info("atlas: patched Verse.GlobalTextureAtlasManager.TryInsertStatic -> false"
                    + " (no atlas pages are baked; textures draw directly)");
            }
            catch (Exception e)
            {
                Refl.Warn("atlas: patch failed: " + e.Message);
            }
        }
    }
}
