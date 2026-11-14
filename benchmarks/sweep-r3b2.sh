#!/bin/bash
# R3b2 sweep, second grill (Issue #472, Decision of 2026-11-14): does the shuffle window act by the number of points or by
# the screen's area, and does drawing only the blocks in view bring zoom 4 under the period.
# Usage: bash sweep-r3b2.sh <output file>. Clocks go to <output file without .txt>-clocks.csv, the machine's state to
# <output file without .txt>-machine.log. Ends with DONE. About 15 minutes.
# 6 DIP, opacity 0.6, uniform, "whole" script, 600 frames after 120 warm-up, 1,920 x 1,080; the scene runs at High priority
# and prints the cores the other processes used over its measured frames (PR #541).
# Three rounds; round 2 runs the scenes in reverse order, so that each scene runs early and late.
set -u
OUT=${1:?output file}
CLOCKS="${OUT%.txt}-clocks.csv"
MACHINE="${OUT%.txt}-machine.log"
ROOT=/d/home/Programs/CSharpProjects/HyperPlot
P=$ROOT/.dev/r3b2-probe
E=$P/benchmarks/HyperPlot.E2E/bin/Release/net10.0-windows10.0.17763.0/HyperPlot.E2E.exe
if tasklist | grep -qi "HyperPlot.E2E.exe"; then echo "a HyperPlot.E2E.exe is still running; stop it first" >&2; exit 1; fi
: > "$OUT"

# The build: a failed build leaves the last executable in place, which would measure the old code. So the step stops
# unless the build succeeded and the executable is newer than the step's start.
STAMP=$(mktemp)
sleep 1
if ! dotnet build "$P/benchmarks/HyperPlot.E2E" -c Release >> "$OUT" 2>&1; then echo "build failed" | tee -a "$OUT" >&2; rm -f "$STAMP"; exit 1; fi
if [ ! "$E" -nt "$STAMP" ]; then echo "the executable is not newer than the build step" | tee -a "$OUT" >&2; rm -f "$STAMP"; exit 1; fi
rm -f "$STAMP"
echo "built $(git -C "$P" rev-parse --short HEAD) $(date +%H:%M:%S)" >> "$OUT"

nvidia-smi --query-gpu=timestamp,clocks.gr,pstate --format=csv -lms 1000 > "$CLOCKS" &
SMI=$!
rm -f "$MACHINE.stop"
pwsh -NoProfile -File "$P/benchmarks/HyperPlot.DataPath/watch-machine.ps1" -Log "$(cygpath -w "$MACHINE")" -Seconds 5 -Scene HyperPlot.E2E &
WATCH=$!
trap 'kill $SMI 2>/dev/null; touch "$MACHINE.stop"; wait $WATCH 2>/dev/null' EXIT

# A scene: name|environment|count|categories. All at zoom 1 ("whole") unless named z4.
SCENES=()
# 1. Points or area: the same screen with 2.5e6, 1e7, and 4e7 points. If the window is a number of points, the best
# block stays at 4,096 (8 parts) and 512 (64 parts) at every count; if it is an area, it moves with the density (8 parts:
# about 1,024 at 2.5e6 and 16,384 at 4e7; 64 parts: 2,048 at 4e7).
for pair in 2500000:n2.5e6 10000000:n1e7 40000000:n4e7; do
  count=${pair%%:*}; tag=${pair#*:}
  limit=""; [ "$count" -gt 10000000 ] && limit="HP_PROBE_NO_RAW_LIMIT=1"
  for blk in 1024 4096 16384; do
    SCENES+=("$tag-p8-b$blk|HP_PROBE_BLOCK_POINTS=$blk $limit|$count|8")
  done
  for blk in 512 2048; do
    SCENES+=("$tag-p64-b$blk|HP_PROBE_BLOCK_POINTS=$blk $limit|$count|64")
  done
done
# 2. Zoom 4 (1.6e8 points, 1e7 in view), 8 parts at 4,096: whole pages against only the blocks in view.
SCENES+=("z4-p8-b4096-pages|HP_PROBE_BLOCK_POINTS=4096 HP_PROBE_ZOOM=4 HP_PROBE_NO_RAW_LIMIT=1|160000000|8")
SCENES+=("z4-p8-b4096-visible|HP_PROBE_BLOCK_POINTS=4096 HP_PROBE_ZOOM=4 HP_PROBE_NO_RAW_LIMIT=1 HP_PROBE_VISIBLE_BLOCKS=1|160000000|8")

# A run past 180 s has no result (the longest, zoom 4, took about 30 s): it is stopped and counted.
run() {
  local name env count cats
  IFS='|' read -r name env count cats <<< "$1"
  echo "=== $name $(date +%H:%M:%S)" >> "$OUT"
  # shellcheck disable=SC2086
  timeout -k 10 180 env $env "$E" points "$count" 6 0.6 uniform 600 "$cats" whole >> "$OUT" 2>&1 || echo "exit $? (run without a result)" >> "$OUT"
  taskkill //F //IM HyperPlot.E2E.exe > /dev/null 2>&1 || true
}

# SWEEP_ONLY=<regex> keeps the scenes whose name matches it at the start.
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
