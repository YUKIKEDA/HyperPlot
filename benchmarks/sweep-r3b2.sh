#!/bin/bash
# R3b2 measurements of the Decision O1-O4 (Issue #472, 2026-11-14). Usage: bash sweep-r3b2.sh <o1|o3|o4|clean> <output file>.
#   o1: D1 (block = 32,768 / parts) and D2 (only the blocks in view): the central clump and the uniform points with one
#       part (block 32,768), and R3f's 6 shapes in one color with blocks of 4,096 and 8,192 (O5); 1e7 points, 6 DIP,
#       opacity 0.6, "whole" script, 600 frames after 120, 1,920 x 1,080; each run also prints the largest bin of the
#       density tiles in view (max_bin). 3 rounds, round 2 in reverse order. About 15 minutes.
#   o3: a cold copy of 1e9 uniform points: frame times while the pages of a still view and a pan fill, then the same warm
#       (PointsFileScene). 3 runs. About 15 to 25 minutes, with the data's generation.
#   o4: 1e9 uniform and clustered points: resident cells, Record's time, and the cells' bytes per frame (PointsFileScene).
#       3 rounds, uniform and clustered alternated (round 2 clustered first). About 20 to 30 minutes, with the data.
#   clean: deletes the generated data.
# The data (8 GB a distribution: generate-points, seed 1, unbuffered) lives in $P/.dev/data; run "clean" after o3 and o4.
# The index's copy (about 13 GB at 1e9) is a temporary file the process deletes on close.
# Clocks go to <output without .txt>-clocks.csv and the machine's state to <output without .txt>-machine.log. Each scene
# runs at High priority and prints the cores the other processes used (machine:). Ends with a line DONE in the output.
set -u
MODE=${1:?o1, o3, o4, or clean}
OUT=${2:?output file}
CLOCKS="${OUT%.txt}-clocks.csv"
MACHINE="${OUT%.txt}-machine.log"
ROOT=/d/home/Programs/CSharpProjects/HyperPlot
P=$ROOT/.dev/r3b2-probe
E=$P/benchmarks/HyperPlot.E2E/bin/Release/net10.0-windows10.0.17763.0/HyperPlot.E2E.exe
G=$P/benchmarks/HyperPlot.DataPath/bin/Release/net10.0/HyperPlot.DataPath.exe
DATA=$P/.dev/data

if [ "$MODE" = clean ]; then
  rm -f "${DATA:?}"/u1e9.x "${DATA:?}"/u1e9.y "${DATA:?}"/c1e9.x "${DATA:?}"/c1e9.y
  rmdir "${DATA:?}" 2>/dev/null
  echo "cleaned $(date +%H:%M:%S)" >> "$OUT"; echo DONE >> "$OUT"; exit 0
fi

if tasklist | grep -qiE "HyperPlot\.(E2E|DataPath)\.exe"; then echo "a HyperPlot.E2E.exe or DataPath.exe is still running; stop it first" >&2; exit 1; fi
: > "$OUT"

# The builds: a failed build leaves the last executable in place, which would measure the old code. So the step stops
# unless the build succeeded and the executable is newer than the step's start.
build() {
  local project=$1 exe=$2 stamp
  stamp=$(mktemp)
  sleep 1
  if ! dotnet build "$P/benchmarks/$project" -c Release >> "$OUT" 2>&1; then echo "build of $project failed" | tee -a "$OUT" >&2; rm -f "$stamp"; echo DONE >> "$OUT"; exit 1; fi
  # A build that was up to date leaves the executable older than the stamp, so the check is against the sources: no source
  # file may be newer than the executable.
  local stale
  stale=$(find "$P/src" "$P/benchmarks" \( -name '*.cs' -o -name '*.hlsl' -o -name '*.csproj' \) -not -path '*/obj/*' -not -path '*/bin/*' -newer "$exe" | head -3)
  if [ ! -f "$exe" ] || [ -n "$stale" ]; then echo "$exe is older than: $stale" | tee -a "$OUT" >&2; rm -f "$stamp"; echo DONE >> "$OUT"; exit 1; fi
  rm -f "$stamp"
}
build HyperPlot.E2E "$E"
[ "$MODE" != o1 ] && build HyperPlot.DataPath "$G"
echo "built $(git -C "$P" rev-parse --short HEAD) $(date +%H:%M:%S)" >> "$OUT"

