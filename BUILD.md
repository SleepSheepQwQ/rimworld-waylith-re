# AgentTouchFix — build notes

Harmony fix mod for the RimWorld 1.7 Android port `com.ports1de.waylith` (Waylith engine, .NET 10).
**No vanilla DLL is recompiled, patched on disk, or shipped.** Everything happens at runtime.

| item | value |
|---|---|
| mod name / folder | `AgentTouchFix` (`Mods/AgentTouchFix`) |
| packageId | `sleepsheep.waylith.agenttouchfix` |
| version | 1.0.0 |
| target framework | `net10.0` (same as `Assembly-CSharp.dll`, verified: `TargetFrameworkAttribute = .NETCoreApp,Version=v10.0`) |
| mod DLL | `Assemblies/AgentTouchFix.dll`, **20,480 bytes** (md5 `f5983f9f63c5945608a84a9618ebefb4`) |
| built by | GitHub Actions run **43440517642** (`.github/workflows/build-mod.yml`), `dotnet build -c Release` on `ubuntu-latest` with .NET SDK 10.0.x |
| result | build **success**, 0 warnings, 0 errors |
| artifact | `AgentTouchFix.zip` (10,591 B) → staged here as `~/.hermes/cache/scratch/agenttouchfix/AgentTouchFix.zip` |

Zip layout (root = mod folder, ready for `deploy_agenttouchfix.sh`):

```
AgentTouchFix/About.xml
AgentTouchFix/Assemblies/AgentTouchFix.dll
```

## 1. Compile-time references and where they came from

| reference | size | source | shipped? |
|---|---|---|---|
| `Assembly-CSharp.dll` | 15,471,104 B | already in the repo `dlls/` (pulled from the installed game via `run-as`, 1.7 build; all patch targets live here: `Verse.UI`, `Verse.IntVec3`, `RimWorld.Selector`, `LudeonTK.*`, `Verse.Log`) | no (`<Private>false</Private>`) |
| `0Harmony.dll` | 2,532,864 B | **NuGet `Lib.Harmony` 3.0.0-rc.3, `lib/net10.0/`** — pushed to `dlls/0Harmony.dll` in commit `86a1aa0` | no |
| `UnityStubs.dll` | 964,096 B | the game's own copy, extracted earlier with `run-as`, pushed to `dlls/UnityStubs.dll` for the record | no |

Notes on `0Harmony.dll`:

* The game's `files/game/0Harmony.dll` is 2,573,312 B and reports `FileVersion 3.0.0.0`,
  `ProductVersion 3.0.0.0-rc.3+3c0e32cd…`, `ProductName Harmony` — i.e. Harmony 3.0.0-rc.3, a
  different TFM build of the same package. The NuGet `net10.0` build has the same assembly
  identity (`0Harmony, Version=3.0.0.0`) which is what the CLR binds on at load time, so the
  mod resolves against the already-loaded game copy.
* Pulling the exact 2.5 MB file over `rish`/Shizuku was not possible reliably: rish splits large
  stdout between fd1 and fd2 in a non-deterministic order (verified by md5 per 220 KB chunk —
  no concatenation order reproduced the source bytes), so the fidelity-checked route was NuGet.
* `UnityStubs.dll` is **not** referenced by the csproj: the mod deliberately contains **zero**
  `UnityEngine` type references (verified: the built DLL has no `UnityEngine` assembly reference
  and no `UnityEngine` string). `UnityEngine.Vector3` is read by field reflection, `Time.frameCount`
  by property reflection, `MotionEvent` entirely by reflection (no `Mono.Android` reference).

Assembly references of the produced DLL, asserted by the CI step
`Verify no third-party assembly references` (fails the build otherwise):

```
assembly refs (metadata strings):
0Harmony
Assembly-CSharp
System.Runtime
reference check OK
```

i.e. **no** `PortUI`, `RwHost`, `Mono.Android`, `UnityEngine`, `Waylith` reference.

### 1.1 Duplicate-type hazard (fixed in commit `00f335e`)

