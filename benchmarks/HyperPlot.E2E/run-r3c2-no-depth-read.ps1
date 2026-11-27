# Row R3c2's measurement (Issue #617 Decision 3, design §6.4 "強調のない run の描き方", docs/benchmarks/r3c2-no-depth-read.md):
# the points scene of this project on two NativeAOT builds, the code before the change (-Base, a commit whose points scene
# takes the selection argument and whose resident markers have one pixel shader) and the checkout, alternated within each
# round (A, B in odd rounds, B, A in even ones, the scenes in reverse order every other round). Each run is at High
# priority (the scene sets it), with the cores the other processes used, and the GPU's clock over its frames at the end.
# The scenes: 1e7 points, discs of 6 DIP at an opacity of 0.6, the whole view, 600 frames after 120 of warm-up:
#   1 part, 8 parts, 64 parts (colored), 6 shapes uniform and clustered, and 1 part under a rectangle and under a lasso.
# Usage, from PowerShell 7: pwsh -NoProfile -File benchmarks/HyperPlot.E2E/run-r3c2-no-depth-read.ps1 <output file> [-Base <commit>] [-Rounds 6]
# The machine log and the GPU clock log go next to the output file (<output>.machine.log, <output>.clocks.csv). The base's
# worktree and both published builds go under .dev/ and are deleted at the end.
param(
    [Parameter(Mandatory)] [string] $Out,
    [string] $Base = 'bd46e42',
    [int] $Rounds = 6,
    [int] $TimeoutSeconds = 300
)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
# The main checkout's .dev/, also from a worktree under it: the parent of the shared .git folder.
$dev = Join-Path (Split-Path -Parent (Resolve-Path (git -C $repo rev-parse --path-format=absolute --git-common-dir))) '.dev'
$baseTree = Join-Path $dev 'r3c2-base'
$publishes = @{ base = (Join-Path $dev 'r3c2-pub-base'); head = (Join-Path $dev 'r3c2-pub-head') }
$machineLog = "$Out.machine.log"
$clockLog = "$Out.clocks.csv"
$failed = 0
$ran = [System.Collections.Generic.List[object]]::new()

function Write-Out([string] $text) {
    $text | Out-File -FilePath $Out -Append -Encoding utf8
}

# The GPU's clock over a run's frames: the samples of the clock log between the two times of day the scene printed.
function Write-Clocks([string] $name, [string[]] $lines) {
    $line = $lines | Where-Object { $_ -match 'ran (\d\d:\d\d:\d\d\.\d+) to (\d\d:\d\d:\d\d\.\d+)' } | Select-Object -First 1
    if (-not $line -or -not (Test-Path $clockLog)) {
        return
    }

    $null = $line -match 'ran (\d\d:\d\d:\d\d\.\d+) to (\d\d:\d\d:\d\d\.\d+)'
    $from = [TimeSpan]::Parse($Matches[1])
    $to = [TimeSpan]::Parse($Matches[2])
    $clocks = @()
    $states = @{}
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

# One run of the scene; a run that fails or passes its time limit is written and counted, and the next runs.
function Invoke-Scene([string] $name, [string] $program, [string[]] $arguments) {
    $began = Get-Date
    Write-Out "=== $name started $($began.ToString('HH:mm:ss')): points $($arguments -join ' ')"
    $stdout = "$Out.run.txt"
    $process = Start-Process -FilePath $program -ArgumentList (@('points') + $arguments) -NoNewWindow -PassThru `
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

function Publish([string] $tree, [string] $folder) {
    $from = Get-Date
    dotnet publish (Join-Path $tree 'benchmarks\HyperPlot.E2E') -c Release -r win-x64 -o $folder 2>&1 |
        ForEach-Object { "$_" } | Out-File -FilePath $Out -Append -Encoding utf8
    $exe = Join-Path $folder 'HyperPlot.E2E.exe'
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $exe) -or (Get-Item $exe).LastWriteTime -lt $from) {
        throw "publish of $tree failed with exit $LASTEXITCODE, or the executable is older than this run"
    }

    Write-Out "## $folder executable $((Get-Item $exe).Length) bytes"
    return $exe
}

# name, distribution, categories, what the categories set, selection
$scenes = @(
    @('1 part', 'uniform', 0, 'color', 'none'),
    @('8 parts', 'uniform', 8, 'color', 'none'),
    @('64 parts', 'uniform', 64, 'color', 'none'),
    @('6 shapes uniform', 'uniform', 6, 'shape', 'none'),
    @('6 shapes cluster', 'cluster', 6, 'shape', 'none'),
    @('1 part rectangle', 'uniform', 0, 'color', 'rectangle'),
    @('1 part lasso', 'uniform', 0, 'color', 'lasso')
)

Set-Content -Path $Out -Value "# r3c2 run (Issue #617), $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), head $(git -C $repo rev-parse --short HEAD), base $Base, rounds $Rounds" -Encoding utf8
$watcher = $null
$clocks = $null
try {
    $env:PATH += ';C:\Program Files (x86)\Microsoft Visual Studio\Installer'
    Write-Out '## publish'
    git -C $repo worktree add --detach $baseTree $Base 2>&1 | ForEach-Object { "$_" } | Out-File -FilePath $Out -Append -Encoding utf8
    $programs = @{ base = (Publish $baseTree $publishes.base); head = (Publish $repo $publishes.head) }
    $watcher = Start-Process pwsh -WindowStyle Hidden -PassThru -ArgumentList @(
        '-NoProfile', '-File', (Join-Path $repo 'benchmarks\HyperPlot.DataPath\watch-machine.ps1'), '-Log', $machineLog, '-Scene', 'HyperPlot.E2E'
    )
    $clocks = Start-Process nvidia-smi -WindowStyle Hidden -PassThru -ArgumentList @(
        '--query-gpu=timestamp,clocks.gr,pstate', '--format=csv', '-lms', '200', '-f', $clockLog
    )

    for ($round = 1; $round -le $Rounds; $round++) {
        # A, B, B, A, A, B: each side runs first as often as second.
        $sides = if (@(1, 4, 5, 8) -contains $round) { @('base', 'head') } else { @('head', 'base') }
        $order = if ($round % 2) { 0..($scenes.Count - 1) } else { ($scenes.Count - 1)..0 }
        foreach ($i in $order) {
            $scene = $scenes[$i]
            foreach ($side in $sides) {
                $name = "$($scene[0]) $side, round $round"
                $frames = "$Out.$($name -replace '[^A-Za-z0-9]+', '-').csv"
                Invoke-Scene $name $programs[$side] @(
                    '10000000', '6', '0.6', $scene[1], '600', "$($scene[2])", 'whole', $scene[3], 'Circle', 'blocks', 'draw', $frames, $scene[4]
                )
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

    foreach ($folder in $publishes.Values) {
        if (Test-Path $folder) {
            Remove-Item -LiteralPath $folder -Recurse -Force
        }
    }

    if (Test-Path $baseTree) {
        git -C $repo worktree remove $baseTree 2>&1 | ForEach-Object { "$_" } | Out-File -FilePath $Out -Append -Encoding utf8
    }

    $left = @($publishes.Values) + @($baseTree) | Where-Object { Test-Path $_ }
    Write-Out "## runs that failed: $failed; cleaned up $(Get-Date -Format 'HH:mm:ss'); left: $($left -join ', ')"
    Write-Out "DONE $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
}

exit $failed
