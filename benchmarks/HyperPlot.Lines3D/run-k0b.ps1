# SPIKE (row K0b, Issue #488): the measurement of 3D line simplification, CPU only.
# Usage (PowerShell 7, no other build or measurement on the machine; the script adds the vswhere folder to PATH itself):
#   ./benchmarks/HyperPlot.Lines3D/run-k0b.ps1 -Out <folder outside the repository> [-Rounds 1] [-NewOnly] [-NewRounds 2]
# Every run's output is kept in -Out (<name>.txt, stderr in <name>.err.txt); the machine's state is logged beside it
# (watch-machine.ps1). Each run has a timeout; a run that times out is killed and counted, and the script exits with 1 when
# any run failed or timed out. A failed check stops the script: what follows would measure wrong code.
#
# The first measurement (2026-11-14, the Result of Issue #488), skipped with -NewOnly, about 2 hours:
#   1. verify on 1e6 vertices: the bound against brute force, for each data, segment size, and split distance (minutes).
#   2. run on 1e8 vertices, perp: for each data, the segment sizes 2^14, 2^16, 2^18 in a balanced order (14, 16, 18, 18, 16,
#      14) per round: build time with 8 workers, memory, and the orbit and zoom scenes under budgets 2^18, 2^20, 2^22.
#   3. run on 1e8 vertices, linf at 2^16, for each data, once: the split distance's effect on vertices and time.
#   4. run on 1e6 vertices for each segment size (quick figures).
#
# The second (Issue #488's Decision of 2026-11-14: segments of 2^14, K-3's rotation at 60 fps as a provisional goal, the
# derived data packed at 8 B a vertex). Segments of 2^14, perp, the orbit; about 20 minutes with -NewRounds 2:
#   5. check on 1e6 (walk, helix): the packed layout, dfs/4, bfs/1, bfs/4 against the split in-order walk; the level-order
#      walk stopped at a quarter of the nodes against brute force. Stops the script on a difference.
#   6. per data (walk first: it is the case the decision waits for), and the walk again in round 2:
#      par on 1e8: per frame, each variant in a rotated order, unpinned at high priority: dfs/1, 2, 4, 8 (the 1-pixel walk
#        split over threads), bfs/8 (the level-order walk, no limit), time/1/8, time/8/8, time/8/12 (stopped 8 or 12 ms
#        after the start), nodes/8/262144, nodes/8/524288 (stopped after that many nodes visited).
#      async on 1e8: a 60 Hz frame thread and a producer that extracts the newest view (dfs/8, dfs/4, time/8/12, time/8/8),
#        3 rounds (2 for the helix) of 600 frames a variant: the age of the drawn line in frames.
#      layout on 1e8: pinned, one thread, the split arrays (10 B) and the packed nodes (8 B) alternated per frame.
#   7. layout on 1e6 (walk, helix): the nodes visited per vertex and the time per node at 1e6 against 1e8.
#
# Expected before measuring the second (worst case and practical range):
# - check: no difference; the stopped walk's largest error under its achieved tolerance.
# - layout: the same entries; packed 0 to 15 % faster than split (one 8-byte read a node instead of two arrays), within the
#   spread at worst. Nodes per vertex about 2 (each kept vertex is a node not pruned with up to 2 children visited) at both
#   sizes; if so, the 4 times of the Result is the time per node (the cache), not the nodes.
# - par, walk orbit: dfs/1 95 to 115 ms as in the Result; dfs/8 4 to 7 times faster (14 to 25 ms: 6 performance cores, 2
#   threads on efficiency cores, the memory's bandwidth), short of 16.7 ms at p95 in the worst case. bfs/8 within 1.5 times
#   dfs/8 (the sort of the kept vertices, about 1 ms). time/8/8 and time/8/12 end within 0.5 ms of their limit (the clock
#   is read every 256 nodes) and reach 1 to 3 px; time/1/8 3 to 10 px. The helix: every variant under 2 ms, nothing stopped.
# - async, walk orbit: dfs/8 shows lines 2 to 3 frames old (its extraction is longer than a frame); time/8/12 1 frame old
#   (the previous frame's view) at 1 to 3 px; the frame thread's own work under 50 µs; frames 0 to 1 ms late.
# - allocation per run: 0 B on every member.
param(
    [Parameter(Mandatory = $true)] [string] $Out,
    [int] $Rounds = 1,
    [switch] $NewOnly,
    [int] $NewRounds = 2
)

$ErrorActionPreference = "Stop"
$Out = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Out)
New-Item -ItemType Directory -Force $Out | Out-Null
if (-not (Get-Command vswhere.exe -ErrorAction SilentlyContinue)) {
    $env:PATH += ';C:\Program Files (x86)\Microsoft Visual Studio\Installer'
}