`Ports1de.TouchGestureEngine` is compiled into **both** `RwHost.dll` and `PortUI.dll`
(confirmed: `dec22/decompiled/{RwHost,PortUI}/Ports1de/TouchGestureEngine.cs`; the PortUI copy has
no callers). `AccessTools.TypeByName` / `AppDomain` enumeration would return whichever copy is
found first, and a `SecondUp` patch applied to the dead PortUI copy would be a silent no-op.

Resolution order now used (`PointerPatches.PinEngine()`):

1. `RwHost.TouchControls.engine` field type — this is by definition the live engine instance type;
2. `Assembly.Load("RwHost").GetType("Ports1de.TouchGestureEngine", false)`;
3. global `TypeByName` search, which logs a **warning** naming the assembly it fell back to.

The same assembly-qualified preference (`Refl.TPrefer`) is used for every other target
(`Assembly-CSharp` for `Verse.*`/`RimWorld.*`/`LudeonTK.*`, `PortUI` for `PortUI.*`,
`RwHost` for `RwHost.TouchCamera`/`TouchControls`). Every successful patch logs the resolved
declaring assembly, so a live run can be checked at a glance:

```
[AgentTouchFix] AgentTouchFix 1.0.0.0 bootstrapping (harmony sleepsheep.waylith.agenttouchfix)
[AgentTouchFix] TouchGestureEngine pinned via TouchControls.engine -> assembly RwHost
[AgentTouchFix] patched RimWorld.Selector.HandleMapClicks -> Assembly-CSharp!RimWorld.Selector.HandleMapClicks
[AgentTouchFix] patched PortUI.MobileSelectionController.Tap -> PortUI!PortUI.MobileSelectionController.Tap
...
[AgentTouchFix] patch summary: ok=7 skipped=0 failed=0
```

### 1.2 Member shapes re-checked against the 1.7 decompile

* `RimWorld.Selector.HandleMapClicks()` — exactly one declaration, public instance, no
  arguments (the only other occurrence is the call site inside `SelectorOnGUI`).
* `RimWorld.Selector.dragBox` — `public DragBox dragBox = new DragBox();` (field).
* `RimWorld.DragBox.active` — `public bool active;` (field, read/write).
* `LudeonTK.DebugTool.clickAction` — `public Action clickAction;` (field). `ClickActionOf()` now
  reads the field first and falls back to a same-named readable **property** before giving up.

## 2. Patch list (7 patches, all registered via `AccessTools`, each one fails soft)

Bootstrap: `[StaticConstructorOnStartup] AgentTouchFix.Bootstrap` → `new Harmony("sleepsheep.waylith.agenttouchfix")`,
`H.Patch(...)` per target inside try/catch, `Log.Message/Warning/Error` with the `[AgentTouchFix] `
prefix, final line `patch summary: ok=N skipped=N failed=N`. A missing target is one red line and
nothing else breaks.

### A. Developer tools (LudeonTK `DebugTools.curTool`)

| # | target | patch | what it does |
|---|---|---|---|
| 1 | `PortUI.MobileSelectionController.Tap(Vector3, bool longPress, bool selectOnly)` | prefix `(object[] __args) → bool` | **the actual fix.** If `DebugTools.curTool != null`: consume the tap (`return false`, selection never runs). `longPress == true` → `curTool = null` (touch right-click); otherwise invoke `tool.clickAction` with `UI.MouseCell()` pinned to the tapped cell. Tool stays armed (vanilla left-click semantics), so grid tools can be tapped repeatedly. |
| 2 | `RimWorld.Selector.HandleMapClicks()` | prefix `(object __instance) → bool` | while a tool is armed, skip selector handling **and** disarm a half-open `dragBox` (`active = false`) so selection/drag cannot "stick". Real-mouse fallback path. |
| 3 | `Verse.UI.MouseCell()` | prefix `(ref IntVec3 __result) → bool`, **priority High (700)** | only while a tool click is being invoked: return the tapped cell. High priority beats PortUI's own `MouseCell` prefix (designator preview / `ContextualActionToolbar.TryCell`), which otherwise rewrites the cell a dev tool reads. The `Vector3` → cell conversion uses the tapped position with banker's rounding (`Mathf.RoundToInt` semantics, the same as `UIToIntVec3`), not the truncating `IntVec3(Vector3)` ctor. |
| 4 | `LudeonTK.DebugTool.DebugToolOnGUI()` | prefix `() → bool` | same-frame de-duplication: after a touch-fired tool, the frame's real `MouseDown` must not fire the same `clickAction` again. Frame clock = `UnityEngine.Time.frameCount` when resolvable, else `Environment.TickCount / 16`. |
| 5 | `PortUI.QuickOrderWheel.Open(Vector3)` | prefix `(object[] __args) → bool` | long press on empty map normally opens the quick-order wheel *instead of* calling `Tap`; while a tool is armed the wheel is suppressed and the tool is cancelled instead. Without this the long-press cancel would be unreachable on empty ground. |

