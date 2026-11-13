#!/bin/bash
# R3b2 shuffle-window sweep (Issue #472, next round): what sets the best block of points shuffled alone.
# Usage: bash sweep-r3b2.sh <output file>. Clocks go to <output file without .txt>-clocks.csv. Ends with DONE.
# 1e7 points in view, 6 DIP, opacity 0.6, uniform, "whole" script, 600 frames after 120 warm-up, 1,920 x 1,080.
# Three rounds; round 2 runs the scenes in reverse order, so that each scene runs early and late.
set -u
OUT=${1:?output file}
CLOCKS="${OUT%.txt}-clocks.csv"
cd /d/home/Programs/CSharpProjects/HyperPlot || exit 1
E=.dev/r3b2-probe/benchmarks/HyperPlot.E2E/bin/Release/net10.0-windows10.0.17763.0/HyperPlot.E2E.exe
if tasklist | grep -qi "HyperPlot.E2E.exe"; then echo "a HyperPlot.E2E.exe is still running; stop it first" >&2; exit 1; fi
: > "$OUT"
nvidia-smi --query-gpu=timestamp,clocks.gr,pstate --format=csv -lms 1000 > "$CLOCKS" &
SMI=$!
trap 'kill $SMI 2>/dev/null' EXIT

# A scene: name|environment|count|categories. Parts 1 is no category (as V0 of the first round).
SCENES=()
for blk in 65536 32768 16384 8192 4096 2048; do
  for parts in 1 2 8 64; do
    cats=$parts; [ "$parts" = 1 ] && cats=0
    SCENES+=("z1-p$parts-b$blk|HP_PROBE_BLOCK_POINTS=$blk|10000000|$cats")
  done
done
# Same color for all 8 parts: around the best block of 8 parts seen so far (8,192 = H3c), and the page whole (H2a).
for blk in 65536 16384 8192 4096; do
  SCENES+=("one-p8-b$blk|HP_PROBE_BLOCK_POINTS=$blk HP_PROBE_COLOR=one|10000000|8")
done
# Zoom 4: the middle 1/4 of each axis of 1.6e8 points, so 1e7 points stay in view at the same screen density.
for blk in 65536 32768 16384 8192 4096 2048; do
  SCENES+=("z4-p8-b$blk|HP_PROBE_BLOCK_POINTS=$blk HP_PROBE_ZOOM=4 HP_PROBE_NO_RAW_LIMIT=1|160000000|8")
done
for blk in 65536 8192; do
  SCENES+=("z4-p1-b$blk|HP_PROBE_BLOCK_POINTS=$blk HP_PROBE_ZOOM=4 HP_PROBE_NO_RAW_LIMIT=1|160000000|0")
done

# 64 parts below 2,048 points a block: the window of 32,768 points is 512 points a block.
for blk in 512 1024; do
  SCENES+=("w64-p64-b$blk|HP_PROBE_BLOCK_POINTS=$blk|10000000|64")
done

run() {
  local name env count cats
  IFS='|' read -r name env count cats <<< "$1"
  echo "=== $name $(date +%H:%M:%S)" >> "$OUT"
  # shellcheck disable=SC2086
  timeout 900 env $env "$E" points "$count" 6 0.6 uniform 600 "$cats" whole >> "$OUT" 2>&1 || echo "exit $? (run without a result)" >> "$OUT"
}

# SWEEP_ONLY=<regex> keeps the scenes whose name matches it at the start (the second run: SWEEP_ONLY="z4|w64").
if [ -n "${SWEEP_ONLY:-}" ]; then KEPT=(); for s in "${SCENES[@]}"; do [[ $s =~ ^($SWEEP_ONLY) ]] && KEPT+=("$s"); done; SCENES=("${KEPT[@]}"); fi
N=${#SCENES[@]}
for r in 1 2 3; do
  echo "##### round $r $(date +%H:%M:%S)" >> "$OUT"
  if [ "$r" = 2 ]; then
    for ((i = N - 1; i >= 0; i--)); do run "${SCENES[$i]}"; done
  else
    for ((i = 0; i < N; i++)); do run "${SCENES[$i]}"; done
  fi
done
echo "DONE $(date +%H:%M:%S)" >> "$OUT"
echo DONE
