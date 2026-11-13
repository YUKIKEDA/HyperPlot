# SPIKE (row K1, Issue #509): the full measurement of how 3D lines and markers composite with depth.
# Usage (PowerShell, from anywhere; the script finds the worktree's root):
#   ./benchmarks/HyperPlot.Depth3D/run-k1.ps1 -Out <folder outside the repository> [-Rounds 3] [-SkipTiming]
# Order (the figures the decision waits for first, .cursor/rules/bench.mdc):
#   1. ShaderGen "depth3d", then the NativeAOT Release publish.
#   2. compare: each method against the CPU reference (16 × 16 samples a pixel) at frames 0, 75, 150, 225 of the orbit, on
#      the hardware, with BMPs of the reference, each method, and its error for frame 75.
#      lines: knot and walk (1e6 segments, width 2 px), the surface before; knot with the surface after the lines.
#      markers: uniform with diameters 4 and 10 px, knot with 10 px, the surface before; uniform 10 px with the surface after.
#   3. determinism: every method, twice on WARP (the SHA-256 must agree between the two runs) and once on the hardware.
#   4. timing: the orbit (300 frames after 120 of warm-up, a frame every 16.7 ms) in rounds; inside a round each scene runs
#      every method in a balanced order (A..G, G..A), with the GPU's clock logged (nvidia-smi, 200 ms); then one run of
#      each method with a timestamp after every pass (--pass-times), to split the segment passes from the box passes.
# About 35 minutes on the reference machine (estimate: compare 8, determinism 5, timing 20); no other build or
# measurement can run meanwhile. Every run's output is kept in -Out; summary.txt collects the figures.
#
# Expected outcome, written before measuring (.cursor/rules/bench.mdc). The error of a pixel is the largest of
# |GPU − reference| over R, G, B in 0..1, over the pixels near a shape; "seam" is a 1-pixel line along an edge.
# Lines (one color a polyline; self-occlusion of one opaque polyline is invisible except where a method cuts it):
#   none      draw order instead of depth: where a later polyline lies behind an earlier one, or under the surface, its
#             color shows. Max ≈ 1.0 (contrast of the two colors), the most pixels over 0.25 of all methods.
#   test      right against the surface (per fragment); between polylines as none. Max ≈ 1.0.
#   prepass   between polylines right inside strokes; a seam where a polyline crosses in front of itself: the front strand's
#             edge pixels with coverage ≥ 0.5 hold its depth and hide the back strand there, so up to (1 − 0.5) of the
#             background shows. Max ≈ 0.5, many seams in the knot (it crosses itself everywhere).
#   postpass  no self seams; between polylines, the edge pixels (coverage in (0, 0.5)) of an earlier polyline in front are
#             painted over by a later one behind. Max ≈ 0.5, mean the lowest of the per-polyline methods. Two segment
#             passes a polyline.
#   capsules  joins blend twice where the stroke runs toward the camera (later segments nearer pass LESS), so edges grow
#             dark; every edge pixel writes depth, so a strand behind, drawn later, is cut along the front strand's edge
#             (seams up to 1 − coverage ≈ 0.9). Max ≈ 0.9, mean the highest after none and test. One pass for all.
#   uav       a pixel is drawn whole or not by the polyline's least depth: where a stroke goes through the surface, the
#             part behind it shows in the pixels where the part in front is nearest (a band of about 1 pixel at each
#             crossing, up to 1.0). Elsewhere as postpass.
#   uavfrag   as postpass up to the threshold being applied to the union's coverage instead of each segment's: expected
#             to differ from postpass in few pixels (by a few levels of 255). One segment pass a polyline.
#   GPU time (RTX 3060 Ti, 1e6 segments, width 2): a segment pass ≈ 1.5–3 ms (17 × 10^6 fragments for the knot, about
#   10^7 for the walk); none ≈ test ≈ capsules ≈ 2–3 ms; prepass ≈ postpass ≈ 2 × that, 4–6 ms; uav ≈ uavfrag ≈ 1.3–2 ×
#   a segment pass (an atomic for every covered fragment), 3–5 ms. The box passes (4 a polyline over its box) add
#   0.2–0.5 ms. Worst case: the atomics serialize where a polyline overlaps itself, uav up to 3 × none.
# Markers (1e6, diameter 4 or 10 px, data order random):
#   none      draw order instead of depth. Max 1.0, most pixels.
#   quad      square halos: the quad's corners (no coverage) write the center's depth and hide markers behind drawn later,
#             and with the surface after the markers they hide it (white corners on the surface). Max 1.0; over_0.5 the
#             largest of the depth methods, growing with the diameter.
#   touched   ring halos of the anti-aliased edge (coverage in (0, 1)): up to (1 − coverage) of the background or a
#             marker behind is lost. Max ≈ 0.9, in a 1-pixel ring.
#   half      the ring of coverage in [0.5, 1) still hides what is behind (≤ 0.5); the fringe (< 0.5) is blended over what
#             is behind without writing depth, so an earlier fringe can be overpainted by a later marker behind. Max ≈ 0.5.
#   GPU time: none and quad ≈ touched (early depth rejection), half ≈ 1.5–2 × touched (two passes). At 10 px with 1e6
#   uniform markers (about 10^8 fragments) several milliseconds; at 4 px about a quarter of that.
# Determinism: every method repeats byte for byte on WARP and on the hardware (ordered blending and depth, InterlockedMin);
# the two WARP runs give the same SHA-256.
param(
    [Parameter(Mandatory = $true)] [string] $Out,
    [int] $Rounds = 3,
    [switch] $SkipTiming
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "../..")
$env:PATH = "C:\Program Files (x86)\Microsoft Visual Studio\Installer;$env:PATH"
New-Item -ItemType Directory -Force $Out | Out-Null
$Out = Resolve-Path $Out