$publish = Join-Path $Out "publish"
dotnet publish (Join-Path $PSScriptRoot "HyperPlot.Lines3D.csproj") -c Release -r win-x64 -o $publish
if ($LASTEXITCODE -ne 0) { throw "publish failed with exit code $LASTEXITCODE" }
$exe = Join-Path $publish "HyperPlot.Lines3D.exe"
if (-not (Test-Path $exe)) { throw "publish made no $exe" }
$machineLog = Join-Path $Out "machine.log"
$watch = Start-Process pwsh -WindowStyle Hidden -PassThru -ArgumentList @(
    "-NoProfile", "-File", (Join-Path $PSScriptRoot "../HyperPlot.DataPath/watch-machine.ps1"), "-Log", $machineLog, "-Scene", "HyperPlot.Lines3D"
)
$script:failures = [System.Collections.Generic.List[string]]::new()

# Runs one measurement with its output in <Out>/<Name>.txt; kills it after -Minutes. A failure or a timeout is recorded;
# with -Required it also stops the script.
function Invoke-Spike([string] $Name, [string[]] $Arguments, [int] $Minutes = 30, [switch] $Required) {
    $log = Join-Path $Out "$Name.txt"
    $errors = Join-Path $Out "$Name.err.txt"
    Write-Output "$(Get-Date -Format HH:mm:ss) $Name (timeout $Minutes min)"
    [string[]] $quoted = @($Arguments | ForEach-Object { '"' + $_ + '"' })
    $process = Start-Process -FilePath $exe -ArgumentList ($quoted -join ' ') -NoNewWindow -PassThru `
        -RedirectStandardOutput $log -RedirectStandardError $errors
    $null = $process.Handle # keeps the exit code readable after the process ends
    if (-not $process.WaitForExit($Minutes * 60 * 1000)) {
        Stop-Process -Id $process.Id -Force
        $process.WaitForExit()
        $message = "$Name timed out after $Minutes min"
    }
    else {
        $process.WaitForExit()
        $message = if ($process.ExitCode -ne 0) { "$Name exited with $($process.ExitCode)" } else { $null }
    }

    if ($null -ne $message) {
        $script:failures.Add($message)
        Write-Warning "$message`: $log"
        if ($Required) { throw $message }
    }
}

try {
    if (-not $NewOnly) {
        foreach ($data in "walk", "helix") {
            foreach ($segment in 14, 16, 18) {
                foreach ($split in "perp", "linf") {
                    Invoke-Spike "verify-$data-s$segment-$split" @("verify", "--data", $data, "--count", "1e6", "--segment", $segment, "--split", $split) -Minutes 10
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
                    ) -Minutes 25
                }
            }
        }

        foreach ($data in "walk", "helix") {
            Invoke-Spike "$data-1e8-s16-linf" @("run", "--data", $data, "--count", "1e8", "--segment", "16", "--split", "linf") -Minutes 25
        }

        foreach ($data in "walk", "helix") {
            foreach ($segment in 14, 16, 18) {
                Invoke-Spike "$data-1e6-s$segment-perp" @("run", "--data", $data, "--count", "1e6", "--segment", $segment) -Minutes 10
            }
        }
    }

    # The second measurement (Issue #488's Decision of 2026-11-14). Segments of 2^14 are the commands' default.
    foreach ($data in "walk", "helix") {
        Invoke-Spike "check-$data-1e6" @("check", "--data", $data, "--count", "1e6", "--segment", "14") -Minutes 5 -Required
    }

    for ($round = 1; $round -le $NewRounds; $round++) {
        # Round 1 runs both data, the walk first; later rounds the walk only, in the reverse order of the commands.
        [string[]] $datas = if ($round -eq 1) { @("walk", "helix") } else { @("walk") }
        foreach ($data in $datas) {
            $asyncRounds = if ($data -eq "walk") { "3" } else { "2" }
            $par = { Invoke-Spike "n$round-par-$data-1e8" @(
                    "par", "--data", $data, "--count", "1e8", "--segment", "14", "--csv", (Join-Path $Out "n$round-par-$data-1e8.csv")
                ) -Minutes 12 }
            $async = { Invoke-Spike "n$round-async-$data-1e8" @(
                    "async", "--data", $data, "--count", "1e8", "--segment", "14", "--rounds", $asyncRounds
                ) -Minutes 10 }
            $layout = { Invoke-Spike "n$round-layout-$data-1e8" @("layout", "--data", $data, "--count", "1e8", "--segment", "14") -Minutes 10 }
            if ($round % 2 -eq 1) {
                & $par
                & $async
                & $layout
            }
            else {
                & $layout
                & $async
                & $par
            }
        }
    }

    foreach ($data in "walk", "helix") {
        Invoke-Spike "layout-$data-1e6" @("layout", "--data", $data, "--count", "1e6", "--segment", "14") -Minutes 5
    }
}
finally {
    New-Item -ItemType File -Force "$machineLog.stop" | Out-Null
    $watch.WaitForExit(30000) | Out-Null
    Write-Output "$(Get-Date -Format HH:mm:ss) done; failures: $($script:failures.Count)"
    foreach ($failure in $script:failures) { Write-Output "  $failure" }
}

if ($script:failures.Count -gt 0) { exit 1 }
