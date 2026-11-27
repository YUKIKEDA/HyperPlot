# Row R3b2's measurements of the blocks and the blocks in view (Issue #577, design §9 "重なりと予算（行 R3b2、Issue #472）"):
# the points scene of this project on a NativeAOT build, each run at High priority (the scene sets it), with the cores the
# other processes used written beside it and the GPU's clock over its frames at the end (docs/benchmarks/r3b2-overlap-budget.md).
# The sets, in the order a decision waits for them:
#   path    1e7 points in 8 parts, the whole view: whole pages against the blocks in view, alternated (A, B, B, A, A, B)
#   zoom64  1e7 points in 64 parts (blocks of 512), the zoom script: the same two forms, alternated
#   p4      requirement P-4's scenes: 1 and 64 parts whole, 1 and 8 parts zoomed, 6 shapes (uniform and clustered), 3 rounds
#   record  the frames of the zoom script recorded and not drawn, 8 and 64 parts, the two forms alternated: the CPU time of
#           the recording
#   scan    (Issue #600) the time of the scan before the recording, which chooses the drawing: 1e7 points uniform and
#           clustered, 1 and 8 parts, the whole view and the zoom script, drawn, 2 rounds
#   stage   (Issue #600) the frames of the zoom script recorded and not drawn, 1 and 8 parts, against a baseline build
#           (-Baseline, an executable of main that times the same stage, its count of the view), alternated A, B, B, A
#   scan1e9 (Issue #600) the same scan at 1e9 uniform points (about 65,536 resident cells), the density scene's zoom with
#           Auto; its 8 GB of data files go under .dev/r3b2p2-data and are deleted at the end
#   reads   (Issue #603) 1e9 uniform points drawn as points: a tenth of the square filled with its pages and panned, with
#           the index's copy in the page cache and then out of it, and the pool disposed while pages are read; 3 runs
#   m4      (Issue #603) the same with 16 line series of 1e6 points, whose M4 extraction is timed with reads and without;
#           2 runs
#   cells   (Issue #603) 1e9 uniform and clustered points, a still view whose pages are all on the GPU, recorded and not
#           drawn: the time and the bytes of the table of cells, against a baseline build that writes no table
#           (-Baseline), alternated A, B, B, A
#   cap     (Issue #603) 2e8 points in clusters with halos (-CapCount; past 131,072 resident cells), deep zooms into a
#           cluster, with the limit of cells and without it, alternated
#   The data files of reads, m4, cells, and cap go under .dev/r3b2p4-data and are deleted at the end.
#   refit   (Issue #624) the 41 scenes of the switch formula's prototype (Issue #552's sweep 8) and 3 it did not measure,
#           drawn as raw points (Draw = Points), whole view, opacity 0.6; each run then prints what Auto reads over its
#           views (the largest bin from the tiles and the estimate from the resident cells). Rounds -FirstRound to
#           -Rounds (1 to 3 by default), the even rounds in the reverse order; -Only keeps the scenes whose names match a
#           regular expression (.cursor/rules/pace.mdc: one round is read before the rest)
#   pair    (Issue #563) two Auto-eligible layers of 8e6 points drawn as raw points (Draw = Points) over one another in one
#           panel: both uniform, both clustered (σ 10), one of each; and each layer alone; 6 DIPs, opacity 0.6, the whole
#           view; each run then prints what Auto reads of each layer and the panel's sum of the predicted times. Rounds
#           -FirstRound to -Rounds, the even rounds in the reverse order; -Only as for refit
# Usage, from PowerShell 7: pwsh -NoProfile -File benchmarks/HyperPlot.E2E/run-r3b2-overlap-budget.ps1 <output file> [sets]
# The machine log and the GPU clock log go next to the output file (<output>.machine.log, <output>.clocks.csv), and so
# does a file of one row a frame for each run (<output>.<run>.csv). The published build goes under .dev/ and is deleted at
# the end.
param(
    [Parameter(Mandatory)] [string] $Out,
    [string[]] $Sets = @('path', 'zoom64', 'p4', 'record'),
    [int] $TimeoutSeconds = 300,
    [string] $Baseline = '',
    [int] $ReadsRuns = 3,
    [string] $CapCount = '2e8',
    [int] $Rounds = 3,
    [int] $FirstRound = 1,
    [string] $Only = '.',
    # The folder under .dev/ the build is published to: runs from two worktrees name their own.
    [string] $PublishName = 'r3b2-pub577'
)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
# The main checkout's .dev/, also from a worktree under it: the parent of the shared .git folder.
$dev = Join-Path (Split-Path -Parent (Resolve-Path (git -C $repo rev-parse --path-format=absolute --git-common-dir))) '.dev'
$publish = Join-Path $dev $PublishName
$machineLog = "$Out.machine.log"
$clockLog = "$Out.clocks.csv"
$exe = Join-Path $publish 'HyperPlot.E2E.exe'
$failed = 0
$ran = [System.Collections.Generic.List[object]]::new()

