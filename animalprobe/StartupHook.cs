using System;
using System.Collections.Generic;
using System.Threading;
using RimWorld;
using UnityEngine;
using Verse;
using WR = Waylith.Rendering;

/// <summary>
/// Reports, inside the real game process after boot:
///
///   [ANIMAL] def=Ibex ... def.uiIcon ... def.uiIconMaterial shader=CutoutComplexUI ...
///   [ANIMAL] body.east ... material=CutoutComplexUI_... registry=true ...
///   [ANIMAL] geticonfor.thing material=CutoutComplexUI_...   (built the way Widgets.GetIconFor(Thing) builds it)
///   [ANIMAL] control.cutout uiMap=false material=null(plain texture)
///
/// Every value comes from the objects the shipped assembly built; nothing is inferred from names.
/// The point is to separate three hypotheses for a blank animal icon:
///   1. the UI material is one the port cannot render (CutoutComplexUI),
///   2. the def's uiIcon texture itself is empty,
///   3. the draw overload that the animal column uses is not covered by the 2.4.0 material drop.
/// </summary>
internal static class StartupHook
{
    private const string T = "[ANIMAL]";
    private static int _done;

    public static void Initialize()
    {
        new Thread(WaitForBoot) { IsBackground = true, Name = "animalprobe-waiter" }.Start();
        new Thread(Watchdog) { IsBackground = true, Name = "animalprobe-watchdog" }.Start();
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
            string list = Environment.GetEnvironmentVariable("ANIMAL_DEFS") ?? "Ibex,Boomalope,Squirrel,Cow";
            foreach (string raw in list.Split(','))
            {
                string name = raw.Trim();
                if (name.Length != 0) Probe(name);
            }
            Console.WriteLine($"{T} RESULT=DONE");
            Console.Out.Flush();
            Environment.Exit(0);
        }
        catch (Exception e)
        {
            Console.WriteLine($"{T} RESULT=FATAL {e.GetType().Name}: {e.Message}");
            Console.WriteLine(e.StackTrace);
            Console.Out.Flush();
            Environment.Exit(8);
        }
    }

    // ------------------------------------------------------------------ one def
    private static void Probe(string defName)
    {
        Log("");
        Log($"==== {defName} ====");

        ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
        if (def == null) { Log($"def={defName} missing"); return; }

        Log($"def={defName} category={def.category} animal={def.race?.Animal} humanlike={def.race?.Humanlike} uiIconPath='{def.uiIconPath}'");
        Log($"def.uiIcon {TexInfo(def.uiIcon)}");
        Log($"def.uiIconMaterial name='{def.uiIconMaterial?.name}' bad={ReferenceEquals(def.uiIconMaterial, BaseContent.BadMat)}");
        Diag("def.uiIconMaterial", def.uiIconMaterial);

        // ------------------------------ Def overload: exactly what the build-menu widget asks for
        try
        {
            Material defMat;
            Texture2D defTex = Widgets.GetIconFor(def, out defMat, null, null, null);
            Log($"deficon.geticonfor.def tex={TexInfo(defTex)} material='{defMat?.name}'");
        }
        catch (Exception e) { Log($"deficon.geticonfor.def failed {e.GetType().Name}: {e.Message}"); }

        // ------------------------------ the body graphic the animal's ThingDef.ResolveIcon used
        PawnKindDef kind = def.race?.AnyPawnKind;
        Log($"pawnkind={kind?.defName} lifeStages={(kind?.lifeStages == null ? -1 : kind.lifeStages.Count)}");
        if (kind?.lifeStages == null || kind.lifeStages.Count == 0) { Log("no life stages"); return; }

        PawnKindLifeStage stage = kind.lifeStages[kind.lifeStages.Count - 1];
        GraphicData gd = stage.bodyGraphicData;
        if (gd == null) { Log("no bodyGraphicData"); return; }

        Log($"body.texPath='{gd.texPath}' shaderType='{gd.shaderType?.defName}' shaderPath='{gd.shaderType?.shaderPath}' drawSize={gd.drawSize} color={gd.color}");

        Graphic bodyGraphic = gd.Graphic;
        Log($"body.graphic={(bodyGraphic == null ? "null" : bodyGraphic.GetType().Name)} path='{bodyGraphic?.path}'");
        Material bodyMat = bodyGraphic?.MatAt(Rot4.East);
        Log($"body.matAtEast name='{bodyMat?.name}' shader='{bodyMat?.shader?.name}'");
        Diag("body.east", bodyMat);

        // ------------------------------ the material Widgets.GetIconFor(Thing, ...) makes for a Pawn
        // Widgets.cs:216447-216455 (GetIconFor(Thing,...), Pawn branch):
        //     material2 = pawn.Drawer?.renderer?.BodyGraphic?.MatAt(rot)
        //     result    = material2.mainTexture
        //     if (TryGetUIShader(material2.shader, out uiShader) && TryGetRequestForMat(material2, out request))
        //         material = MaterialPool.MatFrom(request with shader = uiShader)
        Material thingUiMat = BuildUiMaterial(bodyMat);
        Log($"geticonfor.thing texture={TexInfo(bodyMat?.mainTexture as Texture2D)} material='{thingUiMat?.name}' shader='{thingUiMat?.shader?.name}'");
        Diag("geticonfor.thing.material", thingUiMat);

        if (def.uiIconMaterial == null)
        {
            Material built = thingUiMat;
            Log($"def.uiIconMaterial.wasNull built='{built?.name}' sameAsThingPath={ReferenceEquals(built, thingUiMat)}");
        }
        else
        {
            Log($"def.uiIconMaterial.sameObjectAsThingPath={ReferenceEquals(def.uiIconMaterial, thingUiMat)}");
        }

        // ------------------------------ coverage test: drop the def-level material, the 2.4.0 fix's action
        try
        {
            Material saved = def.uiIconMaterial;
            def.uiIconMaterial = null;
            Material defMat2;
            Texture2D defTex2 = Widgets.GetIconFor(def, out defMat2, null, null, null);
            Material thingMat2 = BuildUiMaterial(bodyMat);
            Log($"simulate.dropDefMaterial defOverload.material='{defMat2?.name}' tex={TexInfo(defTex2)} thingOverload.material='{thingMat2?.name}' shader='{thingMat2?.shader?.name}'");
            def.uiIconMaterial = saved;
        }
        catch (Exception e) { Log($"simulate.dropDefMaterial failed {e.GetType().Name}: {e.Message}"); }

        // ------------------------------ control: the same texture on a plain Cutout graphic
        try
        {
            Graphic cut = GraphicDatabase.Get<Graphic_Multi>(gd.texPath, ShaderDatabase.Cutout, gd.drawSize, gd.color);
            Material cutMat = cut?.MatAt(Rot4.East);
            Shader uiShader = null;
            bool mapped = cutMat?.shader != null && ShaderDatabase.TryGetUIShader(cutMat.shader, out uiShader);
            Log($"control.cutout material='{cutMat?.name}' shader='{cutMat?.shader?.name}' tex={TexInfo(cutMat?.mainTexture as Texture2D)} uiMap={mapped} uiShader='{(mapped ? uiShader?.name : "none")}' wouldBuild={(mapped ? "material" : "null(plain-texture path)")}");
            Diag("control.cutout", cutMat);
        }
        catch (Exception e) { Log($"control.cutout failed {e.GetType().Name}: {e.Message}"); }

        // ------------------------------ live GetIconFor(Thing), best effort (no map in this harness)
        try
        {
            Log($"live.game={(Current.Game == null ? "null" : "set")} map={(Current.Game?.CurrentMap == null ? "null" : "set")}");
            Pawn pawn = null;
            try { pawn = PawnGenerator.GeneratePawn(kind, (Faction)null); Log($"live.pawn.generated={(pawn != null)}"); }
            catch (Exception e) { Log($"live.pawn.generate.threw {e.GetType().Name}: {e.Message}"); }
            if (pawn != null)
            {
                Log($"live.pawn kind={pawn.kindDef?.defName} gender={pawn.gender} spawned={pawn.Spawned}");
                Step("live.drawer", () => pawn.Drawer);
                Step("live.renderer", () => pawn.Drawer.renderer);
                Step("live.bodyGraphic", () => pawn.Drawer.renderer.BodyGraphic);
                Step("live.ensureGraphics", () => { pawn.Drawer.renderer.EnsureGraphicsInitialized(); return "ok"; });
                Step("live.bodyGraphic.afterEnsure", () => pawn.Drawer.renderer.BodyGraphic);
                Step("live.bodyGraphic.east", () =>
                {
                    Graphic bg = pawn.Drawer.renderer.BodyGraphic;
                    if (bg == null) return null;
                    Material bm = bg.MatAt(Rot4.East);
                    return $"material='{bm?.name}' shader='{bm?.shader?.name}' tex={TexInfo(bm?.mainTexture as Texture2D)}";
                });

                float scale = 0f, angle = 0f;
                Vector2 prop = Vector2.one;
                Color col = Color.white;
                Material liveMat = null;
                try
                {
                    Texture icon = Widgets.GetIconFor(pawn, new Vector2(128f, 128f), Rot4.East, false,
                                                      out scale, out angle, out prop, out col, out liveMat);
                    Log($"live.geticonfor.thing tex={TexInfo(icon as Texture2D)} material='{liveMat?.name}' shader='{liveMat?.shader?.name}'");
                    Diag("live.geticonfor.thing.material", liveMat);
                }
                catch (Exception e) { Log($"live.geticonfor.thing.threw {e.GetType().Name}: {e.Message}"); }
            }
        }
        catch (Exception e) { Log($"live.fail {e.GetType().Name}: {e.Message}"); }
    }

    private static void Step(string tag, Func<object> f)
    {
        try
        {
            object v = f();
            Log($"{tag}={(v == null ? "null" : v.ToString())}");
        }
        catch (Exception e) { Log($"{tag}.threw {e.GetType().Name}: {e.Message}"); }
    }

    // ------------------------------------------------------------------ the exact UI-material construction
    private static Material BuildUiMaterial(Material src)
    {
        if (src == null) return null;
        try
        {
            Shader uiShader;
            MaterialRequest request;
            if (ShaderDatabase.TryGetUIShader(src.shader, out uiShader) &&
                MaterialPool.TryGetRequestForMat(src, out request))
            {
                request.shader = uiShader;
                return MaterialPool.MatFrom(request);
            }
        }
        catch (Exception e) { Log($"BuildUiMaterial failed {e.GetType().Name}: {e.Message}"); }
        return null;
    }

    // ------------------------------------------------------------------ diagnostics
    private static void Diag(string tag, Material m)
    {
        if (m == null) { Log($"{tag}.material=null"); return; }
        try
        {
            Shader sh = m.shader;
            int props = -1;
            try { if (sh != null) props = sh.GetPropertyCount(); } catch (Exception e) { Log($"{tag}.propcount threw {e.GetType().Name}"); }

            Log($"{tag}.name='{m.name}' shader='{sh?.name}' shaderProps={props}");

            WR.Shader engine = null;
            bool registered = false;
            if (sh != null)
            {
                try { registered = WR.ShaderRegistry.TryResolve(sh.name, out engine); } catch { }
                if (!registered)
                {
                    try { registered = WR.ShaderRegistry.TryResolve("Map/" + sh.name, out engine); } catch { }
                }
            }
            if (!registered || engine == null)
            {
                Log($"{tag}.registry=false");
            }
            else
            {
                var layout = engine.MaterialLayout;
                Log($"{tag}.registry=true key='{engine.Key}' path='{engine.ShaderPath}' layoutErrors={layout.HasErrors} uniforms={layout.UniformSlots.Length} textures={layout.TextureSlots.Length} uniformSize={layout.UniformSize} program={(engine.Program != null)} precompiled={(engine.PrecompiledStages != null)} flipV={engine.FlipTextureST}");

                foreach (var u in layout.UniformSlots)
                {
                    string extra = "";
                    if (u.PropertyName == "_MainTex_ST")
                        extra = $" defaultST=({F(u.DefaultValue.Vector4.X)},{F(u.DefaultValue.Vector4.Y)},{F(u.DefaultValue.Vector4.Z)},{F(u.DefaultValue.Vector4.W)}) authored={u.HasAuthoredDefault}";
                    Log($"{tag}.uniform property='{u.PropertyName}' type={u.Type} offset={u.Offset} isColor={u.IsColor}{extra}");
                }
                foreach (var t in layout.TextureSlots)
                    Log($"{tag}.texslot property='{t.PropertyName}' binding={t.Binding} kind={t.Kind} default={t.Default}");

                try
                {
                    WR.Material wr = m.engineMaterial;
                    if (wr != null && wr.Shader != null)
                    {
                        int id = UnityEngine.Shader.PropertyToID("_MainTex_ST");
                        System.Numerics.Vector4 st;
                        bool explicitSt = wr.TryGetVector(id, out st);
                        if (!explicitSt)
                        {
                            foreach (var u in layout.UniformSlots)
                                if (u.PropertyName == "_MainTex_ST") { st = u.DefaultValue.Vector4; break; }
                        }
                        var shaderSt = st;
                        if (engine.FlipTextureST) { shaderSt.Y = 1f - shaderSt.Y; shaderSt.W = -shaderSt.W; }
                        Log($"{tag}.st explicit={explicitSt} resolved=({F(st.X)},{F(st.Y)},{F(st.Z)},{F(st.W)}) shaderReceives=({F(shaderSt.X)},{F(shaderSt.Y)},{F(shaderSt.Z)},{F(shaderSt.W)})");
                    }
                }
                catch (Exception e) { Log($"{tag}.st threw {e.GetType().Name}: {e.Message}"); }
            }

            WR.Material pass = null;
            try { pass = m.engineMaterial; } catch (Exception e) { Log($"{tag}.engineMaterial threw {e.GetType().Name}: {e.Message}"); }
            bool passOk = pass != null;
            bool flag = passOk && pass.Shader != null && pass.Shader.Program != null;
            Log($"{tag}.imguidraw passMaterial={(passOk ? "ok" : "null")} program={(flag ? "ok" : "null")} path={(passOk ? (flag ? "material" : "plain") : "none")}");

            Log($"{tag}.mainTexture {TexInfo(m.mainTexture as Texture2D)}");
            Texture2D mask = null;
            try { mask = m.GetTexture("_MaskTex") as Texture2D; } catch { }
            Log($"{tag}.maskTexture {TexInfo(mask)}");
            try
            {
                Color c = m.GetColor("_Color");
                Color c2 = m.GetColor("_ColorTwo");
                Log($"{tag}.color=({F(c.r)},{F(c.g)},{F(c.b)},{F(c.a)}) colorTwo=({F(c2.r)},{F(c2.g)},{F(c2.b)},{F(c2.a)})");
            }
            catch { }
        }
        catch (Exception e) { Log($"{tag}.diag failed {e.GetType().Name}: {e.Message}"); }
    }

    private static string TexInfo(Texture2D t)
    {
        if (t == null) return "null";
        string bad = ReferenceEquals(t, BaseContent.BadTex) ? " BADTEX" : "";
        return $"'{t.name}' {t.width}x{t.height}{bad}";
    }

    private static string F(float v) => v.ToString("0.###");

    private static void Log(string s)
    {
        Console.WriteLine($"{T} {s}");
        Console.Out.Flush();
    }
}
