# Loading-chain facts already established (read this before re-deriving anything)

Scope: this port is RimWorld 1.7.9795 on the Waylith engine (`com.ports1de.waylith`). Reads below are
from `defgraphic/src/Assembly-CSharp.decompiled.cs` (1.7.9795 decompile, kept in this repo) and from
`~/dcport/PortUI.Mods/ModCrashGuard.cs` (PortUI.dll decompile).

If you are about to prove one of these again, don't. Use the line numbers, cite them, and spend your
budget on the OPEN questions at the bottom.

## Settled: a failing mod cannot stop later mods from initialising

1. `Verse.StaticConstructorOnStartupUtility.CallAll()` — line 27597 of the decompile.
   Every type gets its own `try { RuntimeHelpers.RunClassConstructor(type.TypeHandle); }
   catch (Exception ex) { Log.Error(...); }`. The loop keeps going.

2. `PortUI.Mods.ModCrashGuard` (PortUI.dll) only wraps that call.
   `Initializers()` (line 361) transpiles `CallAll`, replacing `RuntimeHelpers.RunClassConstructor`
   with `ModCrashGuard.RunInitializer`. `RunInitializer` (line 375) records the failure and then
   `throw;`s — the exception is still caught by vanilla's per-type catch above.
   Net semantics: unchanged, just better logging.

3. `ModAssemblyHandler.ReloadAll()` — line 109040.
   `Assembly.LoadFrom` is inside a per-file `try/catch`; on failure it logs
   `Exception loading {file}` and `break`s out of *that mod's* file loop only. Other mods are untouched.

4. Duplicate package ids do not abort loading.
   - `ModLister` line 110628: `Log.Error("Tried loading mod with the same packageId multiple times: ... Ignoring the duplicates.")`
   - `ModsConfig` line 112339: `Log.Warning("There was more than one enabled instance of mod with PackageID: ... Disabling the duplicates.")`

## Settled: the Dev In Your Language 1.6 build cannot touch the icon path

`DevInYourLanguage.dll` (workshop 2142743468, md5 `d1da86964f4c9b9cb387f9691e06d3bb`, 27,136 B) has no
member reference to `Widgets.GetIconFor`, `MaterialPool.MatFrom`, `Graphic.MatAt`, `uiIcon`,
`Texture`, `Material`, `Graphic` or `ContentFinder`. Its game-side references are only:
`Translator.{CanTranslate,Translate,TranslateSimple}`, `UIScaling.AdjustRectToUIScaling`,
`Scribe_Values.Look`, `Text.CurFontStyle`, `Prefs.UIScale`, `DebugActionAttribute.name`.

It also never constructs `DebugActionAttribute` (no `.ctor` member reference), which is why it does not
repeat the 1.5 F# build's `MissingMethodException: DebugActionAttribute..ctor(8 args)` crash — 1.7's
ctor takes 9 parameters.

## Settled: what the 1.5 F# build failed on (do not retry it)

`DevL10N.dll` 1.5 (100,864 B) aborts in its static ctor:
- `FileNotFoundException: Poet, Version=5.4.0.0` — its F# transpiler helper library, absent on this port.
- references `Assembly-CSharp 1.5.8887` and emits IL for the 8-parameter `DebugActionAttribute` ctor,
  which 1.7 no longer has.

## OPEN — these are the questions worth spending budget on

A. Can GlyphFix's Harmony patches be silently disabled when another mod patches the same method first?
   Test: pre-patch `Widgets.GetIconFor(...)` with a `ref __result` postfix, then install GlyphFix,
   then call the real `GetIconFor` and check whether the material is still dropped.
   Relevant file: `mod/Source/IconFallbackFix.cs` in `SleepSheepQwQ/rimworld-waylith-icon-fix`.

B. Does GlyphFix install at all when `DevInYourLanguage.dll` is loaded in the same session?
   Test: headless harness, run `DevL10N.StartupConstructor` type initialiser first, then GlyphFix's,
   then measure whether `GetIconFor` still returns `material=null`.

C. Is there any code path where a *definition* (not the mod list) loses its `uiIcon` material, e.g. a
   save loaded with 50 missing mods? If yes, blank build-menu icons would appear with GlyphFix healthy.

## How to quote this doc

Say "established in docs/LOADING-FACTS.md §N (Assembly-CSharp.decompiled.cs:LINE)" and move on.
