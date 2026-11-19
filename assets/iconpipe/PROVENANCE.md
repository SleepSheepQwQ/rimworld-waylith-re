# iconpipe — the 12-asset DDS corpus

These are the only textures the pipeline needs. They are **real, unmodified** RimWorld texture
files, pulled from the DDS entries the Android port ships inside the game's `Assets.zip`
containers. Nothing here was re-encoded, resized, or generated.

## Source

| # | File | Source container | Entry inside the zip | Format |
|---|---|---|---|---|
| 1 | `od_UniqueRevolverA.dds` | `Data/Odyssey/Assets.zip` | `Textures/Things/Item/Equipment/WeaponRanged/UniqueWeapons/Revolver/UniqueRevolverA.png.dds` | BC7 (dxgi 98), 128×128, 8 mips |
| 2 | `od_UniqueRevolverA_m.dds` | `Data/Odyssey/Assets.zip` | `…/Revolver/UniqueRevolverA_m.png.dds` | BC4 (dxgi 80), 128×128, 8 mips |
| 3 | `core_Revolver.dds` | `Data/Core/Assets.zip` | `Resources/Textures/Things/Item/Equipment/WeaponRanged/Revolver.png.dds` | BC7, 128×128 |
| 4 | `core_AssaultRifle.dds` | `Data/Core/Assets.zip` | `Resources/Textures/Things/Item/Equipment/WeaponRanged/AssaultRifle.png.dds` | BC7, 128×128 |
| 5 | `core_Knife.dds` | `Data/Core/Assets.zip` | `Resources/Textures/Things/Item/Equipment/WeaponMelee/Knife.png.dds` | BC7, 128×128 |
| 6 | `core_Mace.dds` | `Data/Core/Assets.zip` | `Resources/Textures/Things/Item/Equipment/WeaponMelee/Mace.png.dds` | BC7, 128×128 |
| 7 | `core_Spear.dds` | `Data/Core/Assets.zip` | `Resources/Textures/Things/Item/Equipment/WeaponMelee/Spear.png.dds` | BC7, 128×128 |
| 8 | `core_FogOfWar.dds` | `Data/Core/Assets.zip` | `Resources/Textures/Other/FogOfWar.psd.dds` | BC4, 128×128 |
| 9 | `core_NorthEastCornerMask.dds` | `Data/Core/Assets.zip` | `Resources/Textures/Things/Mote/NorthEastCornerMask.psd.dds` | BC4, 128×128 |
| 10 | `core_PsycastDistortionMask.dds` | `Data/Core/Assets.zip` | `Resources/Textures/Things/Mote/PsycastDistortionMask.psd.dds` | BC4, 128×128 |
| 11 | `core_SpecularMetal.dds` | `Data/Core/Assets.zip` | `Resources/Textures/Other/SpecularMetal.psd.dds` | BC4, 64×64 |
| 12 | `core_OrnateDoor2x1_Mover_m.dds` | `Data/Core/Assets.zip` | `Resources/Textures/Things/Building/OrnateDoor/OrnateDoor2x1_Mover_m.png.dds` | BC4, 64×64 |

## Why these twelve

* **1 + 2** are the reported icon and its real `_m` mask: the pair the blank-icon chain is about.
  The albedo is the one that measures 12.2 % opaque coverage, the mask is the single-channel BC4
  texture that, bound as `_MainTex`, produces the opaque near-black square.
* **3–7** are five more BC7 item icons, so the BC7 decode path is exercised on more than one file
  (opaque coverage 92–100 %, sizes 128×128).
* **8–12** are five more BC4 single-channel textures, so the BC4 block decoder is exercised on
  both 128×128 and 64×64 blocks. One of them (`OrnateDoor2x1_Mover_m`) is a genuine `_m` mask.

Total: 6 BC7 albedo + 6 BC4 masks = 12 DDS, ~90 KB.

## Extraction

`Assets.zip` on this port stores each texture as a zstd-compressed zip entry (method 93) named
`….dds.split`. The extraction is byte-preserving:

```sh
zstd -dc <entry payload> > <name>.dds
```

The two Odyssey files were extracted from the Odyssey container; the ten Core files from the Core
container, using the repo's `m1work/zasset.py` (zip central directory + zstd) at the time this
corpus was cut. `textures.info` in the same container lists every entry with its width/height/mip
count and format id (25 = BC7, 26 = BC4), and the picks above match those rows.

Known quirk, unchanged: the Core albedo headers declare `mipMapCount = 1` while `textures.info`
says 8, and the payload carries a partial mip chain. Only mip 0 is used by `pipe/decode_dds.py`
(`level0_bytes` is validated against the file length), so this does not affect the pipeline.

## Licence

RimWorld and its textures are © Ludeon Studios. These files live in a **private** repository for
reference and regression testing only; they are not redistributed. Do not publish this directory.
