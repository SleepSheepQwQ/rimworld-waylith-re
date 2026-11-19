# flow — "def -> icon texture" as assertable functions

The build-menu icon of a thing is reached through four steps:

```
texture files on disk
  -> ContentFinder<Texture2D>.GetAllInFolder(req.path)      (x.name per file)
  -> Verse.Graphic_Collection.Init(req)                      (filter / group / build subGraphics)
  -> Verse.Graphic_Random.SubGraphicFor(thing)               (thingIDNumber % subGraphics.Length)
  -> GraphicUtility.ExtractInnerGraphicFor(g, thing)
       .MatAt(rot).mainTexture                                (Widgets.GetIconFor)
```

`flow/` runs that chain without the game, Unity, Harmony or a GPU, and prints a table that
can be asserted on. CI: `.github/workflows/iconflow.yml`.

```bash
dotnet run --project flow/Flow.csproj -c Release -- --dlls dlls --fixtures flow/fixtures
echo $?    # 0 pass / 1 assertion failed / 2 harness cannot vouch for its own replica
```

Options: `--table <path>` also writes the markdown table, `--force-fail` adds one deliberately
failing assertion (used for a control run that must go red), `--dump Type::Method` prints a
method body decoded by the harness itself.

## What is real, what is replicated — and why that is stated in the output

Two independent things are taken from `dlls/Assembly-CSharp.dll` (1.7.9795.32073) with
`System.Reflection.Metadata` only; no game code is executed:

1. **The constants.** `Graphic_Single.MaskSuffix` (`"_m"`, a static field read from its type
   initializer), the grouping character (`'ldc.i4.s 95'` before `String::Split`), the path
   separator (`ldstr "/"` feeding `String::Concat(string,string,string)`), and the four direction
   markers (`_north`, `_east`, `_south`, `_west` — the `ldstr` literals in `Init`). The replica
   is parameterised by these, so a modded dll with a different suffix changes the run, not just
   the comment.
2. **The shape gate.** `Facts.cs` requires `Init` to contain
   `GetAllInFolder -> Where(name.EndsWith(MaskSuffix)) -> OrderBy(name) -> Select((x, name.Split('_')[0])) -> ToList -> GroupBy`,
   one `ContentFinder.Get(path + MaskSuffix)` mask probe, `GraphicDatabase.Get` on both
   `SingleGraphicType` and `MultiGraphicType`, and the final `ToArray + stfld subGraphics`;
   `SubGraphicFor` to be `(OverrideGraphicIndex ?? thingIDNumber) % subGraphics.Length`;
   `GraphicUtility.ExtractInnerGraphicFor` to dispatch `Graphic_Random -> SubGraphicFor`; and
   `Widgets.GetIconFor(Thing,...)` to end in `ExtractInnerGraphicFor(...).MatAt(rot).mainTexture`.
   Compiler-generated lambdas/local functions (`<Init>b__6_*`, `g__ResolveGraphicInner`) are
   followed, because that is where `Split('_')` and the `isinst Graphic_Random` actually live.
   Any drift prints `[BROKEN]` and exits 2 — a broken harness never reports green.

`Verse.Graphic_Collection.Init` itself is **replicated line by line** in `Replica.cs`, with the
IL offsets of the shipped method next to each block (the task allowed either calling the real
method or a line-by-line replica; this is the replica, and every assumption it makes is gated by
the two items above). `flowb/` in this repo is the independent implementation that runs the real
method instead — the two are meant to agree.

## What is stubbed

Only the content seam, because that is where the bug lives: `FolderContentFinder` reads a real
directory and exposes one `Texture2D.name` per file, under one of two contracts:

| contract | `x.name` for `UniqueRevolverA.png.dds` | what it models |
|---|---|---|
| `game-contract` | `UniqueRevolverA` | what `ContentFinder` is supposed to hand out (every texture extension peeled) |
| `port-leak(.png)` | `UniqueRevolverA.png` | what this port hands out for DLC art: `Path.GetFileNameWithoutExtension` strips only the outer `.dds`, so the inner `.png` survives |

Everything downstream of that name is the replica — the same code for both contracts. That is
the whole point: the harness does not branch on "correct", it branches on the input shape, so the
table shows what the pipeline *does* rather than what it is supposed to do.

## Fixtures

`fixtures/revolver/` — 10 files, the real port names: `UniqueRevolverA..E.png.dds` and their
`_m` masks. `UniqueRevolverA*` are the extracted originals; B..E are 128-byte DDS headers with the
same names (the chain only ever reads names, never pixels).

`fixtures/lynx/` — `Lynx_north.png.dds` and `Lynx_northm.png.dds`, the per-direction mask family
(`Graphic_Multi` asks for `path + "_north"` and its mask adds `m`).

## What the table says

```
| scenario | texture names  | files | after _m filter | groups | subGraphics | mask variants | mask hits / 1000 | mask ratio |
| revolver | game-contract  |    10 |               5 |      5 |           5 |             0 |                0 |      0.0% |
| revolver | port-leak(.png)|    10 |              10 |     10 |          10 |             5 |              500 |     50.0% |
| lynx     | game-contract  |     2 |               2 |      1 |           1 |             0 |                0 |      0.0% |
| lynx     | port-leak(.png)|     2 |               2 |      1 |           1 |             0 |                0 |      0.0% |
```

* `_m` filter works: 5 variants, every path distinct, no `thingIDNumber` in 0..999 ever lands on
  a mask.
* `x.name` keeps `.png`: the suffix test misses, `Split('_')[0]` splits `…A` from `…A_m` into two
  groups, and the masks become variants — 10 entries, sorted so textures and masks alternate, so
  exactly half of the ids draw a mask. Same replica, same files: **5/0% vs 10/50% is the
  discrimination test.**
* Directional names are immune to *this* leak (both `Lynx_north` and `Lynx_northm` contain
  `_north`, get culled, and collapse into one `Graphic_Multi`), which is why the animal case stays
  at one variant under both contracts — the direction branch of `Init` is exercised, and the
  extension leak is shown to be specific to the `_m` suffix test.
