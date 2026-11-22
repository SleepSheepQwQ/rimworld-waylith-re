using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using RimWorld;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityStubs.Gui;
using Verse;
using WR = Waylith.Rendering;

/// <summary>
/// Measures UnityEngine.RenderTexture.active at the exact call the animal list uses to paint an
/// icon, with GuiBackend.Current replaced by a recording backend (the headless host never installs
/// the real ImGui one, so without this nothing would observe the draw call at all).
///
/// For every def:
///   [ACTIVE] active.before=...                       the value Graphics.DrawTexture will test
///   [ACTIVE] draw.def calls=...                      calls that reached the backend via Widgets.ThingIcon
///   [ACTIVE] direct.material calls=...                the same tail called directly
///   [ACTIVE] direct.material.forced calls=...         positive control: active set to a real RT
///
/// If active.before is null and the material call reaches the backend, the "active != null diverts
/// the draw" hypothesis is false for this call. The forced case proves the probe would see a
/// diversion if it existed.
internal static class StartupHook
{
    private const string T = "[ACTIVE]";
    private static readonly string[] Defs = { "Ibex", "Boomalope", "Squirrel", "Cow" };
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static int _done;
    private static int _phase2;
    private static Recording _rec;

    public static void Initialize()
    {
        new Thread(WaitForBoot) { IsBackground = true, Name = "activeprobe-waiter" }.Start();
        new Thread(Watchdog) { IsBackground = true, Name = "activeprobe-watchdog" }.Start();
    }

    private static void Watchdog()
    {
        int ms = int.TryParse(Environment.GetEnvironmentVariable("PROBE_TIMEOUT_MS"), out int v) ? v : 240000;
        Thread.Sleep(ms);
        Console.WriteLine($"{T} RESULT=TIMEOUT");
        Console.Out.Flush();
        Environment.Exit(9);
    }

    private static void WaitForBoot()
    {
        while (Waylith.Runtime.WaylithRuntime.Current == null) Thread.Sleep(20);
        Waylith.Runtime.WaylithRuntime.Current.App.BootCompleted += Run;
    }

    private static void Run()
    {
        if (Interlocked.Exchange(ref _done, 1) != 0) return;
        try
        {
            Log($"boot active={Describe(RenderTexture.active)} gui.backend={(GuiBackend.Current == null ? "<null>" : GuiBackend.Current.GetType().FullName)}");
            _rec = new Recording(GuiBackend.Current);
            GuiBackend.Current = _rec;
            Log($"installed recording backend inner={(_rec.Inner == null ? "<null>" : _rec.Inner.GetType().FullName)}");

            foreach (string name in Defs) ProbeDef(name);

            if (Environment.GetEnvironmentVariable("ANIMAL_MAP") == "1")
            {
                Log("map=requesting scene Play");
                SceneManager.LoadScene("Play");
                new Thread(WaitForMap) { IsBackground = true, Name = "activeprobe-map-waiter" }.Start();
                return;
            }

            Finish();
        }
        catch (Exception e)
        {
            Console.WriteLine($"{T} RESULT=FATAL {e.GetType().Name}: {e.Message}");
            Console.WriteLine(e.StackTrace);
            Console.Out.Flush();
            Environment.Exit(8);
        }
    }

