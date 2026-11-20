# Row R3b2's measurements of the blocks and the blocks in view (Issue #577, design §9 "重なりと予算（行 R3b2、Issue #472）"):
# the points scene of this project on a NativeAOT build, each run at High priority (the scene sets it), with the cores the
# other processes used and the GPU's clock over its frames written beside it (docs/benchmarks/r3b2-overlap-budget.md).
# The sets, in the order a decision waits for them:
#   path    1e7 points in 8 parts, the whole view: whole pages against the blocks in view, alternated (A, B, B, A, A, B)
#   zoom64  1e7 points in 64 parts (blocks of 512), the zoom script: the same two forms, alternated
#   p4      requirement P-4's scenes: 1 and 64 parts whole, 1 and 8 parts zoomed, 6 shapes (uniform and clustered), 3 rounds
#   record  the frames of the zoom script recorded and not drawn, 8 and 64 parts, the two forms alternated: the CPU time of
#           the recording
# Usage, from PowerShell 7: pwsh -NoProfile -File benchmarks/HyperPlot.E2E/run-r3b2-overlap-budget.ps1 <output file> [sets]
# The machine log and the GPU clock log go next to the output file (<output>.machine.log, <output>.clocks.csv). The
# published build goes under .dev/ and is deleted at the end.
param(
    [Parameter(Mandatory)] [string] $Out,
    [string[]] $Sets = @('path', 'zoom64', 'p4', 'record'),
    [int] $TimeoutSeconds = 300
)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
# The main checkout's .dev/, also from a worktree under it: the parent of the shared .git folder.
$dev = Join-Path (Split-Path -Parent (Resolve-Path (git -C $repo rev-parse --path-format=absolute --git-common-dir))) '.dev'
$publish = Join-Path $dev 'r3b2-pub577'
$machineLog = "$Out.machine.log"
$clockLog = "$Out.clocks.csv"
$exe = Join-Path $publish 'HyperPlot.E2E.exe'
$failed = 0

function Write-Out([string] $text) {
    $text | Out-File -FilePath $Out -Append -Encoding utf8
}

# The GPU's clock over a run's frames: the samples of the clock log between the two times of day the scene printed.
function Write-Clocks([string[]] $lines) {
    $ran = $lines | Where-Object { $_ -match 'ran (\d\d:\d\d:\d\d\.\d+) to (\d\d:\d\d:\d\d\.\d+)' } | Select-Object -First 1
    if (-not $ran -or -not (Test-Path $clockLog)) {
        return
    }

    $null = $ran -match 'ran (\d\d:\d\d:\d\d\.\d+) to (\d\d:\d\d:\d\d\.\d+)'
    $from = [TimeSpan]::Parse($Matches[1])
    $to = [TimeSpan]::Parse($Matches[2])
    $clocks = @()
    $states = @{}
    foreach ($row in [IO.File]::ReadAllLines($clockLog)) {
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
        Write-Out "gpu_clock: samples=$($clocks.Count) min=$($sorted[0]) p50=$($sorted[[int][math]::Floor($sorted.Count / 2)]) max=$($sorted[-1]) MHz; $shares"
    }
}

# One run of the scene; a run that fails or passes its time limit is written and counted, and the next runs
# (.cursor/rules/bench.mdc).
function Invoke-Scene([string] $name, [string[]] $arguments) {
    $began = Get-Date
    Write-Out "=== $name started $($began.ToString('HH:mm:ss')): points $($arguments -join ' ')"
    $stdout = "$Out.run.txt"
    $process = Start-Process -FilePath $exe -ArgumentList (@('points') + $arguments) -NoNewWindow -PassThru `
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
    Write-Clocks $lines
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

Set-Content -Path $Out -Value "# r3b2 run (Issue #577), $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), commit $(git -C $repo rev-parse --short HEAD), sets $($Sets -join ' ')" -Encoding utf8
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
    }

    if (Test-Path $publish) {
        Remove-Item -LiteralPath $publish -Recurse -Force
    }

    Write-Out "## runs that failed: $failed; cleaned up $(Get-Date -Format 'HH:mm:ss'); left: $(@($publish) | Where-Object { Test-Path $_ } | Join-String -Separator ', ')"
    Write-Out "DONE $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
}

exit $failed