function Write-Out([string] $text) {
    $text | Out-File -FilePath $Out -Append -Encoding utf8
}

# The GPU's clock over a run's frames: the samples of the clock log between the two times of day the scene printed.
# Written after the last run: nvidia-smi writes its log some seconds late.
function Write-Clocks([string] $name, [string[]] $lines) {
    $ran = $lines | Where-Object { $_ -match 'ran (\d\d:\d\d:\d\d\.\d+) to (\d\d:\d\d:\d\d\.\d+)' } | Select-Object -First 1
    if (-not $ran -or -not (Test-Path $clockLog)) {
        return
    }

    $null = $ran -match 'ran (\d\d:\d\d:\d\d\.\d+) to (\d\d:\d\d:\d\d\.\d+)'
    $from = [TimeSpan]::Parse($Matches[1])
    $to = [TimeSpan]::Parse($Matches[2])
    $clocks = @()
    $states = @{}
    # nvidia-smi holds the log open for writing: read it shared.
    $stream = [IO.FileStream]::new($clockLog, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    $reader = [IO.StreamReader]::new($stream)
    $rows = $reader.ReadToEnd() -split "`n"
    $reader.Dispose()
    foreach ($row in $rows) {
        if ($row -match '^\d+/\d+/\d+ (\d\d:\d\d:\d\d\.\d+), (\d+) MHz, (P\d+)') {
            $at = [TimeSpan]::Parse($Matches[1])
            if ($at -ge $from -and $at -le $to) {
                $clocks += [int] $Matches[2]
                $states[$Matches[3]] = 1 + [int] $states[$Matches[3]]
            }
        }
    }

    if ($clocks.Count -gt 0) {
        $sorted = $clocks | Sort-Object
        $shares = ($states.Keys | Sort-Object | ForEach-Object { "$_=$($states[$_])" }) -join ' '
        Write-Out "gpu_clock $($name): samples=$($clocks.Count) min=$($sorted[0]) p50=$($sorted[[int][math]::Floor($sorted.Count / 2)]) max=$($sorted[-1]) MHz; $shares"
    }
}

# One run of the scene; a run that fails or passes its time limit is written and counted, and the next runs
# (.cursor/rules/bench.mdc).
function Invoke-Scene([string] $name, [string[]] $arguments, [string] $program = $exe, [string] $scene = 'points') {
    $began = Get-Date
    Write-Out "=== $name started $($began.ToString('HH:mm:ss')): $scene $($arguments -join ' ')"
    $stdout = "$Out.run.txt"
    # One row a frame, next to the output file: the frame's times, its runs and points, and its allocations by stage.
    $frames = "$Out.$($name -replace '[^A-Za-z0-9]+', '-').csv"
    # The points scene takes the file after its first 11 arguments; a run that passes more names the file itself.
    $process = Start-Process -FilePath $program -ArgumentList (@($scene) + $arguments + $(if ($scene -eq 'points' -and $arguments.Count -le 11) { @($frames) } else { @() })) -NoNewWindow -PassThru `
        -RedirectStandardOutput $stdout -RedirectStandardError "$Out.run.err.txt"
    $code = 'timeout'
    if ($process.WaitForExit($TimeoutSeconds * 1000)) {
        $process.WaitForExit()
        $code = $process.ExitCode
    }
    else {
        Stop-Process -Id $process.Id -Force
    }

    $lines = @([IO.File]::ReadAllLines($stdout)) + @([IO.File]::ReadAllLines("$Out.run.err.txt"))
    $lines | Out-File -FilePath $Out -Append -Encoding utf8
    $script:ran.Add(@($name, $lines))
    Remove-Item -LiteralPath $stdout, "$Out.run.err.txt" -Force
    Write-Out "=== $name ended $((Get-Date).ToString('HH:mm:ss')) after $([math]::Round(((Get-Date) - $began).TotalSeconds, 1)) s, exit $code"
    if ("$code" -ne '0') {
        $script:failed++
    }
}

# 1e7 points, discs of 6 DIPs at an opacity of 0.6, 600 frames after the warm-up.
function Get-Arguments([string] $distribution, [int] $categories, [string] $view, [string] $channel, [string] $form, [string] $frames) {
    @('10000000', '6', '0.6', $distribution, '600', "$categories", $view, $channel, 'Circle', $form, $frames)
}

Set-Content -Path $Out -Value "# r3b2 run (Issues #577, #600, and #603), $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), commit $(git -C $repo rev-parse --short HEAD), sets $($Sets -join ' ')" -Encoding utf8
$watcher = $null
$clocks = $null
try {
    $env:PATH += ';C:\Program Files (x86)\Microsoft Visual Studio\Installer'
    $publishFrom = Get-Date
    Write-Out '## publish'
    dotnet publish (Join-Path $repo 'benchmarks\HyperPlot.E2E') -c Release -r win-x64 -o $publish 2>&1 |
        ForEach-Object { "$_" } | Out-File -FilePath $Out -Append -Encoding utf8
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $exe) -or (Get-Item $exe).LastWriteTime -lt $publishFrom) {
        throw "publish failed with exit $LASTEXITCODE, or the executable is older than this run"
    }

    Write-Out "## executable $((Get-Item $exe).Length) bytes"
    $watcher = Start-Process pwsh -WindowStyle Hidden -PassThru -ArgumentList @(
        '-NoProfile', '-File', (Join-Path $repo 'benchmarks\HyperPlot.DataPath\watch-machine.ps1'), '-Log', $machineLog, '-Scene', 'HyperPlot.E2E'
    )
    $clocks = Start-Process nvidia-smi -WindowStyle Hidden -PassThru -ArgumentList @(
        '--query-gpu=timestamp,clocks.gr,pstate', '--format=csv', '-lms', '200', '-f', $clockLog
    )

    if ($Sets -contains 'path') {
        $round = 0
        foreach ($form in @('pages', 'blocks', 'blocks', 'pages', 'pages', 'blocks')) {
            $round++
            Invoke-Scene "path 8 parts whole $form, run $round" (Get-Arguments 'uniform' 8 'whole' 'color' $form 'draw')
        }
    }

    if ($Sets -contains 'zoom64') {
        $round = 0
        foreach ($form in @('blocks', 'pages', 'pages', 'blocks', 'blocks', 'pages')) {
            $round++
            Invoke-Scene "zoom 64 parts $form, run $round" (Get-Arguments 'uniform' 64 'zoom' 'color' $form 'draw')
        }
    }

    if ($Sets -contains 'p4') {
        $scenes = @(
            @('1 part whole', 'uniform', 0, 'whole', 'color'),
            @('64 parts whole', 'uniform', 64, 'whole', 'color'),
            @('1 part zoom', 'uniform', 0, 'zoom', 'color'),
            @('8 parts zoom', 'uniform', 8, 'zoom', 'color'),
            @('6 shapes uniform whole', 'uniform', 6, 'whole', 'shape'),
            @('6 shapes cluster whole', 'cluster', 6, 'whole', 'shape')
        )
        for ($round = 1; $round -le 3; $round++) {
            $order = if ($round % 2) { 0..5 } else { 5..0 }
            foreach ($i in $order) {
                $scene = $scenes[$i]
                Invoke-Scene "p4 $($scene[0]), round $round" (Get-Arguments $scene[1] $scene[2] $scene[3] $scene[4] 'blocks' 'draw')
            }
        }
    }

    if ($Sets -contains 'scan') {
        $scenes = @(
            @('1 part whole uniform', 'uniform', 0, 'whole'),
            @('1 part whole cluster', 'cluster', 0, 'whole'),
            @('8 parts whole uniform', 'uniform', 8, 'whole'),
            @('8 parts whole cluster', 'cluster', 8, 'whole'),
            @('1 part zoom uniform', 'uniform', 0, 'zoom'),
            @('1 part zoom cluster', 'cluster', 0, 'zoom'),
            @('8 parts zoom uniform', 'uniform', 8, 'zoom'),
            @('8 parts zoom cluster', 'cluster', 8, 'zoom')
        )
        for ($round = 1; $round -le 2; $round++) {
            $order = if ($round % 2) { 0..7 } else { 7..0 }
            foreach ($i in $order) {
                $scene = $scenes[$i]
                Invoke-Scene "scan $($scene[0]), round $round" (Get-Arguments $scene[1] $scene[2] $scene[3] 'color' 'blocks' 'draw')
            }
        }
    }

    if ($Sets -contains 'stage') {
        if (-not (Test-Path $Baseline)) {
            throw "the stage set needs -Baseline, an executable of main that times its count of the view"
        }

        foreach ($categories in @(0, 8)) {
            $round = 0
            foreach ($side in @('head', 'main', 'main', 'head', 'head', 'main', 'main', 'head')) {
                $round++
                $program = if ($side -eq 'head') { $exe } else { $Baseline }
                Invoke-Scene "stage $categories parts zoom $side, run $round" (Get-Arguments 'uniform' $categories 'zoom' 'color' 'blocks' 'record') $program
            }
        }
    }

    if ($Sets -contains 'scan1e9') {
        $data = Join-Path $dev 'r3b2p2-data'
        New-Item -ItemType Directory -Force -Path $data | Out-Null
        try {
            Invoke-Scene 'scan 1e9 uniform zoom auto' @($data, '1e9', 'uniform', 'zoom', '600', 'auto') $exe 'density'
        }
        finally {
            Remove-Item -LiteralPath $data -Recurse -Force
        }
    }

    $data4 = Join-Path $dev 'r3b2p4-data'
    if ($Sets | Where-Object { $_ -in @('reads', 'm4', 'cells', 'cap') }) {
        New-Item -ItemType Directory -Force -Path $data4 | Out-Null
    }

    try {
        if ($Sets -contains 'reads') {
            for ($round = 1; $round -le $ReadsRuns; $round++) {
                Invoke-Scene "reads 1e9 uniform, run $round" @('fill', $data4, '1e9', 'uniform') $exe 'reads'
            }
        }

        if ($Sets -contains 'm4') {
            for ($round = 1; $round -le 2; $round++) {
                Invoke-Scene "m4 1e9 uniform, run $round" @('m4', $data4, '1e9', 'uniform') $exe 'reads'
            }
        }

        if ($Sets -contains 'cells') {
            if (-not (Test-Path $Baseline)) {
                throw "the cells set needs -Baseline, an executable that writes no table of cells"
            }

            foreach ($distribution in @('uniform', 'cluster')) {
                $round = 0
                foreach ($side in @('head', 'base', 'base', 'head')) {
                    $round++
                    $program = if ($side -eq 'head') { $exe } else { $Baseline }
                    Invoke-Scene "cells 1e9 $distribution $side, run $round" @('cells', $data4, '1e9', $distribution) $program 'reads'
                }
            }
        }

        if ($Sets -contains 'cap') {
            $round = 0
            foreach ($limit in @('capped', 'free', 'free', 'capped')) {
                $round++
                Invoke-Scene "cap $CapCount $limit, run $round" @('cap', $data4, $CapCount, $limit) $exe 'reads'
            }
        }
    }
    finally {
        if (Test-Path $data4) {
            Remove-Item -LiteralPath $data4 -Recurse -Force
        }
    }

    if ($Sets -contains 'refit') {
        # name, points, diameter in DIPs, σ of the clump (0: uniform), categories, facet panels, how the rows fall into the
        # categories. The first 41 are Issue #552's sweep 8, in the order of its table (design §5.5.2 "41 場面"); the last 3
        # are the ones the prototype did not measure (Issue #624's Scope).
        $scenes = @(
            @('d1-uniform', 10000000, 1, 0, 0, 0, 'even'),
            @('d1-s10-p6', 10000000, 1, 10, 6, 0, 'even'),
            @('d1-s10', 10000000, 1, 10, 0, 0, 'even'),
            @('d6-n5e6-s8.49', 5000000, 6, 8.49, 0, 0, 'even'),
            @('d10-n5e6', 5000000, 10, 0, 0, 0, 'even'),
            @('d6-uniform-p8', 10000000, 6, 0, 8, 0, 'even'),
            @('d6-n5e6-s7', 5000000, 6, 7, 0, 0, 'even'),
            @('d6-n5e6-s6', 5000000, 6, 6, 0, 0, 'even'),
            @('d6-uniform', 10000000, 6, 0, 0, 0, 'even'),
            @('d12-n5e6', 5000000, 12, 0, 0, 0, 'even'),
            @('d6-n5e6-s5', 5000000, 6, 5, 0, 0, 'even'),
            @('d6-uniform-facet6', 10000000, 6, 0, 0, 6, 'even'),
            @('d4-s9', 10000000, 4, 9, 0, 0, 'even'),
            @('d4-s8', 10000000, 4, 8, 0, 0, 'even'),
            @('d6-s10-p8', 10000000, 6, 10, 8, 0, 'even'),
            @('d4-s7.5', 10000000, 4, 7.5, 0, 0, 'even'),
            @('d6-n7.5e6-s10', 7500000, 6, 10, 0, 0, 'even'),
            @('d4-s7', 10000000, 4, 7, 0, 0, 'even'),
            @('d6-s10-p6', 10000000, 6, 10, 6, 0, 'even'),
            @('d4-s6.5', 10000000, 4, 6.5, 0, 0, 'even'),
            @('d10-n7.5e6', 7500000, 10, 0, 0, 0, 'even'),
            @('d6-s18', 10000000, 6, 18, 0, 0, 'even'),
            @('d6-n7.5e6-s8', 7500000, 6, 8, 0, 0, 'even'),
            @('d8-uniform', 10000000, 8, 0, 0, 0, 'even'),
            @('d6-s16', 10000000, 6, 16, 0, 0, 'even'),
            @('d8-s30', 10000000, 8, 30, 0, 0, 'even'),
            @('d6-s14', 10000000, 6, 14, 0, 0, 'even'),
            @('d6-s10-p2', 10000000, 6, 10, 2, 0, 'even'),
            @('d6-s13', 10000000, 6, 13, 0, 0, 'even'),
            @('d6-s12.5', 10000000, 6, 12.5, 0, 0, 'even'),
            @('d8-s26', 10000000, 8, 26, 0, 0, 'even'),
            @('d6-s12', 10000000, 6, 12, 0, 0, 'even'),
            @('d6-s11', 10000000, 6, 11, 0, 0, 'even'),
            @('d12-n7.5e6', 7500000, 12, 0, 0, 0, 'even'),
            @('d6-s10', 10000000, 6, 10, 0, 0, 'even'),
            @('d8-s22', 10000000, 8, 22, 0, 0, 'even'),
            @('d10-n1e7', 10000000, 10, 0, 0, 0, 'even'),
            @('d8-s20', 10000000, 8, 20, 0, 0, 'even'),
            @('d8-s18', 10000000, 8, 18, 0, 0, 'even'),
            @('d8-s16', 10000000, 8, 16, 0, 0, 'even'),
            @('d12-n1e7', 10000000, 12, 0, 0, 0, 'even'),
            @('d6-s10-p6-uneven', 10000000, 6, 10, 6, 0, 'uneven'),
            @('d16-n5e6', 5000000, 16, 0, 0, 0, 'even'),
            @('d12-n2.5e6-s5', 2500000, 12, 5, 0, 0, 'even')
        )
        # A list, not a pipeline: one scene left would come out of a pipeline as its own array of fields.
        $kept = [System.Collections.Generic.List[object]]::new()
        foreach ($s in $scenes) {
            if ($s[0] -match $Only) {
                $kept.Add($s)
            }
        }

        $scenes = $kept
        for ($round = $FirstRound; $round -le $Rounds; $round++) {
            $order = if ($round % 2) { 0..($scenes.Count - 1) } else { ($scenes.Count - 1)..0 }
            foreach ($i in $order) {
                $s = $scenes[$i]
                $distribution = if ($s[3] -eq 0) { 'uniform' } else { 'cluster' }
                $sigma = if ($s[3] -eq 0) { 10 } else { $s[3] }
                [string[]] $arguments = @(
                    "$($s[1])", "$($s[2])", '0.6', $distribution, '600', "$($s[4])", 'whole', 'color', 'Circle', 'blocks', 'draw',
                    "$Out.refit-$($s[0] -replace '[^A-Za-z0-9]+', '-')-r$round.csv", 'none', "$sigma", "$($s[5])", 'points', $s[6]
                )
                Invoke-Scene "refit $($s[0]), round $round" $arguments
            }
        }
    }

    if ($Sets -contains 'pair') {
        # name, the first layer's distribution, the second's ("none": the first alone).
        $scenes = @(
            @('alone-uniform', 'uniform', 'none'),
            @('alone-cluster', 'cluster', 'none'),
            @('pair-uniform-uniform', 'uniform', 'uniform'),
            @('pair-cluster-cluster', 'cluster', 'cluster'),
            @('pair-uniform-cluster', 'uniform', 'cluster')
        )
        $kept = [System.Collections.Generic.List[object]]::new()
        foreach ($s in $scenes) {
            if ($s[0] -match $Only) {
                $kept.Add($s)
            }
        }

        $scenes = $kept
        for ($round = $FirstRound; $round -le $Rounds; $round++) {
            $order = if ($round % 2) { 0..($scenes.Count - 1) } else { ($scenes.Count - 1)..0 }
            foreach ($i in $order) {
                $s = $scenes[$i]
                [string[]] $arguments = @(
                    '8000000', '6', '0.6', $s[1], '600', '0', 'whole', 'color', 'Circle', 'blocks', 'draw',
                    "$Out.pair-$($s[0])-r$round.csv", 'none', '10', '0', 'points', 'even', $s[2]
                )
                Invoke-Scene "pair $($s[0]), round $round" $arguments
            }
        }
    }

    if ($Sets -contains 'record') {
        foreach ($categories in @(8, 64)) {
            $round = 0
            foreach ($form in @('blocks', 'pages', 'pages', 'blocks')) {
                $round++
                Invoke-Scene "record $categories parts zoom $form, run $round" (Get-Arguments 'uniform' $categories 'zoom' 'color' $form 'record')
            }
        }
    }
}
catch {
    Write-Out "## FAILED: $_"
    $failed++
}
finally {
    if ($null -ne $watcher) {
        New-Item -ItemType File -Force -Path "$machineLog.stop" | Out-Null
        $watcher.WaitForExit(30000) | Out-Null
    }

    if ($null -ne $clocks -and -not $clocks.HasExited) {
        Stop-Process -Id $clocks.Id -Force
        $clocks.WaitForExit(10000) | Out-Null
    }

    Write-Out '## GPU clock over the frames of each run'
    foreach ($run in $ran) {
        Write-Clocks $run[0] $run[1]
    }

    if (Test-Path $publish) {
        Remove-Item -LiteralPath $publish -Recurse -Force
    }

    Write-Out "## runs that failed: $failed; cleaned up $(Get-Date -Format 'HH:mm:ss'); left: $(@($publish) | Where-Object { Test-Path $_ } | Join-String -Separator ', ')"
    Write-Out "DONE $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
}

exit $failed