### B. Stuck pointer / drag-becomes-zoom (RwHost `Ports1de`)

| # | target | patch | what it does |
|---|---|---|---|
| 6 | `Ports1de.TouchGestureEngine.SecondUp(long)` | postfix `(object __instance)` | force `state = Idle`, `twoMoved = twoPan = false`, `vx = vy = 0`, `inertia = false`, `twoDistance = twoStartDistance = 1`. This is the dead state the original leaves behind (`SecondUp` sets `State.Ignore`, and neither `Move()` nor `Up()` has an `Ignore` branch). |
| 7 | `RwHost.TouchControls.OnTouch(MotionEvent)` | prefix + postfix `(object __instance, object[] __args)` | prefix: on `ACTION_POINTER_UP (6)` and `ACTION_CANCEL (3)` reset the engine state and the pinch baseline and call `TouchCamera.Clear()` (zeroes `GameCamera.panX/panY/zoomLog`) **before** the original handler — this also runs ahead of the letter layer, which swallows `ACTION_POINTER_UP` (R10 in the findings) and can otherwise park the engine in `Two`/`Ignore` forever. Postfix: after `ACTION_POINTER_UP`, re-arm the fingers that are still down — 1 remaining finger → `engine.Down(x, y, eventTime, context)` then forced to `Pan` (never a stray `Tap` selection), 2 remaining → `engine.SecondDown(...)`. Net effect: the gesture continues on the remaining finger instead of dying until the next `ACTION_DOWN`. |

Read access to `MotionEvent` is reflection only: `Action`, `ActionMasked`, `PointerCount`,
`EventTime`, `GetX(int)`, `GetY(int)`; the lifted pointer index comes from `(Action >> 8) & 0xff`.

All field/method lookups are cached after the first call; a missing member produces one
`[AgentTouchFix]` warning and the dependent patch degrades to a no-op instead of throwing.

## 3. Known risks / limitations