    // ------------------------------------------------------------------ def overload (menu/build icon)
    private static void ProbeDef(string defName)
    {
        Log($"==== def {defName} ====");
        ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
        if (def == null) { Log($"{defName}: missing"); return; }

        Log($"def.uiIcon={(def.uiIcon == null ? "<null>" : $"'{def.uiIcon.name}' {def.uiIcon.width}x{def.uiIcon.height}")} " +
            $"bad={ReferenceEquals(def.uiIcon, BaseContent.BadTex)} " +
            $"def.uiIconMaterial={(def.uiIconMaterial == null ? "<null>" : $"'{def.uiIconMaterial.name}' shader='{def.uiIconMaterial.shader?.name}'")}");

        Material mat = null;
        Texture2D tex = null;
        try { tex = Widgets.GetIconFor(def, out mat, null, null, null); }
        catch (Exception e) { Log($"{defName}: GetIconFor(ThingDef) threw {e.GetType().Name}: {e.Message}"); }
        Log($"{defName}: tex={(tex == null ? "<null>" : $"'{tex.name}' {tex.width}x{tex.height}")} " +
            $"material={(mat == null ? "<null>" : $"'{mat.name}' shader='{mat.shader?.name}'")}");

        var rect = new Rect(0f, 0f, 64f, 64f);
        var uv = new Rect(0f, 0f, 1f, 1f);
        ResetEvent();

        // 1) exactly what the animal column calls: Widgets.ThingIcon(Rect, ThingDef)
        Log($"{defName}: active.before={Describe(RenderTexture.active)}");
        _rec.Reset();
        try { Widgets.ThingIcon(rect, def); }
        catch (Exception e) { Log($"{defName}: ThingIcon(ThingDef) threw {e.GetType().Name}: {e.Message}"); }
        Log($"{defName}: draw.def {_rec.Summary()} active.after={Describe(RenderTexture.active)}");

        // 2) the exact tail of that call, with the material GetIconFor produced
        if (tex != null && !ReferenceEquals(tex, BaseContent.BadTex))
        {
            ResetEvent();
            _rec.Reset();
            try { GenUI.DrawTextureWithMaterial(rect, tex, mat, uv); }
            catch (Exception e) { Log($"{defName}: direct.material threw {e.GetType().Name}: {e.Message}"); }
            Log($"{defName}: direct.material {_rec.Summary()} active={Describe(RenderTexture.active)}");

            // 3) positive control: with active non-null the same call must not reach the backend
            RenderTexture rt = null;
            try { rt = new RenderTexture(64, 64, 0) { name = "ActiveProbe_Forced" }; }
            catch (Exception e) { Log($"{defName}: forced new RenderTexture threw {e.GetType().Name}: {e.Message}"); }
            if (rt != null)
            {
                try { rt.Create(); }
                catch (Exception e) { Log($"{defName}: forced Create threw {e.GetType().Name}: {e.Message} (continuing)"); }
                try
                {
                    RenderTexture.active = rt;
                    Log($"{defName}: forced active={Describe(RenderTexture.active)}");
                    ResetEvent();
                    _rec.Reset();
                    try { GenUI.DrawTextureWithMaterial(rect, tex, mat, uv); }
                    catch (Exception e) { Log($"{defName}: forced call threw {e.GetType().Name}: {e.Message}"); }
                    Log($"{defName}: direct.material.forced {_rec.Summary()} active={Describe(RenderTexture.active)}");
                }
                catch (Exception e) { Log($"{defName}: forced setup threw {e.GetType().Name}: {e.Message}"); }
                finally
                {
                    try { RenderTexture.active = null; } catch { }
                    try { rt.Release(); } catch { }
                    Log($"{defName}: restored active={Describe(RenderTexture.active)}");
                }
            }
        }
    }

    // ------------------------------------------------------------------ live Thing overload on a real map
    private static void WaitForMap()
    {
        while (Clock.Elapsed.TotalSeconds < 320)
        {
            Map map = null;
            try { map = Current.Game?.CurrentMap; } catch { }
            if (map == null) { try { map = Find.AnyPlayerHomeMap; } catch { } }
            if (map != null)
            {
                Log($"map=published {map.Size.x}x{map.Size.z}");
                LongEventHandler.QueueLongEvent(() => Phase2(map), null, doAsynchronously: false, null);
                return;
            }
            Thread.Sleep(100);
        }
        Log("map=FAILED timeout");
        Finish();
    }

    private static void Phase2(Map map)
    {
        if (Interlocked.Exchange(ref _phase2, 1) != 0) return;
        try
        {
            // Same workaround as animalprobe: the deferred mesh pass dies in headless (GasMat null)
            // and Thing.SpawnSetup would dereference the half-built section array. The icon path
            // does not read the mesh.
            try
            {
                MapDrawer drawer = map.mapDrawer;
                if (drawer != null && drawer.sections != null)
                {
                    bool complete = true;
                    for (int i = 0; i < drawer.SectionCount.x && complete; i++)
                        for (int j = 0; j < drawer.SectionCount.z; j++)
                            if (drawer.sections[i, j] == null) { complete = false; break; }
                    if (!complete) { drawer.sections = null; Log("mapMesh=incomplete->null"); }
                }
            }
            catch (Exception e) { Log($"mapMesh {e.GetType().Name}: {e.Message}"); }

            foreach (string name in Defs)
            {
                Log($"==== live {name} ====");
                ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(name);
                PawnKindDef kind = def?.race?.AnyPawnKind;
                if (kind == null) { Log($"{name}: no pawnKind"); continue; }

                Pawn pawn = null;
                try { pawn = PawnGenerator.GeneratePawn(kind, (Faction)null); }
                catch (Exception e) { Log($"{name}: generate threw {e.GetType().Name}: {e.Message}"); }
                if (pawn == null) { Log($"{name}: no pawn"); continue; }

                IntVec3 cell;
                if (!CellFinder.TryFindRandomCellNear(map.Center, map, 30, c => c.Standable(map), out cell)) cell = map.Center;
                try { GenSpawn.Spawn(pawn, cell, map, WipeMode.Vanish); }
                catch (Exception e) { Log($"{name}: spawn threw {e.GetType().Name}: {e.Message}"); continue; }
                Log($"{name}: spawned={pawn.Spawned} cell={cell}");

                float scale = 0f, angle = 0f;
                Vector2 prop = Vector2.one;
                Color col = Color.white;
                Material mat = null;
                Texture icon = null;
                try
                {
                    icon = Widgets.GetIconFor(pawn, new Vector2(128f, 128f), Rot4.East, false,
                                              out scale, out angle, out prop, out col, out mat);
                }
                catch (Exception e) { Log($"{name}: GetIconFor(Thing) threw {e.GetType().Name}: {e.Message}"); }
                Log($"{name}: live tex={(icon == null ? "<null>" : $"'{icon.name}' {icon.width}x{icon.height}")} " +
                    $"material={(mat == null ? "<null>" : $"'{mat.name}' shader='{mat.shader?.name}'")}");

                ResetEvent();
                Log($"{name}: live active.before={Describe(RenderTexture.active)}");
                _rec.Reset();
                try { Widgets.ThingIcon(new Rect(0f, 0f, 64f, 64f), pawn); }
                catch (Exception e) { Log($"{name}: ThingIcon(Thing) threw {e.GetType().Name}: {e.Message}"); }
                Log($"{name}: live draw {_rec.Summary()} active.after={Describe(RenderTexture.active)}");
            }

            Finish();
        }
        catch (Exception e)
        {
            Log($"RESULT=FATAL phase2 {e.GetType().Name}: {e.Message}");
            Console.Out.Flush();
            Environment.Exit(8);
        }
    }

