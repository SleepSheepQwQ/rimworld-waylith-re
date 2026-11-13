# AgentTouchFix — build notes

Harmony fix mod for the RimWorld 1.7 Android port `com.ports1de.waylith` (Waylith engine, .NET 10).
**No vanilla DLL is recompiled, patched on disk, or shipped.** Everything happens at runtime.

| item | value |
|---|---|
| mod name / folder | `AgentTouchFix` (`Mods/AgentTouchFix`) |
| packageId | `sleepsheep.waylith.agenttouchfix` |
| version | 1.7.0 (this file's section 11) |
| target framework | `net10.0` (same as `Assembly-CSharp.dll`, verified: `TargetFrameworkAttribute = .NETCoreApp,Version=v10.0`) |
| mod DLL (v9) | `Assemblies/AgentTouchFix.dll` — see §11.4 (v6 was 48,128 B / md5 `ffc51b6cf1f4bb69d306640bc08c374f`) |
| built by | GitHub Actions (`.github/workflows/build-mod.yml`), `dotnet build -c Release` on `ubuntu-latest` with .NET SDK 10.0.x; run id per version in §11.4 |
| result | build **success**, 0 warnings, 0 errors |
| artifact | `AgentTouchFix.zip` → staged under `~/.hermes/cache/scratch/agenttouchfix/v9/` |

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

## 8. v6-snaptip build (version 1.4.0) — tighter snapping + tooltip suppression

Two user reports on v5:

1. *"the snap radius is too big"* — tapping empty terrain next to a wall or a dropped item put the
   tool on the item, so clear-terrain / spawn tools kept hitting the wrong cell;
2. *"the hover text stays on screen after a touch"* — the tooltip covers the button labels.

### 8.1 Snapping is now hit tested (`SnapGeometry.cs`)

`PortUI.MobileSelectionController.Candidates(Vector3)` (tap position → `List<object>`, exact cell
first, then the 5x5 neighbourhood inside 28.8 px, nearest first) is still the source of candidates,
but it is only a **proposal**: `SnapGeometry.Resolve` accepts the first candidate that passes

* **(a) footprint** — the tapped map position lies inside `GenAdj.OccupiedRect(thing)` (the thing's
  own cells, i.e. what its graphic covers), or
* **(b) proximity** — the tapped point is at most **16 dp** away from the projected screen position
  of `Thing.DrawPos`, where the radius is also capped at **half a cell on screen**, so zooming out
  can never widen the snap into neighbouring cells (`min(16 * dpi/160, 0.5 * cellPx)`, floor 6 px,
  ceiling 64 px).

Nothing passes → the raw tap is used (exact position for `UI.MouseMapPosition`, floor cell for
`UI.MouseCell`). If PortUI offered **no** candidate at all and `Find.Selector.SingleSelectedThing`
is set, that thing is targeted instead (tools that answer to the selection).

Also fixed: the tap → cell conversion used `Math.Round`, so a tap at 123.7 landed in cell **124**.
It now floors (matching `UI.MouseMapPosition().ToIntVec3()`), which on its own removed a whole class
of "empty ground, neighbouring item" snaps.

Screen projection is pure reflection (`Verse.Find.Camera` → `Camera.WorldToScreenPoint`, screen y
flipped with `Camera.pixelHeight`, density from `UnityEngine.Screen.dpi`) — the mod still carries no
compile-time `UnityEngine` / `PortUI` / `RwHost` reference, and the CI guard still passes.

### 8.2 Mode switch

```
files/game/agenttouchfix.snap
```

| content | behaviour |
| --- | --- |
| `auto` (or file absent) | the hit test above — **default** |
| `always` | v5 behaviour: nearest candidate wins, no hit test |
| `off` | never snap: exact tap position + floored cell |
| anything else | one WARN line, `auto` |

Read once in `Bootstrap` (`SnapGeometry.ReadMode`), logged as
`snap mode=auto (…/agenttouchfix.snap = 'auto')` plus one line with the density scale
(`snap: density scale 2.5 (400 dpi) -> 16dp = 40px before the half cell cap`).

Per tap the log now shows the decision:

```
tool fire tap=(123.4,45.1) cell=(123,45) things=0 snap=auto
tool snap -> Steel at (124,0,45) (near 9.6/24.0px)
tool snap none - using the raw tap
debug tool fired at (123,0,45) (raw tap)
```

### 8.3 Tooltips (`TooltipPatches.cs`)

`Verse.TooltipHandler` registers a tip when `Event.current.type == Repaint && Mouse.IsOver(rect)`,
and the port parks the cursor on the last tap, so that condition is true forever. The mod now
prefixes **all six** `TipRegion` / `TipRegionByKey` overloads of this build (2 × `TipRegion`,
4 × `TipRegionByKey`) with one **parameterless** `bool` prefix (returning `false` skips only the
registration; `Event.current.Use()` is never called, so surrounding GUI is untouched):

| state | result |
| --- | --- |
| no touch event for 2 s | unchanged (desktop / real mouse) |
| finger held ≥ 600 ms | tip allowed — long press asks for the description |
| ≤ 1500 ms since the last tap / pointer still parked | suppressed |

Timestamps are fed from **both** touch chains: `RwHost.TouchControls.OnTouch` (modern, patched since
v3) and `RwHost.ClassicTouchControls.OnTouch` (new patch, timestamp only — `mobile-controls.txt`
without bit 2 uses the classic chain and would otherwise never be seen). "Parked" means
`Input.mousePosition` has not moved for 300 ms, sampled once per frame, so a real mouse that moves
ends the suppression immediately. One log line per 10 s: `tooltip: N tip(s) suppressed in touch mode`.

The binding mechanism (one parameterless prefix on differently shaped overloads, skip on `false`,
normal on `true`) is exercised in CI by `tests/HarmonyProbe` (`OVERLOAD PROBE OK`).

### 8.4 Build

* commits `bec67a5` (mod) / `7fc0376` (probe) / `f5db8e2` (empty-Candidates rule), run **43458414120**,
  `dotnet build -c Release`:
  **0 warnings, 0 errors**, reference guard + engine-pin literals OK.
* `Assemblies/AgentTouchFix.dll` **48,128 bytes**, md5 `ffc51b6cf1f4bb69d306640bc08c374f`
  (successive v6 builds differ only in the PE timestamp / MVID / checksum, ~94 bytes; the IL is
  identical).
* artifact staged at `~/.hermes/cache/scratch/agenttouchfix/v6/AgentTouchFix-v6.zip`
  (zip md5 `37dac204647d006cc19eb1e1cb0b9272`, 22,264 B), unzipped next to it in `v6/AgentTouchFix/`.
* **not deployed** — v5 is still what runs on the device.

## 9. v7-txtsel build (version 1.5.0) — one texture trim, building select-all, IME text

Three independent reports, three independent patches. **Nothing from v6 was removed or relaxed**:
tap snapping (`always`/`auto`/`off`), tooltip suppression, the debug-tool path, the pointer/gesture
fixes and the file-only logging contract are byte-for-byte the same code paths.

| # | report | patch target (this build) | file |
|---|---|---|---|
| A | textures blink / turn grey after a memory pressure event | `RwHost.AndroidMemoryAndGcGovernor.GameBoundary` (private static, 0 params), `.RenderBoundary` (internal static, 0 params) | `GovernorPatches.cs` (new) |
| B | double tapping a wall / workbench does not select all of them | `PortUI.MobileSelectionController.Tap` (public static, 3 params) — the already-installed prefix, extended | `DoubleTapSelect.cs` (new), `ToolPatches.cs`, `SnapGeometry.cs` |
| C | typed text is dropped, Chinese input looks broken | `Ports1de.TextEditMailbox.TryTake` (internal, 3 params: `TextEditTarget`, `long`, `out string`) | `TextInputPatches.cs` (new) |

### 9.1 A — a single pressure event was answered twice

> **Removed in v9 (`1.7.0`, §11): this patch is gone.** The whole `GovernorPatches` file and its
> three install points (`GameBoundary`, `RenderBoundary`, `OnTrimMemory`) were deleted, and v9 must
> never patch a governor method or call a texture entry point again. Kept below as history only.

```
GameBoundary()      [game thread, per frame via AppContext "Ports1de.MemoryPressureAtFrame"]
    if (pending == 0) return;
    ...
    release?.Invoke();                       // 1st: ReleaseBuffers() on every decoded staging buffer
    Volatile.Write(ref renderTrim, 1);
RenderBoundary()    [render thread, VulkanLifecycleAndSwapchainGuard.BeforeFrame]
    if (Interlocked.Exchange(ref renderTrim, 0) != 0) release?.Invoke();   // 2nd
```

The first release is at the game boundary, before the render thread consumes this frame's texture
uploads. The second one fires on the *next* render frame — the frame that consumes exactly those
queued uploads — so buffers that were decoded but not yet uploaded are evicted, the upload is lost,
and the texture shows as a grey block / flickers until it is decoded again.

Fix, both prefixes:

* `GameBoundary_Prefix` — records `Environment.TickCount64` **only when `pending != 0`**. That
  condition is the whole point: `GameBoundary` runs every frame and returns immediately when nothing
  is pending, so an unconditional timestamp would suppress the render trim forever (renders are
  16 ms apart, the window is 500 ms).
* `RenderBoundary_Prefix` — if `renderTrim == 1` **and** a recorded release is less than 500 ms old,
  `return false` (skip). `renderTrim` is *not* touched, so that trim is deferred to the first render
  frame more than 500 ms later, not lost. When `renderTrim == 0` the original is a no-op and is left
  alone; a `RenderBoundary` with no recent `GameBoundary` (pause, surface recreate, device loss)
  always runs.

Log (file only, rate limited to 1/s):
`render trim skipped: 16 ms after the game boundary already released the textures (renderTrim left set, retried after 500 ms) skips=N`

Risk: a real trim can now be delayed by up to 500 ms (memory pressure is answered half a second
later instead of immediately twice). That is the intended trade — the pixels are what was broken.

### 9.2 B — the double-tap branch tested the wrong category

`PortUI.MobileSelectionController.Tap` has:

```csharp
if (same != null && same == lastTappedThing && Time.unscaledTime - lastTap < 0.4f
    && same.def.category == ThingCategory.Item && !same.def.neverMultiSelect)
```

`ThingCategory` in this build is `None=0, Pawn=1, Item=2, Building=3`, so `== Item` can never be
true for a building. Vanilla `RimWorld.Selector.SelectAllMatchingObjectUnderMouseOnScreen()` has no
category restriction at all (desktop double click).

`ToolPatches.Tap_Prefix` (no dev tool armed) now calls `DoubleTapSelect.TryConsume`:

1. PortUI's own candidate picker (`MobileSelectionController.Candidates`, shared with the snapping
   code) resolves the thing under the tap — a tap a few pixels off the wall still counts, and the
   thing, not the pixel cell, is the identity key (the port fires its `DoubleTap` signal for two ups
   < 300 ms and < 40 dp apart, which at high zoom spans several cells);
