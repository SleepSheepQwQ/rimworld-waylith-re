# host — static assertions over the real binaries

Reads `dlls/*.dll` with `System.Reflection.Metadata` only. No game runtime, no Unity, no GPU,
no project references — so it runs on any CI box.

```bash
dotnet run --project host/Host.csproj -c Release            # finds ../dlls by walking up
dotnet run --project host/Host.csproj -c Release -- dlls    # explicit dir
echo $?                                                     # 0 pass / 1 assertion failed / 2 harness could not run
```

`--force-fail` flips assert 1 to `FAIL` on purpose; the workflow's `demo_fail` dispatch input uses it to
show that the exit code (not the log text) is what turns the job red.

## The assertions

| # | kind | what it checks |
|---|------|----------------|
| 1 | hard | `Verse.Graphic.DrawMeshInt` (Assembly-CSharp) may only reach `UnityEngine.Graphics::DrawMesh`. Any other draw path (`DrawMeshNow`, `Internal_DrawMesh`, `DrawMeshCore`, `MeshDrawSubmission::Enqueue`, …) or a missing draw call → `FAIL`. |
| 2 | info | Walks the `UnityEngine.GUI::DrawTextureWithTexCoords` call chain (depth 3) and reports every read of the GUI event phase (`Event.current` / `Event.current.type`, `CheckOnGUI`) with the enum value it is compared against. Never fails the run. |
| 3 | hard (list) | Enumerates every method in every module that references `Waylith.Rendering.MeshDrawSubmission::Enqueue` (calls, `ldftn`/`ldvirtftn`), with call-site counts. Empty list → `FAIL` (the queue would be dead code). |

A mutation self-test runs first: the assert-1 rule is fed a mutated call graph
(`DrawMeshNow`, `Internal_DrawMesh`, `Enqueue`, empty) and must reject each one. If it does not,
the harness is `BROKEN` and the run fails — a green run cannot be green by accident.

Every conclusion is printed as `[PASS] / [FAIL] / [INFO] / [FINDING] / [HIT]`, then a one-line
`[SUMMARY]` and a final `RESULT: PASS|FAIL`. The workflow also writes those lines to the job
summary and uploads `host-assertions.log`.

## Adding an assertion

1. Add a `Check...` method in `Gate.cs`, return the number of failures (0 = pass).
2. Call it from `RunAll` and add its status to the `[SUMMARY]` line. Failures must flow into the
   returned exit code; info-only findings must not.
3. Use `Meta.Methods(module, "Namespace.Type", "Method")` to find targets, `Meta.TryBody` +
   `Il.Decode` to walk IL, `module.Describe(token)` to name every call target.
4. If the rule is a pure decision function (like `DrawVerdict`), add a case to `SelfTest`.
