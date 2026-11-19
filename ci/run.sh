#!/usr/bin/env bash
# Runs the headless host once and records the two facts the report needs: wall time and exit code.
#   ci/run.sh <logfile> [extra args...]
set -uo pipefail

LOG="$1"; shift
R="${RUN_DIR:-$PWD/run}"

# The console formatter colours its level column when it thinks it has a terminal; the report parses
# these lines, so ask for plain text and drop the per-line stack traces (they are what makes a
# full-texture-miss run 20 MB of log).
export NO_COLOR=1
export TERM=dumb

cd "$R"
start=$(date +%s.%N)
timeout "${BOOT_TIMEOUT:-600}" ./RimWorld -Headless -SuppressUnityStubs -NoAssetPack -DisableLogStackTraces "$@" > "$LOG" 2>&1
code=$?
end=$(date +%s.%N)

{
  echo "wall_seconds=$(awk "BEGIN{printf \"%.2f\", $end - $start}")"
  echo "exit_code=$code"
} >> "$LOG"

echo "--- $LOG: exit=$code wall=$(tail -2 "$LOG" | head -1)"
grep -aE "^\[SCENARIO\]" "$LOG" | head -20 || true