2. it is taken over only when it is the same `Thing` as the previous tap, the gap is < 400 ms,
   `def.category == Building` and `!def.neverMultiSelect` (the last two are exactly the vanilla
   preconditions);
3. `ToolPatches.PushCellPin(thing.Position, thing.DrawPos)` pins `UI.MouseCell()` /
   `UI.MouseMapPosition()` (the nested-safe version of the pin the tool path already uses), the
   vanilla `SelectAllMatchingObjectUnderMouseOnScreen()` is invoked, `PopCellPin()` restores;
4. `return false` from the prefix, so the rest of `Tap` is skipped. The selection is **not** cleared
   first — the vanilla call only adds the matching things.

Log: `dbl-tap building select-all -> Wall at (12,0,34) (selected 1 -> 7)`.

If `SelectAllMatchingObjectUnderMouseOnScreen` cannot be resolved, one WARN is written and the tap
falls through to the unmodified single select (behaviour unchanged, not broken).

Note: `MobileTouchInput` passes `Tap(pos, kind == Context, kind != Context)`, so `selectOnly` is
**true for every ordinary tap**. It is therefore not used as a filter; only a long press is excluded.

### 9.3 C — the IME answer was thrown away when the field moved

```csharp
public bool TryTake(TextEditTarget field, long now, out string text)   // lock (gate)
    if (hasPending && now > expires) hasPending = false;
    if (!hasPending || field != target) return false;                  // <-- whole struct compare
    hasPending = false; text = pending; pending = null; return true;
```