1. **Not runtime-tested.** Nothing was installed on the device and `ModsConfig.xml` was not
   touched (that is the deployer's job). The patch points are derived from the 0.0.22 decompiled
   sources; the first in-game session should confirm in `Player.log`:
   `TouchGestureEngine pinned via TouchControls.engine -> assembly RwHost`,
   seven `patched …` lines each naming their assembly, and
   `patch summary: ok=7 skipped=0 failed=0`. If the engine line says PortUI, or any line reads
   `fell back to …`, the corresponding patch is mis-targeted and the log will say so.
2. **Designator selected.** `MobileTouchInput.Process` returns early when
   `Find.DesignatorManager.SelectedDesignator != null` (before it can reach `Tap`), so with a
   build/designator tool active a dev tool tap does nothing. Select nothing (Esc) first.
3. **Off-map taps** are filtered out by the port before `Tap` (`intVec.InBounds`), so clicking
   outside the map with a tool armed is a no-op rather than "tool fires at an invalid cell".
4. **`UI.MouseMapPosition()` is not overridden** (only `MouseCell()`), because returning a
   `UnityEngine.Vector3` would require a compile-time UnityEngine reference. Tools that read
   `UI.MouseMapPosition()` (rather than `UI.MouseCell()`) will use the cursor position that the
   engine pushed on `ACTION_DOWN` — normally the same spot, but not guaranteed.
5. **`MeasureTool` / `curMeasureTool`** is not handled (only `curTool`).
6. **Classic touch mode** (`TouchPreferences.Modern == false`, `ClassicTouchControls`) is not
   patched; the port defaults to modern. The tool fixes still work, the pointer fixes do not.
7. **3+ finger sequences**: the port has no state branch for them at all (`action 5` with
   `PointerCount > 2` calls `Cancel()`). We reset state so nothing stays stuck, but we do not
   synthesise a pinch for 3 fingers; after one lifts, the remaining two are re-armed.
8. **Native mouse-button slot**: the port's `glfwAndroidPush*` shim has a single-slot event
   buffer, so a press/release pair can be collapsed natively (findings §0.4). That is outside
   managed reach and is *not* fixed here.
9. The NuGet `0Harmony` build differs from the shipped one byte-wise (same version/identity).
   If the port ever ships a Harmony build with a different API, patches will fail loudly in the
   log rather than silently.

## 4. Reproduce / rebuild

```bash
# repo: SleepSheepQwQ/rimworld-waylith-re
gh workflow run build-mod.yml -R SleepSheepQwQ/rimworld-waylith-re
gh run list    -R SleepSheepQwQ/rimworld-waylith-re --workflow build-mod.yml --limit 1
gh run download <run-id> -R SleepSheepQwQ/rimworld-waylith-re -n AgentTouchFix -D dl
unzip -l dl/AgentTouchFix.zip
```

Repo layout:

```
.github/workflows/build-mod.yml
mod/AgentTouchFix.csproj      # HintPath ..\dlls\Assembly-CSharp.dll, ..\dlls\0Harmony.dll, Private=false
mod/About.xml
mod/Source/AgentTouchFixMod.cs   # bootstrap + reflection helpers
mod/Source/ToolPatches.cs        # A. developer tools
mod/Source/PointerPatches.cs     # B. touch state machine
dlls/Assembly-CSharp.dll dlls/0Harmony.dll dlls/UnityStubs.dll ...
```

The workflow refuses to publish an artifact if any vanilla/game assembly ends up in
`AgentTouchFix/Assemblies/`.

## 5. v3-diagnostic build (version 1.1.0) — read-only instrumentation

Purpose: find where "one finger drag turns into a zoom" comes from on the **classic** touch
chain. This build changes **no behaviour**: every new patch is a prefix that only reads fields
and appends one line to `files/game/agenttouchfix.log`. The seven fix patches are untouched,
and `SecondUp_Postfix` keeps resetting state exactly as before (it just logs the pre-reset state).

Live chain on this device: `mobile-controls.txt` contains `5` → `TouchPreferences.Options = 5`,
`Modern = (5 & 2) != 0 = false`, so `RwHost.TouchControls.OnTouch` takes
`if (flag) return classic.OnTouch(e);` — `RwHost.ClassicTouchControls.OnTouch` is the code that
actually runs, and `Ports1de.TouchGestureEngine` is never driven on touch.

| # | patch target | line written | throttle |
|---|---|---|---|
| 1 | `RwHost.GameCamera.Add(dx, dy, zoom)` | `Add dx=.. dy=.. zoom=.. frames: <f0> \| <f1> \| <f2> \| <f3>` | only when `|zoom| > 0.0001`; max 5 lines per second; `sup=N` when lines were dropped |
| 2 | `RwHost.TouchCamera.Add` | `tcam.Add dx=.. dy=.. zoom=.. frames: <3 frames>` | same rule (separate budget) |
| 3 | `RwHost.ClassicTouchControls.OnTouch` | `classic act=.. ptr=.. mode=.. x=.. y=.. [x1= y1=] pz=.. pd=.. pm=.. pui=..` | `ACTION_MOVE` max 1 per 100 ms; **every other action always**; MOVE with `ptr<2` while `mode` is `Pinch`/`Ignore` always (marked `STUCK-PINCH-SINGLE` / `STUCK-IGNORE-SINGLE`) |
| 4 | `RwHost.TouchControls.OnTouch` | `modern act=.. ptr=.. state=Name/int classic=0/1 x=.. y=..` | `ACTION_MOVE` max 1 per 100 ms |
| 5 | `Ports1de.TouchGestureEngine.SecondUp` | `SecondUp -> state=<pre> (reset to Idle/0)` | none (rare) |

Install-time lines (once, at bootstrap):
`diag: ClassicTouchControls resolved … mode=ok pinchZoom=ok …`, `diag: TouchControls resolved … engine=Ports1de.TouchGestureEngine classicMode=ok`,
`diag: touch options=5 guide=1 modern=0 inertia=1`, `diag v3 summary: ok=4/4 skipped=0 failed=0`.
If a field is missing the log says `MISSING`/`?` once and the line is still written — nothing throws.

Two patch lines per zoom request is deliberate: `tcam.Add` is the entry every gesture branch calls,
`Add` is `GameCamera`'s accumulator (`TouchCamera.add` is a delegate pointing at it, so a patched
`GameCamera.Add` still catches it). Adjacent `tcam.Add` + `Add` with the same zoom = healthy
forwarding; `Add` with no `tcam.Add` = something else called the accumulator.

