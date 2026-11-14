#!/usr/bin/env bash
# Runs the offscreen material-draw harness over the cases that matter and asserts the outcome.
# Software Vulkan (lavapipe) - no GPU, no phone.
set -u
BIN="${1:-build}"
H="$BIN/harness"
FRAG="$BIN/frag.spv"
VERT="$BIN/quad.spv"
A="vk/assets"

fail=0

run() { # name, main, w, h, mask, stx, sty, stz, stw
  local name="$1" main="$2" w="$3" h="$4" mask="$5"
  local out
  local maskarg="$A/$mask"
  [ "$mask" = "-" ] && maskarg="-"
  out=$("$H" "$FRAG" "$VERT" "$A/$main" "$w" "$h" "$maskarg" "$6" "$7" "$8" "$9" "$BIN/$name.ppm" 2>&1) || {
    echo "CASE $name: harness failed"; echo "$out"; fail=1; return
  }
  local drawn total
  drawn=$(echo "$out" | sed -n 's/.*RESULT drawn=\([0-9]*\) total=\([0-9]*\).*/\1/p')
  total=$(echo "$out" | sed -n 's/.*RESULT drawn=\([0-9]*\) total=\([0-9]*\).*/\2/p')
  echo "CASE $name st=($6,$7,$8,$9) mask=$mask -> drawn=$drawn/$total"
  echo "$name $drawn $total" >> "$BIN/results.txt"
}

: > "$BIN/results.txt"

# 1. the shader's own annotation default: (offset 0,0 / scale 1,1) - the icon must appear
run default_st       royalbed_south.rgba 128 128 "royalbed_south_mask.rgba" 0 0 1 1
# 2. a Unity-order _MainTex_ST (scale=1, offset=0) handed to this shader: uv pins to (1,1)
run unity_order_st   royalbed_south.rgba 128 128 "royalbed_south_mask.rgba" 1 1 0 0
# 3. the value the mod writes when the engine does not transform (flipV=false, scaleFirst=false)
run fix_no_transform royalbed_south.rgba 128 128 "royalbed_south_mask.rgba" 0 0 1 1
# 4. the value the mod writes when scaleFirst=true (reorder happens): (1,0,0,0) -> reordered (0,0,1,1)
run fix_scale_first  royalbed_south.rgba 128 128 "royalbed_south_mask.rgba" 1 0 0 0
# 5. no mask at all (engine substitutes a 1x1 black texture)
run default_no_mask  royalbed_south.rgba 128 128 "-"                     0 0 1 1
# 7. binding sanity: a fully transparent main must draw nothing
run clear_main       clear.rgba         128 128 "royalbed_south_mask.rgba" 0 0 1 1
# 6. the other reported icon
run nutrient_default nutrient_north.rgba 192 256 "-"                     0 0 1 1

expect() { # name, cmp, value
  local name="$1" cmp="$2" want="$3"
  local drawn
  drawn=$(awk -v n="$name" '$1==n{print $2}' "$BIN/results.txt")
  [ -n "$drawn" ] || { echo "ASSERT $name: missing result"; fail=1; return; }
  case "$cmp" in
    gt) if [ "$drawn" -gt "$want" ]; then echo "PASS $name drawn=$drawn > $want"; else echo "FAIL $name drawn=$drawn <= $want"; fail=1; fi ;;
    eq) if [ "$drawn" -eq "$want" ]; then echo "PASS $name drawn=$drawn == $want"; else echo "FAIL $name drawn=$drawn != $want"; fail=1; fi ;;
  esac
}

# the identity must paint the tile, the Unity-order value must paint nothing at all
expect default_st        gt 3000
expect fix_no_transform  gt 3000
expect fix_scale_first   gt 3000
expect default_no_mask   gt 3000
expect nutrient_default  gt 8000
expect clear_main        eq 0
expect unity_order_st    eq 0

echo
cat "$BIN/results.txt"
exit $fail
