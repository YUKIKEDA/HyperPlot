# SPIKE (row K0b, Issue #488): the measurement of 3D line simplification, CPU only.
# Usage (PowerShell, from the repository root, with C:\Program Files (x86)\Microsoft Visual Studio\Installer on PATH, no other
# build or measurement on the machine):
#   ./benchmarks/HyperPlot.Lines3D/run-k0b.ps1 -Out <folder outside the repository> [-Rounds 1]
# Order (what the decisions wait for comes first, .cursor/rules/bench.mdc):
#   1. verify on 1e6 vertices: the bound against brute force, for each data, segment size, and split distance (minutes).
#   2. run on 1e8 vertices, perp: for each data, the segment sizes 2^14, 2^16, 2^18 in a balanced order (14, 16, 18, 18, 16,
#      14) per round: build time with 8 workers, memory, and the orbit and zoom scenes under budgets 2^18, 2^20, 2^22.
#   3. run on 1e8 vertices, linf at 2^16, for each data, once: the split distance's effect on vertices and time.
#   4. run on 1e6 vertices for each segment size (quick figures).
# Takes about 1 to 1.5 hours with -Rounds 1. The machine's state is logged beside it (watch-machine.ps1). Every run's output
# is kept in -Out. The data is generated in memory from seed 1; nothing is written but the logs.
#
# Expected before measuring (worst case and practical range):
# - verify: 0 violations and 0 uncovered vertices in every scene; the largest error 0.5 to 1 px. The bound is loose by the
#   componentwise maximum (up to √3) and the Jacobian's bound over a node's box (about 1.3 at the frame's edge), so the
#   vertices are 1.5 to 4 times a per-view screen-space Douglas–Peucker's; linf no better than perp by more than 10 %.
# - build (1e8, 8 workers): 2 to 6 s; slower with the segment size by the mean depth (about 2 ln S: 20 at 2^14, 25 at 2^18).
#   The walk's DP trees are deeper than the helix's.
# - memory: 6 B of errors + 4 B of offsets a vertex = 83 % of float32 x, y, z (42 % of float64); 67 % (33 %) with 2-byte
#   offsets for segments up to 2^16; the segments' boxes under 0.2 %.
# - orbit, full range, 1 px with no budget: walk 1e6 to 5e6 vertices, helix 1e4 to 1e5 (the noise of 1e-4 is under a pixel);
#   time 20 to 100 ms for the walk, under 2 ms for the helix. Under the budget of 2^20: p95 5 to 20 ms, 2 or 3 passes for
#   the walk. The segment size changes the orbit's time by under 20 %: a smaller segment adds endpoints and roots, a larger
#   one deeper walks; 2^16 the expected best.
# - zooms: the vertices fall as the visible share shrinks, then rise once the noise or the walk's steps reach a pixel; with
#   x alone zoomed by 1024, the segments that cross the x range are walked until their nodes leave the box.
# - allocation per call: 0 B.
param(
    [Parameter(Mandatory = $true)] [string] $Out,
    [int] $Rounds = 1
)

$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force $Out | Out-Null
$publish = Join-Path $Out "publish"
dotnet publish benchmarks/HyperPlot.Lines3D -c Release -r win-x64 -o $publish
if ($LASTEXITCODE -ne 0) { throw "publish failed" }
$exe = Join-Path $publish "HyperPlot.Lines3D.exe"
$machineLog = Join-Path $Out "machine.log"
$watch = Start-Process pwsh -WindowStyle Hidden -PassThru -ArgumentList @(
    "-NoProfile", "-File", "benchmarks/HyperPlot.DataPath/watch-machine.ps1", "-Log", $machineLog, "-Scene", "HyperPlot.Lines3D"
)

function Invoke-Spike([string] $Name, [string[]] $Arguments) {
    $log = Join-Path $Out "$Name.txt"
    Write-Output "$(Get-Date -Format HH:mm:ss) $Name"
    & $exe @Arguments *> $log
    if ($LASTEXITCODE -ne 0) { Write-Warning "exit $LASTEXITCODE`: $log" }
}

try {
    foreach ($data in "walk", "helix") {
        foreach ($segment in 14, 16, 18) {
            foreach ($split in "perp", "linf") {
                Invoke-Spike "verify-$data-s$segment-$split" @("verify", "--data", $data, "--count", "1e6", "--segment", $segment, "--split", $split)
            }
        }
    }

    for ($round = 1; $round -le $Rounds; $round++) {
        foreach ($data in "walk", "helix") {
            $at = 0
            foreach ($segment in 14, 16, 18, 18, 16, 14) {
                $at++
                Invoke-Spike "r$round-$data-1e8-s$segment-perp-$at" @(
                    "run", "--data", $data, "--count", "1e8", "--segment", $segment, "--split", "perp",
                    "--csv", (Join-Path $Out "r$round-$data-1e8-s$segment-perp-$at.csv")
                )
            }
        }
    }

    foreach ($data in "walk", "helix") {
        Invoke-Spike "$data-1e8-s16-linf" @("run", "--data", $data, "--count", "1e8", "--segment", "16", "--split", "linf")
    }

    foreach ($data in "walk", "helix") {
        foreach ($segment in 14, 16, 18) {
            Invoke-Spike "$data-1e6-s$segment-perp" @("run", "--data", $data, "--count", "1e6", "--segment", $segment)
        }
    }
}
finally {
    New-Item -ItemType File -Force "$machineLog.stop" | Out-Null
    $watch.WaitForExit(30000) | Out-Null
}