nvidia-smi --query-gpu=timestamp,clocks.gr,pstate --format=csv -lms 1000 > "$CLOCKS" &
SMI=$!
rm -f "$MACHINE.stop"
pwsh -NoProfile -File "$P/benchmarks/HyperPlot.DataPath/watch-machine.ps1" -Log "$(cygpath -w "$MACHINE")" -Seconds 5 -Scene HyperPlot.E2E &
WATCH=$!
trap 'kill $SMI 2>/dev/null; touch "$MACHINE.stop"; wait $WATCH 2>/dev/null' EXIT

# A run: name|environment|timeout in s|arguments. A run past its timeout has no result: it is stopped and counted.
run() {
  local name env limit rest
  IFS='|' read -r name env limit rest <<< "$1"
  echo "=== $name $(date +%H:%M:%S)" >> "$OUT"
  # shellcheck disable=SC2086
  timeout -k 10 "$limit" env $env "$E" $rest >> "$OUT" 2>&1 || echo "exit $? (run without a result)" >> "$OUT"
  taskkill //F //IM HyperPlot.E2E.exe > /dev/null 2>&1 || true
}

# The data of a distribution, generated when missing (seed 1, written unbuffered, so cold after the write).
data() {
  local file=$1 dist=$2
  mkdir -p "$DATA"
  if [ ! -f "$DATA/$file.y" ]; then
    echo "--- generate $file $dist $(date +%H:%M:%S)" >> "$OUT"
    if ! timeout -k 10 1800 "$G" generate-points --file "$(cygpath -w "$DATA/$file")" --distribution "$dist" --points 1000000000 --seed 1 >> "$OUT" 2>&1; then
      echo "generation of $file failed" | tee -a "$OUT" >&2; echo DONE >> "$OUT"; exit 1
    fi
    echo "--- generated $(date +%H:%M:%S)" >> "$OUT"
  fi
}

SCENES=()
ROUNDS=3
D2="HP_PROBE_VISIBLE_BLOCKS=1"
case "$MODE" in
  o1)
    for dist in cluster uniform; do
      SCENES+=("$dist-p1-b32768|HP_PROBE_BLOCK_POINTS=32768 $D2|180|points 10000000 6 0.6 $dist 600 0 whole")
      for blk in 4096 8192; do
        SCENES+=("$dist-shape6-b$blk|HP_PROBE_BLOCK_POINTS=$blk $D2|180|points 10000000 6 0.6 $dist 600 6 whole shape")
      done
    done
    ;;
  o3)
    data u1e9 uniform
    for r in 1 2 3; do
      SCENES+=("o3-uniform-run$r|HP_PROBE_BLOCK_POINTS=32768 $D2 HP_PROBE_NO_RAW_LIMIT=1|900|points-file $(cygpath -w "$DATA/u1e9") o3")
    done
    ROUNDS=1
    ;;
  o4)
    data u1e9 uniform
    data c1e9 clustered
    SCENES+=("o4-uniform|HP_PROBE_BLOCK_POINTS=32768 $D2 HP_PROBE_NO_RAW_LIMIT=1|900|points-file $(cygpath -w "$DATA/u1e9") o4")
    SCENES+=("o4-clustered|HP_PROBE_BLOCK_POINTS=32768 $D2 HP_PROBE_NO_RAW_LIMIT=1|900|points-file $(cygpath -w "$DATA/c1e9") o4")
    ;;
  *) echo "unknown mode $MODE" >&2; exit 2 ;;
esac

# SWEEP_ONLY=<regex> keeps the scenes whose name matches it at the start; SWEEP_ROUNDS=<n> sets the rounds.
if [ -n "${SWEEP_ONLY:-}" ]; then KEPT=(); for s in "${SCENES[@]}"; do [[ $s =~ ^($SWEEP_ONLY) ]] && KEPT+=("$s"); done; SCENES=("${KEPT[@]}"); fi
ROUNDS=${SWEEP_ROUNDS:-$ROUNDS}
N=${#SCENES[@]}
for ((r = 1; r <= ROUNDS; r++)); do
  echo "##### round $r $(date +%H:%M:%S)" >> "$OUT"
  if [ "$r" = 2 ]; then
    for ((i = N - 1; i >= 0; i--)); do run "${SCENES[$i]}"; done
  else
    for ((i = 0; i < N; i++)); do run "${SCENES[$i]}"; done
  fi
done
echo "DONE $(date +%H:%M:%S)" >> "$OUT"
echo DONE