`ACTION_*` codes: 0 DOWN, 1 UP, 2 MOVE, 3 CANCEL, 5 POINTER_DOWN, 6 POINTER_UP.
`ClassicTouchControls.Mode`: None/Pending/Held/Pan/Scroll/PreviewDrag/LeftDrag/Pinch/Ignore.

How to read the report:

* `zoom` non-zero on a `classic act=2 ptr=1 mode=Pinch` line → a single finger is being
  processed by the pinch branch: `pointerCount` really is 2 for that event (check `x1/y1`) or
  `mode` was left at `Pinch` after a finger lifted.
* `mode=Pinch` + `ptr=1` with `pd` (pinchDistance) jumping between lines → the baseline is being
  re-seeded with a one finger distance, so the next `num4 = log(num/pinchDistance)` is a big zoom.
* `STUCK-IGNORE-SINGLE` after a two finger gesture → the dead-pointer residue
  (`case 6` sets `mode = Ignore`, and `Ignore` has no branch in MOVE).
* on the world map `GameCamera.WorldPrefix` turns zoom into `desiredAltitude` (world zoom), on a
  map into `CameraDriver.desiredSize`; the stack in the `Add` line says which one.

## 6. v4-toolsnap build (version 1.2.0) — tap precision

User report on the v3 build: dev tools "work, but only if the tap lands exactly on the entity; a
few pixels off and it hits empty ground". Two causes, both fixed here:

1. only `Verse.UI.MouseCell` was pinned — tools that read `Verse.UI.MouseMapPosition()` (a
   `UnityEngine.Vector3`) still saw the cursor position the port pushed, not the tap;
2. the tap was forced onto the *rounded* cell, throwing away the port's own forgiveness
   (`PortUI.MobileSelectionController.Candidates` resolves the exact cell first and then, when
   that cell holds no `Thing`, every `Thing` in the surrounding 5x5 cells within a 28.8 px
   screen radius, nearest first).

### 6.1 New / changed patches

| # | target | change |
|---|---|---|
| 8 | `Verse.UI.MouseMapPosition()` | **new** prefix `(ref T __result) -> bool`, priority High (700), same as `MouseCell`. While a `clickAction` runs it returns the exact snapped map position; no tool click -> passthrough. |
| 3 | `Verse.UI.MouseCell()` | now returns the *snapped* cell (`Thing.Position`) instead of the rounded tap cell. |
| 1 | `PortUI.MobileSelectionController.Tap` | unchanged consumption, but `Fire()` now resolves the target through `Candidates` and logs it. |
| diag | `Add`, `tcam.Add`, `classic`, `modern` | v3 lines kept, per-second budget lowered to **2 lines/s** (`Add`/`tcam.Add` were 5, MOVE lines were ~10). |

`MouseMapPosition` returns `UnityEngine.Vector3` and this mod deliberately carries **zero**
compile-time `UnityEngine` references (the CI step rejects a `UnityEngine` assembly reference, and
section 1 of this file claims none). The prefix is therefore a generic method:

```csharp
internal static bool MouseMapPosition_Prefix<T>(ref T __result)
```