Push-Location $root
try {
    dotnet run --project tools/ShaderGen -- depth3d
    if ($LASTEXITCODE -ne 0) { throw "ShaderGen failed" }
    $publish = Join-Path $Out "publish"
    dotnet publish benchmarks/HyperPlot.Depth3D -c Release -r win-x64 -o $publish
    if ($LASTEXITCODE -ne 0) { throw "publish failed" }
}
finally {
    Pop-Location
}

$exe = Join-Path $publish "HyperPlot.Depth3D.exe"
$lineMethods = @("none", "test", "prepass", "postpass", "capsules", "uav", "uavfrag")
$markerMethods = @("none", "quad", "touched", "half")

function Invoke-Run([string] $log, [string[]] $arguments) {
    $started = Get-Date
    & $exe @arguments *> $log
    $code = $LASTEXITCODE
    Add-Content $log "# exit=$code seconds=$([math]::Round(((Get-Date) - $started).TotalSeconds, 1)) args=$($arguments -join ' ')"
    if ($code -ne 0) { Write-Warning "exit $code`: $log" }
}

# 2. compare
$dumps = Join-Path $Out "bmp"
$compares = @(
    @{ Name = "lines-knot"; Args = @("compare", "lines", "knot") },
    @{ Name = "lines-walk"; Args = @("compare", "lines", "walk") },
    @{ Name = "lines-knot-after"; Args = @("compare", "lines", "knot", "--plane", "after") },
    @{ Name = "markers-uniform-4"; Args = @("compare", "markers", "uniform", "--diameter", "4") },
    @{ Name = "markers-uniform-10"; Args = @("compare", "markers", "uniform", "--diameter", "10") },
    @{ Name = "markers-knot-10"; Args = @("compare", "markers", "knot", "--diameter", "10") },
    @{ Name = "markers-uniform-10-after"; Args = @("compare", "markers", "uniform", "--diameter", "10", "--plane", "after") }
)
foreach ($compare in $compares) {
    Invoke-Run (Join-Path $Out "compare-$($compare.Name).txt") $compare.Args
    Invoke-Run (Join-Path $Out "compare-$($compare.Name)-dump.txt") ($compare.Args + @("--at", "75", "--dump", $dumps))
}

# 3. determinism
foreach ($kind in @(@("lines", "knot"), @("lines", "walk"), @("markers", "uniform"), @("markers", "knot"))) {
    foreach ($run in 1, 2) {
        Invoke-Run (Join-Path $Out "det-warp-$($kind[0])-$($kind[1])-run$run.txt") @("determinism", $kind[0], $kind[1])
    }

    Invoke-Run (Join-Path $Out "det-hw-$($kind[0])-$($kind[1]).txt") @("determinism", $kind[0], $kind[1], "--hardware")
}

# 4. timing
if (-not $SkipTiming) {
    $smi = $null
    if (Get-Command nvidia-smi -ErrorAction SilentlyContinue) {
        $smi = Start-Process nvidia-smi -ArgumentList "--query-gpu=timestamp,clocks.gr,pstate --format=csv -lms 200" `
            -RedirectStandardOutput (Join-Path $Out "clocks.csv") -WindowStyle Hidden -PassThru
    }
    else {
        Write-Warning "nvidia-smi not found: the GPU's clock is not logged"
    }

    try {
        for ($round = 1; $round -le $Rounds; $round++) {
            foreach ($scene in "knot", "walk") {
                $order = $lineMethods + $lineMethods[($lineMethods.Count - 1)..0]
                for ($i = 0; $i -lt $order.Count; $i++) {
                    $method = $order[$i]
                    $name = "r$round-lines-$scene-$method-$i"
                    Invoke-Run (Join-Path $Out "$name.txt") @("run", "lines", $method, $scene, "--csv", (Join-Path $Out "$name.csv"))
                }
            }

            foreach ($scene in "uniform", "knot") {
                $order = $markerMethods + $markerMethods[($markerMethods.Count - 1)..0]
                for ($i = 0; $i -lt $order.Count; $i++) {
                    $method = $order[$i]
                    $name = "r$round-markers-$scene-$method-$i"
                    Invoke-Run (Join-Path $Out "$name.txt") @("run", "markers", $method, $scene, "--csv", (Join-Path $Out "$name.csv"))
                }
            }
        }

        foreach ($scene in "knot", "walk") {
            foreach ($method in $lineMethods) {
                Invoke-Run (Join-Path $Out "passes-lines-$scene-$method.txt") @("run", "lines", $method, $scene, "--pass-times")
            }
        }

        foreach ($method in $markerMethods) {
            Invoke-Run (Join-Path $Out "passes-markers-uniform-$method.txt") @("run", "markers", $method, "uniform", "--pass-times")
        }
    }
    finally {
        if ($smi) { Stop-Process -Id $smi.Id -ErrorAction SilentlyContinue }
    }
}

# The figures of every run, one file.
$summary = Join-Path $Out "summary.txt"
Get-ChildItem $Out -Filter *.txt | Where-Object { $_.Name -ne "summary.txt" } | Sort-Object Name | ForEach-Object {
    Add-Content $summary "== $($_.Name)"
    Get-Content $_.FullName | Where-Object { $_ -match "^(kind=|frame |gpu_shapes|gpu_total|gpu_segment|gpu_bounds|gpu_marker|cpu_frame|gpu frames|# exit|[a-z]+: repeat)" } |
        ForEach-Object { Add-Content $summary $_ }
}
Write-Host "Done: $summary"
