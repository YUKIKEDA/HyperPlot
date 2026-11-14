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
#
# The steps before the design (Issue #513's decision 1). Each switch runs only its step (after the build, unless
# -NoBuild); they can be combined and run in the order below. Every run has a deadline (-RunTimeoutSeconds) and its exit
# code is checked; the script ends with 1 when a run failed or timed out, after the others.
#   -Retime         postpass against none, held at P0: frames back to back with at most 2 queued (--back-to-back), so
#                   that the GPU never idles between frames, after a warm-up run of about 10 s; 1,200 frames after 600 of
#                   warm-up a run (about 10 s), knot and walk, in -RetimeRounds rounds of A, B, B, A (the side that runs
#                   first alternates by round). The clock is logged (nvidia-smi, 200 ms) and each run's P0 share and
#                   median clock are read inside its measured window. -LockClockMHz n also locks the graphics clock
#                   (nvidia-smi -lgc n,n, administrator only; -rgc after). Output <Out>\retime: each run's .txt and
#                   .csv, clocks.csv, retime-runs.csv, and the ratio postpass / none of the p50 in each round and scene.
#                   About 10 minutes with 6 rounds (48 runs).
#                   Expected, before measuring: every measured window at P0 (share 1.00); postpass about 1.3 to 2 times
#                   none (one segment pass more a polyline over 3 passes), the ratio inside ±10 % across rounds.
#   -Trace          the hardware compare of the three line scenes (knot, walk, knot with the surface after) at frames 0,
#                   75, 150, 225, with --trace: each pixel of postpass with an error above 0.5 and each polyline there,
#                   with postpass replayed on the CPU, and the reference, postpass, and its error map as BMPs (postpass
#                   only). Output <Out>\trace: each run's .txt, trace-<scene>.csv, bmp\, compare-summary.csv.
#                   About 2 minutes. Expected: the replay matches the GPU within 2 levels in nearly every traced pixel;
#                   the pixels are where an earlier polyline in front covers less than the threshold (so it wrote no
#                   depth) and later polylines behind paint over it, or several such edges compound (later_behind_painted).
#   -WarpTolerance  compare --warp (the device of design §10's tests) for every scene of the compare step at frames 0, 75,
#                   150, 225, the line scenes with --trace. Output <Out>\warp: each run's .txt, trace-<scene>.csv,
#                   compare-summary.csv (a row a frame and method: mean, p99, max, over_0.1/0.25/0.5) from which the
#                   tolerance is set. About 5 to 10 minutes.
param(
    [Parameter(Mandatory = $true)] [string] $Out,
    [int] $Rounds = 3,
    [switch] $SkipTiming,
    [switch] $Retime,
    [switch] $Trace,
    [switch] $WarpTolerance,
    [int] $RetimeRounds = 6,
    [int] $LockClockMHz = 0,
    [int] $RunTimeoutSeconds = 0,
    [switch] $NoBuild
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "../..")
$env:PATH = "C:\Program Files (x86)\Microsoft Visual Studio\Installer;$env:PATH"
New-Item -ItemType Directory -Force $Out | Out-Null
$Out = (Resolve-Path $Out).Path
$publish = Join-Path $Out "publish"

if (-not $NoBuild) {
    Push-Location $root
    try {
        dotnet run --project tools/ShaderGen -- depth3d
        if ($LASTEXITCODE -ne 0) { throw "ShaderGen failed" }
        dotnet publish benchmarks/HyperPlot.Depth3D -c Release -r win-x64 -o $publish
        if ($LASTEXITCODE -ne 0) { throw "publish failed" }
    }
    finally {
        Pop-Location
    }
}

$exe = Join-Path $publish "HyperPlot.Depth3D.exe"
if (-not (Test-Path $exe)) { throw "not published: $exe" }
$lineMethods = @("none", "test", "prepass", "postpass", "capsules", "uav", "uavfrag")
$markerMethods = @("none", "quad", "touched", "half")