and `Bootstrap.MouseMapPositionPrefix()` closes it over
`Refl.M(Verse.UI, "MouseMapPosition").ReturnType` at install time, so the closed `MethodInfo` really
takes `ref UnityEngine.Vector3`. The value is carried as a boxed `Vector3` (`Thing.DrawPos` read by
reflection, or the tap position itself) and unboxed in the prefix. `tests/HarmonyProbe/` proves the
mechanism in CI: it patches a stand-in struct-returning method with the same shape and asserts
passthrough, override, restore-after-disable and wrong-type fallback. If the generic closure fails
at runtime the mod logs one warning and `MouseCell` pinning keeps working as before.

### 6.2 Snapping rules

* `Fire()` logs every tap first:
  `tool fire tap=(x,z) cell=(x,z) things=N` (`N` = `Find.CurrentMap.thingGrid.ThingsListAt(cell).Count`).
  `things=0` + a `tool snap ->` line = the tap missed the entity and the snap caught it;
  `things=0` + no snap line = the tap really did land on empty ground.
* `PortUI.MobileSelectionController.Candidates(tapPos)` is invoked by reflection; if its first
  element is a `Thing`, `MouseCell` is pinned to `thing.Position`, `MouseMapPosition` to
  `thing.DrawPos` (or to the tap position if `DrawPos` cannot be read), and
  `tool snap -> <defName> at <cell>` is logged.
* No candidate -> old behaviour minus the rounding loss: `MouseCell` gets the rounded tap cell,
  `MouseMapPosition` gets the exact tap coordinate.
* Every reflection failure is contained: one warning on first failure, then the raw tap is used.

### 6.3 Caveat

Snapping is intentionally forgiving, which means an empty-cell tool (terrain, spawn, dig
designators) tapped next to a wall or item will be snapped onto that thing. The `tool snap ->` line
names the target every time, so read the log before blaming the tool.

## 7. v5-quiet build (version 1.3.0) — no more log dialog

The functional patches are byte-for-byte the v4 ones; only the logging changed.

### 7.1 What was wrong

`Refl.Info/Warn/Err` called `Verse.Log.Message/Warning/Error` on every line. RimWorld opens its
"there were errors / warnings" window for the latter two, so a single skipped patch target or one
reflection hiccup on a tap produced a dialog box, and the v3/v4 diagnostics added ~2 lines/s on
top of it.

### 7.2 New logging contract

| call | file `files/game/agenttouchfix.log` | in-game console | dialog |
| --- | --- | --- | --- |
| `Refl.Info` | always | only when the diag flag exists | never |
| `Refl.Warn` | always | never | never |
| `Refl.Err` (runtime failures: taps, gestures) | always | never | never |
| `Refl.ErrOnce` (patch failed to apply at startup) | always | once per process | at most one, ever |

* Default is silent: no `Verse.Log` call at all.
* Bounded size: the sink keeps one `FileStream(FileMode.Append, FileShare.ReadWrite)` + a reused
  `StreamWriter` open (no per-line open/close), flushes one line at a time, and when
  `agenttouchfix.log` passes **1 MB** it is renamed to `agenttouchfix.log.1` (the previous `.1` is
  replaced) and a fresh file is started. The length is only probed every 200 lines or every 30 s
  (`Environment.TickCount`), never per line, so the cap costs nothing on hot paths.
  Worst case on disk: ~2 MB (log + one rotated file).

### 7.3 Diagnostics switch

```
files/game/agenttouchfix.diag      # create an empty file, restart the game
```

* Present → the four diagnostic prefixes (`GameCamera.Add`, `TouchCamera.Add`,
  `ClassicTouchControls.OnTouch`, `TouchControls.OnTouch`) are installed as in v3/v4 and every INFO
  line is mirrored to the console via `Log.Message` (`Warning`/`Error` are never mirrored, so no
  dialog can appear).
* Absent → `Diag.Install()` returns without patching anything, and each prefix also starts with
  `if (!Refl.DiagOn) return;`, so a disabled build costs zero per-event work: no Harmony hop, no
  string building, no reflection, no file write.
* Read once, in `Bootstrap`, before the first patch is applied — toggling needs a restart.
