#!/usr/bin/env bash
# One headless boot of the patch-conflict experiment.
#
#   run_case.sh <name> <PC_MODE> <dll-path|none>
#
# Stages (or removes) GlyphFix in run/Mods/glyphfix, rewrites the mod list, boots the real host with
# the probe as a startup hook, and keeps three logs:
#   out/<name>.log        everything the process printed
#   out/<name>.txt        the [PATCHCONFLICT] lines the gate reads
#   out/<name>.modlog.txt GlyphFix's own [AgentTouchFix] install lines (stderr off-device)
set -uo pipefail

NAME="${1:?name}"
MODE="${2:?mode}"
DLL="${3:-none}"

ROOT="$PWD"
RUN="$ROOT/run"
OUT="$ROOT/out"
HOOK="${PATCHCONFLICT_HOOK:-$ROOT/patchconflict/bin/Release/net10.0/PatchConflict.dll}"
CFG="$HOME/.config/unity3d/Ludeon Studios/RimWorld by Ludeon Studios/Config"

mkdir -p "$OUT" "$RUN/Mods" "$CFG"
rm -rf "$RUN/Mods/glyphfix"

if [ "$DLL" != "none" ]; then
  mkdir -p "$RUN/Mods/glyphfix/Assemblies" "$RUN/Mods/glyphfix/About"
  cp "$ROOT/iconfix/mod/About/About.xml" "$RUN/Mods/glyphfix/About/"
  cp "$DLL" "$RUN/Mods/glyphfix/Assemblies/GlyphFix.dll"
  # Add the package to activeMods only (not to knownExpansions).
  python3 - "$ROOT/ci/ModsConfig.xml" "$CFG/ModsConfig.xml" <<'PY'
import sys
src, dst = sys.argv[1], sys.argv[2]
text = open(src, encoding="utf-8").read()
old = "    <li>ludeon.rimworld.odyssey</li>\n  </activeMods>"
new = "    <li>ludeon.rimworld.odyssey</li>\n    <li>sleepsheep.waylith.glyphfix</li>\n  </activeMods>"
if old not in text:
    sys.exit("ModsConfig.xml: activeMods block not in the expected shape")
open(dst, "w", encoding="utf-8").write(text.replace(old, new))
PY
else
  cp "$ROOT/ci/ModsConfig.xml" "$CFG/ModsConfig.xml"
fi

echo "=== $NAME mode=$MODE mod=$DLL ==="
echo "hook=$HOOK"
grep -c 'sleepsheep.waylith.glyphfix' "$CFG/ModsConfig.xml" || true

(
  cd "$RUN" || exit 90
  DOTNET_STARTUP_HOOKS="$HOOK" PC_MODE="$MODE" PROBE_TIMEOUT_MS=330000 \
    timeout 600 ./RimWorld -Headless -SuppressUnityStubs -DisableLogStackTraces
) > "$OUT/$NAME.log" 2>&1
RC=$?
echo "host exit=$RC"

grep -a '^\[PATCHCONFLICT\]' "$OUT/$NAME.log" > "$OUT/$NAME.txt" || true
grep -a 'iconfallback:\|patchreport: Widgets.GetIconFor\|\[AgentTouchFix\]' "$OUT/$NAME.log" > "$OUT/$NAME.modlog.txt" || true
echo "probe lines: $(wc -l < "$OUT/$NAME.txt")  mod lines: $(wc -l < "$OUT/$NAME.modlog.txt")"
exit 0