# A run with a deadline: stdout to the log, stderr beside it; returns the exit code, or "timeout" after killing it.
function Invoke-Timed([string] $log, [string[]] $arguments, [int] $seconds) {
    $started = Get-Date
    # Start-Process joins the arguments with spaces; a path with a space is quoted.
    $quoted = $arguments | ForEach-Object { if ($_ -match "\s") { "`"$_`"" } else { $_ } }
    $process = Start-Process -FilePath $exe -ArgumentList $quoted -NoNewWindow -PassThru `
        -RedirectStandardOutput $log -RedirectStandardError ([IO.Path]::ChangeExtension($log, ".err.txt"))
    $null = $process.Handle # keeps the handle, so that ExitCode is read after the wait
    $code = if ($process.WaitForExit($seconds * 1000)) { $process.ExitCode } else {
        $process.Kill($true)
        $process.WaitForExit()
        "timeout"
    }
    Add-Content $log "# exit=$code seconds=$([math]::Round(((Get-Date) - $started).TotalSeconds, 1)) args=$($arguments -join ' ')"
    if ($code -ne 0) {
        $script:failed++
        Write-Warning "run failed ($code): $log"
    }

    return $code
}

# The rows "frame F method: mean=... over_0.5=... of N max_elsewhere=..." of compare logs, one object each.
function Read-Compare([string] $scene, [string] $log) {
    foreach ($line in Get-Content $log) {
        if ($line -match "^frame (\d+) (\w+): mean=([\d.]+) p99=([\d.]+) max=([\d.]+) over_0.1=(\d+) over_0.25=(\d+) over_0.5=(\d+) of (\d+) max_elsewhere=([\d.]+)") {
            [pscustomobject]@{
                scene = $scene; frame = [int]$Matches[1]; method = $Matches[2]; mean = [double]$Matches[3]
                p99 = [double]$Matches[4]; max = [double]$Matches[5]; over_01 = [int]$Matches[6]; over_025 = [int]$Matches[7]
                over_05 = [int]$Matches[8]; touched = [int]$Matches[9]; max_elsewhere = [double]$Matches[10]
            }
        }
    }
}

$failed = 0
$compareScenes = @(
    @{ Name = "lines-knot"; Lines = $true; Args = @("compare", "lines", "knot") },
    @{ Name = "lines-walk"; Lines = $true; Args = @("compare", "lines", "walk") },
    @{ Name = "lines-knot-after"; Lines = $true; Args = @("compare", "lines", "knot", "--plane", "after") },
    @{ Name = "markers-uniform-4"; Lines = $false; Args = @("compare", "markers", "uniform", "--diameter", "4") },
    @{ Name = "markers-uniform-10"; Lines = $false; Args = @("compare", "markers", "uniform", "--diameter", "10") },
    @{ Name = "markers-knot-10"; Lines = $false; Args = @("compare", "markers", "knot", "--diameter", "10") },
    @{ Name = "markers-uniform-10-after"; Lines = $false; Args = @("compare", "markers", "uniform", "--diameter", "10", "--plane", "after") }
)

if ($Retime -or $Trace -or $WarpTolerance) {
    if ($Retime) {
        $folder = Join-Path $Out "retime"
        New-Item -ItemType Directory -Force $folder | Out-Null
        $timeout = if ($RunTimeoutSeconds -gt 0) { $RunTimeoutSeconds } else { 120 }
        $clocks = Join-Path $folder "clocks.csv"
        if ($LockClockMHz -gt 0) {
            nvidia-smi -lgc "$LockClockMHz,$LockClockMHz"
            if ($LASTEXITCODE -ne 0) { throw "nvidia-smi -lgc failed (administrator only)" }
        }

        $smi = Start-Process nvidia-smi -ArgumentList "--query-gpu=timestamp,clocks.gr,pstate --format=csv -lms 200" `
            -RedirectStandardOutput $clocks -WindowStyle Hidden -PassThru
        $runs = @()
        try {
            # A warm-up load, not measured: the GPU reaches its clock and temperature before the first run.
            $null = Invoke-Timed (Join-Path $folder "warmup.txt") @("run", "lines", "postpass", "knot", "--back-to-back", "--frames", "2000", "--warmup", "0") $timeout
            for ($round = 1; $round -le $RetimeRounds; $round++) {
                $order = if ($round % 2 -eq 1) { @("none", "postpass", "postpass", "none") } else { @("postpass", "none", "none", "postpass") }
                foreach ($scene in "knot", "walk") {
                    for ($i = 0; $i -lt $order.Count; $i++) {
                        $method = $order[$i]
                        $name = "r$round-$scene-$method-$i"
                        [string[]] $arguments = @(
                            "run", "lines", $method, $scene, "--back-to-back", "--frames", "1200", "--warmup", "600",
                            "--csv", (Join-Path $folder "$name.csv")
                        )
                        $code = Invoke-Timed (Join-Path $folder "$name.txt") $arguments $timeout
                        $runs += [pscustomobject]@{ Round = $round; Scene = $scene; Method = $method; Slot = $i; Name = $name; Exit = "$code" }
                    }
                }
            }
        }
        finally {
            if ($smi) { Stop-Process -Id $smi.Id -ErrorAction SilentlyContinue }
            if ($LockClockMHz -gt 0) { nvidia-smi -rgc }
        }

        # The clock samples: "2026/11/14 03:20:01.123, 1665 MHz, P0".
        $samples = @(Get-Content $clocks | Where-Object { $_ -match "^\d{4}/\d\d/\d\d \d\d:\d\d:\d\d\.\d{3}," } | ForEach-Object {
                $parts = $_ -split ","
                if ($parts.Count -ge 3) {
                    [pscustomobject]@{
                        Time = [datetime]::ParseExact($parts[0].Trim(), "yyyy/MM/dd HH:mm:ss.fff", [Globalization.CultureInfo]::InvariantCulture)
                        Mhz = [int](($parts[1].Trim() -split " ")[0])
                        State = $parts[2].Trim()
                    }
                }
            })
        $rows = foreach ($run in $runs) {
            $text = Get-Content (Join-Path $folder "$($run.Name).txt")
            $window = $text | Where-Object { $_ -match "^measured frames ran (\S+ \S+) to (\S+ \S+)" } | Select-Object -First 1
            $total = $text | Where-Object { $_ -match "^gpu_total: " } | Select-Object -First 1
            $timed = $text | Where-Object { $_ -match "^gpu frames timed: " } | Select-Object -First 1
            $row = [ordered]@{
                round = $run.Round; scene = $run.Scene; method = $run.Method; slot = $run.Slot; exit = $run.Exit
                p50 = $null; p95 = $null; p99 = $null; timed = $timed; clock_samples = 0; p0_share = $null; clock_median_mhz = $null
            }
            if ($total -match "p50=([\d.]+) p95=([\d.]+) p99=([\d.]+)") {
                $row.p50 = [double]$Matches[1]
                $row.p95 = [double]$Matches[2]
                $row.p99 = [double]$Matches[3]
            }

            if ($window -match "^measured frames ran (\S+ \S+) to (\S+ \S+)") {
                $from = [datetime]::ParseExact($Matches[1], "yyyy-MM-dd HH:mm:ss.fff", [Globalization.CultureInfo]::InvariantCulture)
                $to = [datetime]::ParseExact($Matches[2], "yyyy-MM-dd HH:mm:ss.fff", [Globalization.CultureInfo]::InvariantCulture)
                $inside = @($samples | Where-Object { $_.Time -ge $from -and $_.Time -le $to })
                $row.clock_samples = $inside.Count
                if ($inside.Count -gt 0) {
                    $row.p0_share = [math]::Round(@($inside | Where-Object { $_.State -eq "P0" }).Count / $inside.Count, 3)
                    $sorted = @($inside | ForEach-Object { $_.Mhz } | Sort-Object)
                    $row.clock_median_mhz = $sorted[[int][math]::Floor($sorted.Count / 2)]
                }
            }

            [pscustomobject]$row
        }

        $rows | Export-Csv -NoTypeInformation (Join-Path $folder "retime-runs.csv")
        $rows | Format-Table -AutoSize | Out-String -Width 300 | Write-Host

        # The ratio postpass / none of the mean p50 of the two runs a side, in each round and scene.
        $ratios = foreach ($group in ($rows | Where-Object { $null -ne $_.p50 } | Group-Object round, scene)) {
            $none = @($group.Group | Where-Object { $_.method -eq "none" } | ForEach-Object { $_.p50 })
            $post = @($group.Group | Where-Object { $_.method -eq "postpass" } | ForEach-Object { $_.p50 })
            $atP0 = @($group.Group | Where-Object { $_.p0_share -lt 1 }).Count -eq 0
            if ($none.Count -gt 0 -and $post.Count -gt 0) {
                [pscustomobject]@{
                    round = $group.Group[0].round; scene = $group.Group[0].scene
                    none_p50 = [math]::Round(($none | Measure-Object -Average).Average, 3)
                    postpass_p50 = [math]::Round(($post | Measure-Object -Average).Average, 3)
                    ratio = [math]::Round(($post | Measure-Object -Average).Average / ($none | Measure-Object -Average).Average, 3)
                    all_runs_at_p0 = $atP0
                }
            }
        }

        $ratios | Export-Csv -NoTypeInformation (Join-Path $folder "retime-ratios.csv")
        $ratios | Format-Table -AutoSize | Out-String -Width 300 | Write-Host
    }

    if ($Trace) {
        $folder = Join-Path $Out "trace"
        New-Item -ItemType Directory -Force $folder | Out-Null
        $timeout = if ($RunTimeoutSeconds -gt 0) { $RunTimeoutSeconds } else { 900 }
        $rows = @()
        foreach ($compare in $compareScenes | Where-Object { $_.Lines }) {
            $log = Join-Path $folder "compare-$($compare.Name).txt"
            [string[]] $arguments = $compare.Args + @(
                "--at", "0,75,150,225", "--trace", (Join-Path $folder "trace-$($compare.Name).csv"),
                "--dump", (Join-Path $folder "bmp"), "--dump-methods", "postpass"
            )
            $code = Invoke-Timed $log $arguments $timeout
            if ($code -eq 0) { $rows += @(Read-Compare $compare.Name $log) }
        }

        $rows | Export-Csv -NoTypeInformation (Join-Path $folder "compare-summary.csv")
    }

    if ($WarpTolerance) {
        $folder = Join-Path $Out "warp"
        New-Item -ItemType Directory -Force $folder | Out-Null
        $timeout = if ($RunTimeoutSeconds -gt 0) { $RunTimeoutSeconds } else { 1200 }
        $rows = @()
        foreach ($compare in $compareScenes) {
            $log = Join-Path $folder "compare-$($compare.Name).txt"
            [string[]] $arguments = $compare.Args + @("--warp", "--at", "0,75,150,225")
            if ($compare.Lines) { $arguments += @("--trace", (Join-Path $folder "trace-$($compare.Name).csv")) }
            $code = Invoke-Timed $log $arguments $timeout
            if ($code -eq 0) { $rows += @(Read-Compare $compare.Name $log) }
        }

        $rows | Export-Csv -NoTypeInformation (Join-Path $folder "compare-summary.csv")

        # The worst frame of each scene and method: the figures a tolerance is set from.
        $rows | Group-Object scene, method | ForEach-Object {
            [pscustomobject]@{
                scene = $_.Group[0].scene; method = $_.Group[0].method
                mean_max = ($_.Group | Measure-Object mean -Maximum).Maximum
                p99_max = ($_.Group | Measure-Object p99 -Maximum).Maximum
                max_max = ($_.Group | Measure-Object max -Maximum).Maximum
                over_05_max = ($_.Group | Measure-Object over_05 -Maximum).Maximum
                max_elsewhere_max = ($_.Group | Measure-Object max_elsewhere -Maximum).Maximum
            }
        } | Format-Table -AutoSize | Out-String -Width 300 | Write-Host
    }

    if ($failed -gt 0) {
        Write-Warning "$failed run(s) failed or timed out"
        exit 1
    }

    exit 0
}

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