    private static void Finish()
    {
        Log("--- active transition log (every set of RenderTexture.active observed) ---");
        foreach (string line in _rec.Transitions) Log("transition " + line);
        Log("RESULT=DONE");
        Console.Out.Flush();
        Environment.Exit(0);
    }

    private static void ResetEvent()
    {
        try
        {
            var ev = new Event();
            ev.rawType = EventType.repaint;
            Event.current = ev;
        }
        catch (Exception e) { Log($"event reset threw {e.GetType().Name}"); }
    }

    private static string Describe(RenderTexture rt) =>
        rt == null ? "null" : $"'{rt.name}' {rt.width}x{rt.height}";

    private static void Log(string s)
    {
        Console.WriteLine($"{T} {s}");
        Console.Out.Flush();
    }

    // ------------------------------------------------------------------ recording backend
    private sealed class Recording : IGuiBackend
    {
        public readonly IGuiBackend Inner;
        private readonly Dictionary<string, int> _calls = new Dictionary<string, int>();
        private readonly List<string> _details = new List<string>();
        public readonly List<string> Transitions = new List<string>();

        public Recording(IGuiBackend inner) { Inner = inner; }

        public void Reset() { _calls.Clear(); _details.Clear(); }

        public string Summary()
        {
            if (_calls.Count == 0) return "calls=none";
            var parts = new List<string>();
            foreach (var kv in _calls) parts.Add($"{kv.Key}:{kv.Value}");
            parts.Sort(StringComparer.Ordinal);
            return "calls=" + string.Join(",", parts) + (_details.Count == 0 ? "" : " [" + string.Join("; ", _details) + "]");
        }

        private void Hit(string kind, Texture tex, Material mat)
        {
            _calls.TryGetValue(kind, out int n);
            _calls[kind] = n + 1;
            if (_details.Count < 8)
                _details.Add($"{kind} active={Describe(RenderTexture.active)} tex={(tex == null ? "<null>" : $"'{tex.name}'")} mat={(mat == null ? "<null>" : $"'{mat.name}'")}");
        }

        public bool MouseOutsideHitClip => Inner?.MouseOutsideHitClip ?? false;
        public int GroupCount => Inner?.GroupCount ?? 0;
        public bool InlineControlsObscured => Inner?.InlineControlsObscured ?? false;

        public Vector2 MeasureText(int fontIndex, float fontSize, FontStyle fontStyle, string text, float wrapWidth, bool richText = true)
            => Inner?.MeasureText(fontIndex, fontSize, fontStyle, text, wrapWidth, richText) ?? Vector2.zero;

        public float LineSpacing(int fontIndex, float fontSize) => Inner?.LineSpacing(fontIndex, fontSize) ?? 0f;

        public void FontMetrics(int fontIndex, out float importSize, out float ascent, out float lineSpacing)
        {
            if (Inner != null) { Inner.FontMetrics(fontIndex, out importSize, out ascent, out lineSpacing); return; }
            importSize = ascent = lineSpacing = 0f;
        }

        public int RegisterFont(string name, byte[] fontData, float importSize, int renderingMode)
            => Inner?.RegisterFont(name, fontData, importSize, renderingMode) ?? -1;

        public int TextIndexAt(int fontIndex, float fontSize, FontStyle fontStyle, string text, float wrapWidth, Vector2 local, bool richText = true)
            => Inner?.TextIndexAt(fontIndex, fontSize, fontStyle, text, wrapWidth, local, richText) ?? 0;

