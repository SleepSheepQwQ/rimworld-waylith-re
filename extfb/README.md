# extfallback gate

The workflow in `.github/workflows/extfallback.yml` judges GlyphFix's **lookup-layer** texture
fallback (`mod/Source/ExtFallbackFix.cs` + `ExtFallbackDecision.cs` in
`SleepSheepQwQ/rimworld-waylith-icon-fix`) on the real host.

It answers three questions, in this order:

1. **Does the shadowed .png come back?** `Shadow.png` (128x128) and `Shadow.dds` (8x8 DXT3) share a
   name, so `ModContentLoader.ShadowedByDds` deletes the png and the undecodable dds owns the key.
   Control: `hit=False`. Patched: `hit=True`, `128x128`, `RGBA32`, and the *same* `meanRGB` / `lumSd`
   / `alpha0` as `PngOnly` - the same bytes with no dds beside them. An 8x8 answer is the dds and
   fails the gate.
2. **Is anything invented?** `Dxt3.dds` has no same-named image. It must stay `hit=False` with the
   fix installed, and the log must not contain a repair line for it.
3. **Did the gates that already existed move?** `Graphic_Collection` variants 10/5 -> 5/0, the
   animal's four directions 0/4 -> 4/4, and the three case mismatches (`IbexMale_east`,
   `LongSword`, `Bullet_big`) must all behave exactly as before. `Bc7.dds` must still load untouched.

The DDS **format** repair is switched off for both runs (the shipped `agenttouchfix.dds` switch
file): with it on, `Shadow.dds` would be decoded to an 8x8 texture and the png path would never be
exercised, and "Dxt3 stays a miss" could not be asserted at all. Its own workflow (`ddsfix.yml`)
owns that gate.

Fixtures are the bytes already in `ddsfix/`; nothing new is generated. The one line a release would
carry (`ExtFallbackFix.Install();` after `CaseFix.Install();`) is inserted at build time, so the
branch under test differs from the public repository by new files only.