`TextEditTarget` is `record struct (int Window, int Control, float X, float Y, float Width, float
Height)` and `NativeTextBridge.Target(rect)` rebuilds it every frame from
`GUIUtility.GUIToScreenPoint(rect.position)`, `rect.width/height`, `GUIUtility.keyboardControl` and
`Find.WindowStack.currentlyDrawnWindow.ID`. Any layout change between opening the editor and the IME
answering — scrolled window, re-laid-out dialog, the soft keyboard resizing the screen — changes
X/Y/Control, the equality fails and the text is dropped (it is only cleared on a 10 s expiry). An
IME commit for Chinese always takes long enough to hit this.

`TryTake_Postfix` (`ref string text`, `ref bool __result`, `object[] __args`, `object __instance`)
runs only when the exact match already failed, takes the mailbox's own `gate` lock, and then:

* `hasPending` must be true and `now <= expires` (a genuinely waiting answer),
* `pending.Target.Window == field.Window` (both read by reflection, `int.MinValue` when missing),
* when the window id is `-1` (no window drawn, where "same window" proves nothing) the control id
  must match as well; otherwise the control id may differ — it is only reported.

On a hit: `text`, `__args[2]`, `__result = true`, and `hasPending`/`pending` cleared, so the answer
is delivered exactly once. Log per hit (the diff is what makes a wrong delivery visible):

```
ime text recovered: window 4021 control 99043 -> 99043 (same) rect (312.5,844.0,880.0,96.0) -> (312.5,712.0,880.0,96.0) chars=3 relaxed=1
```

Risk: if two text fields in the same window are pumped in the same frame, the pending text goes to
the first one pumped. It is one answer, delivered once, in the window it was typed in — the failure
mode this replaces is "the text is lost".

### 9.4 Verification and build

* metadata check against the shipped `dlls/` references (`dnfile`): `RwHost.AndroidMemoryAndGcGovernor`
  has `GameBoundary` (0 params) and `RenderBoundary` (0 params); `Ports1de.TextEditMailbox.TryTake`
  has 3; `RimWorld.Selector.SelectAllMatchingObjectUnderMouseOnScreen` has 0;
  `PortUI.MobileSelectionController.Tap` has 3.
* the produced DLL contains `TryTake_Postfix(__instance, __args, text, __result)`,
  `GameBoundary_Prefix()`, `RenderBoundary_Prefix()`, `TryConsume`, `FirstCandidate`, `PushCellPin`,
  `PopCellPin`, and the literals `dbl-tap building select-all`, `ime text recovered`,
  `render trim skipped`, `BUILD v7-txtsel`.
* assembly references of the DLL are still exactly `0Harmony`, `Assembly-CSharp`, `System.Runtime` —
  the CI guard passes, i.e. no `PortUI` / `RwHost` / `UnityEngine` / `Mono.Android` reference was
  introduced by reading all of this through reflection.
* CI run **43460212057** (mod commit `daf6273`) and **43461618422** (About.xml commit `e650034`),
  `dotnet build -c Release`: **0 errors**; two runs produced a byte-identical DLL
  (`374c5baf…`), so the docs commit did not change the binary.
* `Assemblies/AgentTouchFix.dll` **55,808 bytes**, md5 `374c5baf33be538c51e4b8cb4a23ba3b`
  (sha256 `3ec03355388d18b5caf5eb344115b645179ea2b3843327ccce54f8f8d58a9e87`); v6 was 48,128 B.
* artifact: `AgentTouchFix.zip` **26,117 bytes**, md5 `56bb7e652c179c94050abefaa01c3e96`, staged at
  `~/.hermes/cache/scratch/agenttouchfix/v7/AgentTouchFix.zip`, unzipped next to it in
  `v7/AgentTouchFix/`.
