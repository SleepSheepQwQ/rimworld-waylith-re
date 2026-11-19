#!/usr/bin/env bash
# Runs the headless host once and records the two facts the report needs: wall time and exit code.
#   ci/run.sh <logfile> [extra args...]
set -uo pipefail

LOG="$1"; shift
R="${RUN_DIR:-$PWD/run}"

cd "$R"
start=$(date +%s.%N)
timeout "${BOOT_TIMEOUT:-600}" ./RimWorld -Headless -SuppressUnityStubs -NoAssetPack "$@" > "$LOG" 2>&1
code=$?
end=$(date +%s.%N)

{
  echo "wall_seconds=$(awk "BEGIN{printf \"%.2f\", $end - $start}")"
  echo "exit_code=$code"
} >> "$LOG"

echo "--- $LOG: exit=$code wall=$(tail -2 "$LOG" | head -1)"
grep -aE "^\[SCENARIO\]" "$LOG" | head -20 || true