        public Vector2 TextCursorPos(int fontIndex, float fontSize, FontStyle fontStyle, string text, float wrapWidth, int index, out float lineHeight, bool richText = true)
        {
            if (Inner != null) return Inner.TextCursorPos(fontIndex, fontSize, fontStyle, text, wrapWidth, index, out lineHeight, richText);
            lineHeight = 0f;
            return Vector2.zero;
        }

        public void TextSelectionRects(int fontIndex, float fontSize, FontStyle fontStyle, string text, float wrapWidth, int from, int to, List<Rect> rects, bool richText = true)
        { Inner?.TextSelectionRects(fontIndex, fontSize, fontStyle, text, wrapWidth, from, to, rects, richText); }

        public void DrawText(Rect rect, string text, int fontIndex, float fontSize, FontStyle fontStyle, TextAnchor anchor, bool wordWrap, Color color, Rect clipRect = default(Rect), bool richText = true)
        { Inner?.DrawText(rect, text, fontIndex, fontSize, fontStyle, anchor, wordWrap, color, clipRect, richText); }

        public void DrawTexture(Rect rect, Texture image, ScaleMode scaleMode, Color color)
        {
            Hit("texture", image, null);
            Inner?.DrawTexture(rect, image, scaleMode, color);
        }

        public void DrawTextureWithTexCoords(Rect rect, Texture image, Rect texCoords, Color color)
        {
            Hit("texcoords", image, null);
            Inner?.DrawTextureWithTexCoords(rect, image, texCoords, color);
        }

        public void DrawTextureWithMaterial(Rect rect, Texture image, Rect texCoords, Color color, Material material, WR.Material passMaterial)
        {
            Hit("material", image, material);
            Inner?.DrawTextureWithMaterial(rect, image, texCoords, color, material, passMaterial);
        }

        public void FillRect(Rect rect, Color color) { Inner?.FillRect(rect, color); }
        public void FillRectRounded(Rect rect, Color color, float rounding) { Inner?.FillRectRounded(rect, color, rounding); }

        public void DrawTextureQuad(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, Texture image, Color color)
        { Inner?.DrawTextureQuad(p0, p1, p2, p3, image, color); }

        public void DrawPrimitives(int mode, ReadOnlySpan<GuiVertex> vertices, Texture texture, bool screenSpace)
        { Inner?.DrawPrimitives(mode, vertices, texture, screenSpace); }

        public void SetMatrix(Matrix4x4 matrix) { Inner?.SetMatrix(matrix); }
        public void PushGroup(Rect rect) { Inner?.PushGroup(rect); }
        public void PushScrollView(Rect clipRect, Vector2 contentOffset) { Inner?.PushScrollView(clipRect, contentOffset); }
        public void PopGroup() { Inner?.PopGroup(); }

        public void GetGroup(int index, out Rect rect, out Vector2 contentOrigin)
        {
            if (Inner != null) { Inner.GetGroup(index, out rect, out contentOrigin); return; }
            rect = default(Rect);
            contentOrigin = Vector2.zero;
        }

        public void BeginWindowContent(Rect windowRect) { Inner?.BeginWindowContent(windowRect); }
        public void EndWindowContent() { Inner?.EndWindowContent(); }
        public void PushClipRect(Rect rect) { Inner?.PushClipRect(rect); }
        public void PopClipRect() { Inner?.PopClipRect(); }

        public void ActiveRenderTextureChanged(RenderTexture active)
        {
            var frames = new StackTrace(1, false).GetFrames() ?? Array.Empty<StackFrame>();
            string caller = "?";
            foreach (var f in frames)
            {
                string tn = f.GetMethod()?.DeclaringType?.FullName ?? "";
                if (tn == typeof(Recording).FullName) continue;
                if (tn == "UnityEngine.RenderTexture") continue;
                caller = f.GetMethod()?.DeclaringType?.FullName + "::" + f.GetMethod()?.Name;
                break;
            }
            Transitions.Add($"{Describe(active)} <- {caller}");
            Inner?.ActiveRenderTextureChanged(active);
        }

        public void SetTextLanguage(string languageFolderName) { Inner?.SetTextLanguage(languageFolderName); }
        public Vector2 ScreenToGuiPoint(Vector2 screenPoint) => Inner?.ScreenToGuiPoint(screenPoint) ?? screenPoint;
        public Vector2 GuiToScreenPoint(Vector2 guiPoint) => Inner?.GuiToScreenPoint(guiPoint) ?? guiPoint;
        public Vector2 GuiToDevicePoint(Vector2 guiPoint) => Inner?.GuiToDevicePoint(guiPoint) ?? guiPoint;
    }
}