* **not deployed and not run in the game** (as instructed): the three fixes are compile-verified and
  metadata-verified, not play-tested. The first gameplay check should be the three reports that
  motivated them — a pressure event (or the log line above), a double tap on a wall, and a
  Chinese IME commit into a text field.

## 10. v8-inputrel build (version 1.6.0) — the host never sends the release

All four fixes are in `RwHost.MainActivity` / `RwHost.AndroidMemoryAndGcGovernor`, i.e. one layer
above everything v3–v7 touched (those were all in the game or in PortUI). New file:
`mod/Source/InputReleasePatches.cs` (A, B, C); D is one more prefix in `GovernorPatches.cs`.

### 10.1 A — mouse buttons stayed pressed

The host keeps a mirror of the held buttons in `MainActivity.mouseButtons` (private int) and the
diff loop in `MouseEvent` is the **only** place a release is ever forwarded:

```csharp
int num = (int)e.ButtonState;                 // bitmask 1,2,4,8,16 == buttons 0..4
...
for (i = 0..4) if ((mouseButtons & bit) != (num & bit))
                   Native.glfwAndroidPushMouseButton(i, (num & bit) != 0 ? 1 : 0, 0);
mouseButtons = num;
```

Three lifecycle paths clear that mirror **without** forwarding anything (`SurfaceDestroyed:899`,
`OnConfigurationChanged:915`, `OnWindowFocusChanged(false):945` do `mouseButtons = 0;`), and
`OnPause:986` does not even do that. Once the mirror is 0 while the engine still holds the button
down, the next event computes a 0 → 0 difference, so nothing is ever released: the button is stuck
until the process dies. (Resuming also *re-presses* it, because the first real ButtonState differs
from the zeroed mirror, giving a phantom click.)

Fix — prefix on all four methods, before the original body runs:

* `SurfaceDestroyed`, `OnConfigurationChanged`, `OnWindowFocusChanged(false)`: read `mouseButtons`,
  and for every set bit call `glfwAndroidPushMouseButton(i, 0, 0)` (i = 0..4, the same five buttons
  the diff loop uses). The original then zeroes the mirror exactly as before.
* `OnPause`: same releases, **plus** `mouseButtons = 0` written through reflection — that method
  never cleared the mirror, so leaving it set would make the release invisible to the diff loop and
  swallow the next press instead (a "stuck up" button, the mirror image of this bug).
* `OnWindowFocusChanged(true)` is untouched (the host only clears on focus loss).
* The touch engine is not re-cancelled: the originals already do it (`touch?.Cancel()`).

Log (file only): `window focus lost: released 1 held mouse button(s), mirror was 0x1`.

### 10.2 B — keys stayed pressed (WASD / Shift)

`OnKeyDown` (`MainActivity.cs:1565-1598`) and `OnKeyUp` (`:1605-1615`) call
`Native.glfwAndroidPushKey(num, e.ScanCode, action, mods)` with no bookkeeping at all: press 1 /
release 0, and nothing on the lifecycle side. A key held while the window loses focus therefore
stays down in the engine forever — the pawn keeps walking.

Fix: `OnKeyDown` prefix records `e.ScanCode → glfw key` (only for `RepeatCount == 0`, only when
`AndroidKeyMap.ToGlfw(keyCode) >= 0`, i.e. only when the original really pushes something);
`OnKeyUp` prefix removes the scan code. The same four lifecycle prefixes then push
`glfwAndroidPushKey(key, scancode, 0, 0)` for everything left in the set and clear it.

* `KEYCODE_BACK` (4) is deliberately **not** tracked: `AndroidKeyMap` maps 4 and 111 both to GLFW
  `Escape` (256), and `OnBackPressed` already pushes its own press+release pair, so a tracked Back
  would be released twice (two `GetKeyUp` edges for one press — menu opens and closes).
* The stored glfw key is the one the press used, so the release names the same key; the scan code
  alone would be enough for GLFW but matching the press exactly costs nothing.
* Reading the event goes through `__args` + reflection (`ScanCode`, `RepeatCount`), so the mod still
  has no `Mono.Android` compile-time reference (CI guard).

Log: `activity paused: released 2 held key(s)`.

### 10.3 C — `ACTION_UP` released every button

```csharp
if (action == ACTION_UP || action == ACTION_CANCEL) num = 0;      // MainActivity.cs:1021-1024
```

`num` **is** the button bitmask, not a pointer count: it is `(int)e.ButtonState`, the five bits
1/2/4/8/16 are handed to `glfwAndroidPushMouseButton` as buttons 0..4, and the same value is stored
back into `mouseButtons`. So forcing 0 means "the user let go of every button". On a device that
reports the still-held buttons in `ACTION_UP`'s ButtonState, that releases buttons nobody released;
the next `ACTION_MOVE` carries the real ButtonState, differs from the zeroed mirror and *presses
them again* — a phantom up/down pair (stray click, broken drag) plus a fresh `MouseDown` that
`DebugTool`/`HandleMapClicks` can act on.

Fix (`MouseEvent_Prefix`, private method, `ref bool __result`): only when

