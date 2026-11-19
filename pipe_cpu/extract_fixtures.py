"""Pull a fixed sample of real main+mask texture pairs out of the device's Odyssey Assets.zip.

Run on the device (root read-only copy already in hand); CI never needs the 100 MB zip because the
few 128x128 pairs it uses are committed under fixtures/ with their hashes in PROVENANCE.txt.

    python3 pipe_cpu/extract_fixtures.py /path/to/Odyssey-Assets.zip
"""

import hashlib
import json
import os
import sys
import zipfile

# One per weapon family, chosen for spread: full-cartridge rifles, energy, exotic, melee.
CHOSEN = [
    "Textures/Things/Item/Equipment/WeaponRanged/UniqueWeapons/AssaultRifle/UniqueAssaultRifleA.png.dds",
    "Textures/Things/Item/Equipment/WeaponRanged/UniqueWeapons/BeamRepeater/UniqueBeamRepeaterB.png.dds",
    "Textures/Things/Item/Equipment/WeaponRanged/UniqueWeapons/BoltActionRifle/UniqueBoltActionRifleC.png.dds",
    "Textures/Things/Item/Equipment/WeaponRanged/UniqueWeapons/ChainShotgun/UniqueChainShotgunA.png.dds",
    # the two ends of the real mask-coverage range: 1.4 % and 19.2 % of the 128x128 face
    "Textures/Things/Item/Equipment/WeaponRanged/UniqueWeapons/ChainShotgun/UniqueChainShotgunB.png.dds",
    "Textures/Things/Item/Equipment/WeaponRanged/UniqueWeapons/ChargeLance/UniqueChargeLanceB.png.dds",
]

HERE = os.path.dirname(os.path.abspath(__file__))
FIX = os.path.join(HERE, "fixtures")


def main(zip_path):
    os.makedirs(FIX, exist_ok=True)
    z = zipfile.ZipFile(zip_path)
    names = set(z.namelist())
    manifest = []

    # textures.info is the engine's own per-texture declaration: `path w h mips fmtCode isSingle offset`
    info = {}
    if "textures.info" in names:
        for line in z.read("textures.info").decode().strip().split("\n"):
            p = line.split()
            if len(p) >= 6:
                info[p[0]] = {"w": int(p[1]), "h": int(p[2]), "mips": int(p[3]),
                              "fmt_code": int(p[4]), "single_channel": int(p[5])}

    picked = []
    for m in CHOSEN:
        if m in names and m[:-8] + "_m.png.dds" in names:
            picked.append((m, m[:-8] + "_m.png.dds"))
    if not picked:  # fall back to the first 6 real pairs
        mains = sorted(n for n in names if n.endswith(".png.dds") and "_m.png.dds" not in n)
        for m in mains:
            k = m[:-8] + "_m.png.dds"
            if k in names:
                picked.append((m, k))
            if len(picked) == 6:
                break

    for main_n, mask_n in picked:
        rec = {}
        for role, entry in (("main", main_n), ("mask", mask_n)):
            blob = z.read(entry)
            base = os.path.basename(entry).replace(".png.dds", "")
            dst = os.path.join(FIX, "%s%s.dds" % (base, "" if role == "main" else "_mask"))
            open(dst, "wb").write(blob)
            rec[role] = {
                "file": os.path.relpath(dst, HERE),
                "zip_entry": entry,
                "bytes": len(blob),
                "sha256": hashlib.sha256(blob).hexdigest(),
                "engine_info": info.get(entry),
            }
        rec["name"] = os.path.basename(main_n).replace(".png.dds", "")
        manifest.append(rec)

    with open(os.path.join(FIX, "PROVENANCE.json"), "w") as f:
        json.dump({"source_zip": os.path.basename(zip_path), "pairs": manifest}, f, indent=2)

    with open(os.path.join(FIX, "PROVENANCE.txt"), "w") as f:
        f.write("Real texture pairs copied verbatim (byte-identical) from the installed game:\n")
        f.write("  <device>/game/Data/Odyssey/Assets.zip  (zstd-compressed entries)\n\n")
        f.write("Only the two 128x128 DDS files per pair are kept, so CI needs no 100 MB zip.\n")
        f.write("Formats: main = BC7_UNORM (dxgi 98, engine format code 25),\n")
        f.write("         mask = BC4_UNORM (dxgi 80, engine format code 26, single_channel=1).\n\n")
        for r in manifest:
            f.write("%s\n" % r["name"])
            for role in ("main", "mask"):
                d = r[role]
                f.write("  %-4s %-46s %6d B  sha256=%s  info=%s\n"
                        % (role, os.path.basename(d["file"]), d["bytes"],
                           d["sha256"][:16], d["engine_info"]))
    print("wrote %d pairs to %s" % (len(manifest), FIX))
    for r in manifest:
        print(" ", r["name"], os.path.basename(r["main"]["file"]), os.path.basename(r["mask"]["file"]))


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "Odyssey-Assets.zip")