* `ActionMasked == ACTION_UP`,
* the event really is a mouse event (`(Source & 0x2002) == 8194`, the host's own test),
* `ButtonState != 0` (something is still held), and
* at least one button survives clearing `e.ActionButton` (the one that went up),

the prefix does the host's own work itself — `TouchCamera.UseMouse(true)`, `touch.Cancel()`, cursor
push, then the same diff loop against `remaining = ButtonState & ~ActionButton` — and returns
`false` so the original cannot force `num = 0`. Everything else (all MOVEs, all POINTER_DOWN/UP,
SCROLL, ACTION_CANCEL, and the normal single-button `ACTION_UP` whose ButtonState is already 0) runs
the untouched original, so the everyday path is bit-for-bit v7.

Log (rate limited to 1/s):
`action up kept other buttons down: mirror 0x3 -> 0x2 (released only the lifted button, ButtonState 0x3, ActionButton 0x1)`.

Risk: if a device reports a ButtonState that names a button the engine never received a press for,
that button is now kept "down" until the next MOVE reconciles it (the MOVE diff releases it, exactly
as the host would). The old behaviour in that same situation was to release a button that is really
still held, which is the worse failure.

### 10.4 D — `TRIM_MEMORY_UI_HIDDEN` (20) was the one level nobody answered

> **Removed in v9 (`1.7.0`, §11): this patch is gone.** No governor method is patched any more, and
> `Request(1)` / `RequestMemoryTrim` are not called from the mod. Kept below as history only.

`AndroidMemoryAndGcGovernor.OnTrimMemory` (`:46-68`) answers 15/80 with severity 2 and 10/5/60/40
with severity 1; level 20 (`TrimMemory.UiHidden`, "the activity left the screen") falls through both
branches and does nothing. `MainActivity.OnTrimMemory` (`:1096-1112`) filters identically.

Fix: `OnTrimMemory_Prefix` on the governor. For `level == 20` it calls
`AndroidMemoryAndGcGovernor.Request(1)` — the exact call the `Background` (40) branch makes, i.e. the
existing `pending` flag that `GameBoundary` consumes at the next game frame. That keeps one trim
channel with v7's throttles (5 s low / 30 s critical, one release per pressure event) instead of a
second, differently timed one. `TouchCamera.RequestMemoryTrim()` was considered and rejected: it is
a separate `TextureCacheStore`-only path with its own 30 s throttle in `GameCamera`, so it would give
UI_HIDDEN different semantics from Background.

Rate limited to one request per 5 s (the level can be redelivered while the process sits cached).
The original method still runs — it is a no-op for 20, and other levels are not this patch's
business. `MainActivity.OnTrimMemory` is not patched separately: the governor is registered as
`IComponentCallbacks2`, so it receives the same callback.

Log: `TRIM_MEMORY_UI_HIDDEN(20) treated as background: pending=1, engine textures release at the next game boundary`.

Note: if the game is already fully paused, `GameBoundary` will not run until resume, so the release
lands on the first frame back — same as a `Background` event arriving just before the pause.

### 10.5 Verification and build

* target shapes re-read from `dlls/RwHost.dll` metadata (`dnfile` 0.18.0) — every patch target
  exists exactly once with the arity used, no overload guessing:
  `MainActivity.SurfaceDestroyed(holder)`, `OnConfigurationChanged(configuration)` (single overload —
  the "two-parameter" variant mentioned in the task does not exist in this build),
  `OnWindowFocusChanged(hasFocus)`, `OnPause()`, `OnKeyDown(keyCode, e)`, `OnKeyUp(keyCode, e)`,
  private `MouseEvent(e)`, `AndroidMemoryAndGcGovernor.OnTrimMemory(level)`,
  `AndroidMemoryAndGcGovernor.Request(severity)` (public static), `Native.glfwAndroidPushMouseButton(button, action, mods)`,
  `Native.glfwAndroidPushKey(key, scancode, action, mods)`, `Native.glfwAndroidPushCursorPos(x, y)`,
  `AndroidKeyMap.ToGlfw(code)` (public static), `TouchCamera.UseMouse(value)`, `TouchControls.Cancel()`,
  and the fields `MainActivity.mouseButtons` (private int) / `MainActivity.touch` (private TouchControls).
  `AndroidKeyMap` is `internal`, so it is resolved at runtime, not by reference.
* the produced DLL contains `SurfaceDestroyed_Prefix`, `OnConfigurationChanged_Prefix`,
  `OnWindowFocusChanged_Prefix`, `OnPause_Prefix`, `OnKeyDown_Prefix`, `OnKeyUp_Prefix`,
  `MouseEvent_Prefix`, `OnTrimMemory_Prefix`, `ReleaseMouseButtons`, `ReleaseKeys`, and the literals
  `BUILD v8-inputrel`, `TRIM_MEMORY_UI_HIDDEN(20) treated as background`,
  `action up kept other buttons down`, `held mouse button(s)`, `held key(s)`.

  `RwHost` / `PortUI` / `Mono.Android` / `UnityEngine` reference was added by reading any of this
  through reflection, and the CI guard passes.
* CI run **43462609399** (`d57be1b`), `dotnet build -c Release`: success, 0 errors.
* `Assemblies/AgentTouchFix.dll` **67,072 bytes**, md5 `7fa21cfe253f426f00eff2a5b6ac4b11`
  (sha256 `42a2dec71ef89946ebda3021d576adfc5bbacfe7686338244f4e17fbd6fd9703`); v7 was 55,808 B.
* `AgentTouchFix.csproj` now sets `<IncludeSourceRevisionInInformationalVersion>false</...>`: the
  .NET 8+ SDK appends `+<git sha>` to `AssemblyInformationalVersion`, and the v8 docs commit alone
  produced a different 67,072-byte DLL (`a4122739…` → `a23f548b…`). With the sha pinned the bytes
  depend on the source only, so the hashes below stay valid across documentation commits.
* artifact `AgentTouchFix.zip` **30,594 bytes**, md5 `5f299d155de47d5eb14d8bc5ce9df685`, staged at
  `~/.hermes/cache/scratch/agenttouchfix/v8/AgentTouchFix.zip`, unzipped next to it in
  `v8/AgentTouchFix/` (from run **43463836056**, `7b0bb08`). The zip md5 changes per run because the
  entry mtimes do; the DLL inside is byte-stable.
* **not deployed and not run in the game** (as instructed). First gameplay check: hold the left
  button (or a mouse-look drag) and switch app / rotate / show the keyboard — the drag must stop and
  the button must be released; hold W and background the app — the pawn must stop; with a mouse
  drag that ends while a second button is held, the second button must stay down and not re-click;
  and going to the home screen must produce the `TRIM_MEMORY_UI_HIDDEN(20)` log line (once per 5 s).

> The v8 checklist above still asks for the `TRIM_MEMORY_UI_HIDDEN(20)` line — v9 removed that
> patch (see §11.1), so that item no longer applies.

## 11. v9-notex build (version 1.7.0) — no memory / texture patch, tooltips parked

The v7/v8 memory work is gone for good, and the sticky tooltip is fixed where it actually starts:
at the virtual cursor.

### 11.1 Removed: every governor / texture patch

`mod/Source/GovernorPatches.cs` was **deleted** and with it all three install points:

| patch | status in v9 |
| --- | --- |
| `AndroidMemoryAndGcGovernor.GameBoundary` (v7 A, remember the release timestamp) | gone |
| `AndroidMemoryAndGcGovernor.RenderBoundary` (v7 A, skip the second texture release) | gone |
| `AndroidMemoryAndGcGovernor.OnTrimMemory` (v8 D, answer UI_HIDDEN with `Request(1)`) | gone |

v9 patches **no** `AndroidMemoryAndGcGovernor` method and calls no texture entry point
(`RequestMemoryTrim`, `PushTexture`, `TextureCacheStore`). Reason: the grey / flickering / scrambled
textures were caused by the port itself — its mobile profile drops to `Economy`, the decoded-texture
budget is 32 MiB and BC sampling goes through a software decode path — and intercepting the memory
governor on such a machine turns "a flash" into "the whole screen is wrong".

CI now enforces this in `Verify no governor / texture patch (v9)`:
`mod/Source/GovernorPatches.cs` must not exist, `grep GovernorPatches mod/Source` must find nothing,
and the produced DLL must not contain `AndroidMemoryAndGcGovernor`, `GameBoundary`, `RenderBoundary`,
`OnTrimMemory`, `RequestMemoryTrim`, `PushTexture`, `TextureCacheStore`, `renderTrim` or
`GovernorPatches` in **either** string heap (a .NET assembly keeps its literals in the `#US` heap as
UTF-16, so both `strings -a` and `strings -el` are checked). It must contain `agenttouchfix.tips`,
`tips mode=` and `BUILD v9-notex` — so the switch cannot silently disappear either.

### 11.2 Tooltips: the cursor is parked, not just the tip suppressed

v6 suppressed `TipRegion` while "the cursor had not moved for 300 ms", which was an inference about
state. The actual mechanism is simpler and was read out of the decompiled host:

```
TouchControls.Translate(c)            RwHost/TouchControls.cs:196
  case TouchSignal.Tap/DoubleTap/Context:
      Click(0, c.X, c.Y)              // Click() -> :269
  Click(): Native.glfwAndroidPushCursorPos(x, y); push press; push release;
```

Nothing ever moves the cursor away from the tap point, so `Event.current.mousePosition` stays on the
button/cell, `Mouse.IsOver(rect)` is true forever, the tip is re-registered on every Repaint (and the
hover highlight stays lit). `ClassicTouchControls` has its own copy of the same `Click()`.

v9 therefore pushes the cursor **off screen** when the gesture is over:

* postfix on `RwHost.TouchControls.OnTouch` (modern chain) and on
  `RwHost.ClassicTouchControls.OnTouch` (classic chain);
* only for `ACTION_UP` with no other finger left (`PointerCount <= 1`) or `ACTION_CANCEL`;
* `RwHost.Native.glfwAndroidPushCursorPos(-1, -1)` through reflection (`double, double`; the argument
  is converted to the resolved parameter type), so the mod still carries no `RwHost` /
  `Mono.Android` reference;
* one push per frame (`Time.frameCount`, else `TickCount / 16`) — the classic chain is called from
  the modern one, so both postfixes see the same `ACTION_UP`;
* the native input queue is a 4096-entry ring (40-byte entries), so the park entry is queued *after*
  the press/release entries that carry the tap position — the tap itself is not affected.

The `TipRegion` suppression stays installed as the second line of defence.

### 11.3 The switch: `files/game/agenttouchfix.tips`

Read once at startup, same folder as `agenttouchfix.log` / `.diag` / `.snap`:

| value | behaviour |
| --- | --- |
| `park` **default** (also when the file is absent) | park the cursor on release **and** keep the `TipRegion` suppression |
| `suppress` | only the `TipRegion` suppression, the cursor is never moved |
| `off` | neither; the patches stay installed and return immediately (no `Event.current.Use()`, no GUI change) |

Decision timing — from the `OnTouch` timestamps and the `ACTION_DOWN` time only, no cursor-dwell
inference any more:

| state | result |
| --- | --- |
| no touch event for 2 s | allowed (desktop / real mouse) |
| finger down < 500 ms | suppressed |
| finger down ≥ 500 ms | allowed — the user asked for the description |
| released after such a hold, within 800 ms | allowed (no blinking) |
| within 2 s of the last up / cancel | suppressed |
| afterwards, while `Input.mousePosition` has not moved since the previous sample | suppressed (still parked) |

Log (file only, at most one line per 10 s):
`tips mode=park suppressed=12 allowed=hold/real-mouse hold=3 real-mouse=40 idle=2 parked=5`, plus
`tips mode=park parked=N (cursor pushed to (-1,-1) …)` from the park path. The startup line
`tips mode=<mode> (… )` records which switch value was read.

Known risk of `park`: if this port's `glfwAndroidPushCursorPos` clamped a negative coordinate to
`(0,0)` instead of taking it off screen, the tip would return to the top-left corner widget; the
suppression would still be in place, and `agenttouchfix.tips = suppress` restores v6-style
behaviour without touching the cursor.

### 11.4 Everything else is v8, unchanged

Tap snapping (`agenttouchfix.snap`), building double-tap select-all, IME text recovery
(`TextEditMailbox.TryTake`), mouse/key release on focus loss / pause / surface destroy,
`ACTION_UP` releasing only the lifted button, `SecondUp` state reset, file-only logging with 1 MB
rotation. Only the tooltip path and the removed governor patches differ.

Harmony targets installed at startup (17 `Add(...)` entries, each logged as
`patched <label> -> <assembly>!<type>.<method>`), plus 6 `TipRegion` / `TipRegionByKey` overloads:

```
RimWorld.Selector.HandleMapClicks            prefix
PortUI.MobileSelectionController.Tap         prefix
Verse.UI.MouseCell                           prefix (priority 700)
Verse.UI.MouseMapPosition                    prefix (priority 700, generic over Vector3)
PortUI.QuickOrderWheel.Open                  prefix
LudeonTK.DebugTool.DebugToolOnGUI            prefix
Ports1de.TouchGestureEngine.SecondUp         postfix        (RwHost copy, pinned)
RwHost.TouchControls.OnTouch                 prefix+postfix
RwHost.ClassicTouchControls.OnTouch          prefix+postfix (v9: postfix parks the cursor)
RwHost.MainActivity.SurfaceDestroyed         prefix
RwHost.MainActivity.OnConfigurationChanged   prefix
RwHost.MainActivity.OnWindowFocusChanged     prefix
RwHost.MainActivity.OnPause                  prefix
RwHost.MainActivity.OnKeyDown                prefix
RwHost.MainActivity.OnKeyUp                  prefix
RwHost.MainActivity.MouseEvent               prefix
PortUI.Ports1de.TextEditMailbox.TryTake      postfix
Verse.TooltipHandler.TipRegion        x2     prefix (parameterless)
Verse.TooltipHandler.TipRegionByKey   x4     prefix (parameterless)
```

The diagnostics in `DiagPatches.cs` (`RwHost.GameCamera.Add`, `RwHost.TouchCamera.Add`,
both `OnTouch` methods) are only installed when `files/game/agenttouchfix.diag` exists; with the flag
absent `Diag.Install()` returns before patching anything.

### 11.5 Verification and build

* target shapes re-read from `dlls/RwHost.dll` / `dlls/Assembly-CSharp.dll` metadata (`dnfile` 0.18.0):
  `RwHost.Native.glfwAndroidPushCursorPos` (2 parameters, matching the decompiled
  `(double x, double y)`), `RwHost.TouchControls.OnTouch` (1), `RwHost.ClassicTouchControls.OnTouch`
  (1), and exactly 6 `TipRegion`/`TipRegionByKey` overloads (2 + 4) on `Verse.TooltipHandler`.
* the produced DLL contains no `AndroidMemoryAndGcGovernor` / `GameBoundary` / `RenderBoundary` /
  `OnTrimMemory` / `RequestMemoryTrim` / `PushTexture` / `TextureCacheStore` / `renderTrim` /
  `GovernorPatches` in either string heap (0 occurrences, checked independently of CI with a Python
  scanner over the ASCII and UTF-16LE runs), and does contain `BUILD v9-notex`, `tips mode=`,
  `agenttouchfix.tips` and `glfwAndroidPushCursorPos`.
* assembly references (metadata read with `dnfile`): `System.Runtime`, `0Harmony`, `Assembly-CSharp`,
  `System.Collections`, `System.Threading` — no `RwHost` / `PortUI` / `Mono.Android` / `UnityEngine` /
  `Waylith` reference (the last two `System.*` facades come from the v8 key dictionary and the
  `Interlocked`-free bookkeeping, and are part of the runtime the game already loads).
* CI run **43467145188** (`4f43b85`), `dotnet build -c Release`: **0 warnings, 0 errors**, reference
  guard OK, `no governor / texture patch: OK`, `tips switch literals: OK`.
* `Assemblies/AgentTouchFix.dll` **68,096 bytes**, md5 `d48bef40573d96138a8c3c9bd35cf3de`
  (sha256 `c2de8138cde15e9091056c9d71a1b2ba5e9abd8f4bf396bd5405ba3ada7c5851`); v8 was 67,072 B.
* artifact `AgentTouchFix.zip` **30,800 bytes**, md5 `975d9bc83342fda85a47276f92738d72`, staged at
  `~/.hermes/cache/scratch/agenttouchfix/v9/AgentTouchFix.zip`, unzipped next to it in
  `v9/AgentTouchFix/` (zip content: `AgentTouchFix/About/About.xml` + `Assemblies/AgentTouchFix.dll`).
* **not deployed and not run in the game** (as instructed); `ModsConfig.xml` was not touched.
  First gameplay check with `tips` absent (= `park`): tap a button → no tooltip and no hover
  highlight left behind; tap and hold 600 ms → the description appears while the finger is down and
  disappears on release; move a real mouse → tooltips behave normally; capture a region with a dev
  tool → the park must not shift the click (if taps land wrong, set `agenttouchfix.tips` to
  `suppress` and re-test, that isolates the native push from everything else).

## 12. v10-texprobe build (version 1.8.0-diag) — one behaviour switch, read-only texture counters

Two changes only; every v9 fix is untouched.

### 12.1 Why the snap switch was *not* turned off

The first draft of this version planned to default `agenttouchfix.snap` to `off`. That is wrong:
snap is the **developer-tool** path (`ToolPatches.Fire` → `SnapGeometry.Resolve`), and without it an
armed tool cannot be aimed at an entity at a zoomed-out phone view. Default stays `auto`.

The "I tapped the ground and it grabbed the nearest item" report has a different source:
`PortUI.MobileSelectionController.Candidates` answers a tap with `Selector.SelectableObjectsAt(cell)`
and, when that cell holds no `Thing`, additionally scans `CellRect.CenteredOn(cell, 2)` and *inserts*
every `Thing` within 28.8 px of the tap **in front of** the cell's own contents, nearest first. So a
tap on empty ground never stays on empty ground.

### 12.2 New: `files/game/agenttouchfix.pick` (`port` default / `exact`)

`PickMode.Candidates_Prefix<T>` (generic, closed over `UnityEngine.Vector3` exactly like the
`MouseMapPosition` prefix — the mod still has no UnityEngine reference) is a prefix on `Candidates`:

* `exact` + the tapped cell's own selectables contain **no** `Thing` → return that (usually empty)
  list and skip the original: the 5×5 / 28.8 px radius search never runs. The cell is computed the
  same way the original computes it (`IntVec3(Vector3)` truncates, it does not round).
* every other case (mode `port`, cell holds something, unknown position, no map, out of bounds, any
  exception) → `return true`, the original runs untouched.

The prefix is installed in both modes so the log always names the live mode; in `port` it returns
immediately. Arguments are never rewritten, and the branch it does not own is never bypassed.

Log: `pick mode=exact skipped-radius=N` (at most one line per 2 s, N = session total) and the startup
line `pick mode=<mode> (...)`.

### 12.3 New: read-only texture counters (`TexProbe.cs`)

Feature-gated by nothing (they are counters on cold paths, not the diag flag). Each patch reads and
increments only — no `__result` is written, no argument is modified, and **no governor / memory
method is patched** (the v9 rule and its CI guard are unchanged).

| target | patch | recorded |
|---|---|---|
| `Ports1de.DdsFallback.TryPlan` | prefix (`int budget, int maxDimension`) | requested budget + dimension cap |
| `Ports1de.DdsFallback.TryPlan` | postfix (`bool __result, object plan`) | plan `Format/Width/Height/Shift/SourceMip` |
| `Ports1de.DdsFallback.TryConvert` | postfix (`object[] __args, bool __result`) | success, `used` bytes, in/out header |
| `Waylith.Rendering.Texture2D.Evict` | parameterless prefix | evictions |
| `Waylith.Rendering.GpuTextureService.GetOrCreateFallbackTexture` | parameterless prefix | 1×1 stand-in handed out |
| `Waylith.Rendering.TextureResidency.Offload` | parameterless prefix | offload passes |
| `Waylith.Rendering.TextureResidency.EvictHostResident` | parameterless prefix | host-resident evictions |

`object plan` is how the out parameter is read without naming `Ports1de.DdsFallback.Plan` (internal
to `RwHost.dll`); if that shape is ever rejected the postfix is skipped with a `WARN` and the mip
shift is derived from the input vs. converted pixel width instead, so the summary keeps working.

Every 30 s (`System.Threading.Timer`, root-held static field) one line into
`files/game/agenttouchfix.log` — file only, never `Verse.Log`:

```
tex: decode ok=N shift>0=N fail=N fallback=N evict=N offload=N budget=<MiB> limit=<px> hostevict=N plan=<ok>/<fail>
```

plus two one-off detail lines: the first down-shifted plan (`tex detail shift>0: plan format=… WxH
shift=… srcMip=… budget=… limit=…`) and the first failed decode (`tex detail decode-fail: format=…
WxH mips=… bytes=… budget=… limit=…`). `tex: probes ok=… skipped=… failed=… planpostfix=on|off`
records what could be installed.

### 12.4 Probe coverage in CI

`tests/HarmonyProbe` gained four shape probes, so none of the above is assumed:

| probe | proves |
|---|---|
| span/plan | a prefix+postfix can be installed on a `ReadOnlySpan<byte>` method; named `int` parameters bind; `object` receives an `out` reference-type parameter; `__result` is visible |
| args | a postfix may read every argument, including two `out` values, through `object[] __args` |
| pick | a generic prefix closed over the position struct can replace `List<object>` and skip the original, and reads the boxed position correctly |
| evict | a parameterless prefix on an instance method counts without disturbing the body |
