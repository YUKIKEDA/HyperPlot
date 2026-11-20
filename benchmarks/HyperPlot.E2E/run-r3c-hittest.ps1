# Row R3c's measurement (Issue #526, design §9 "散布のヒットテストと選択（行 R3c、Issue #471）"): the hit test and the selection
# scenes of this project on a NativeAOT build. The figures the requirements wait for come first: P-6 (the mouse alone over
# 1e7 selected raw points) and P-5 (the hit test at 1e9 points, uniform, then clustered); then the selection over a density
# whose cells are fills (a lasso over the panel) and over one whose cells have tiles (docs/benchmarks/r3c-hittest.md).
# Usage, from PowerShell 7: pwsh -NoProfile -File benchmarks/HyperPlot.E2E/run-r3c-hittest.ps1 <output file> [-Pilot]
# -Pilot runs every step small (1e7 points, 60 frames, one round) to check the script before the machine is taken.
# The machine is shared and no run waits for it to be quiet (.cursor/rules/bench.mdc): each scene prints the cores the other
# processes used. The machine log and the GPU clock log go next to the output file (<output>.machine.log,
# <output>.clocks.csv). Each step's start, end, and exit code go to .dev/out/queue.log with the prefix r3c526; the first
# step that fails or passes its time limit stops the rest. The data (8 GB a distribution at 1e9, and the index's copy of
# 12 GB while a scene runs) goes under .dev/ and is deleted at the end with the published build.
param(
    [Parameter(Mandatory)] [string] $Out,
    [switch] $Pilot
)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
# The main checkout's .dev/, also from a worktree under it: the parent of the shared .git folder.
$dev = Join-Path (Split-Path -Parent (Resolve-Path (git -C $repo rev-parse --path-format=absolute --git-common-dir))) '.dev'
$data = Join-Path $dev 'r3c-data526'
$publish = Join-Path $dev 'r3c-pub526'
$queue = Join-Path $dev 'out\queue.log'
$machineLog = "$Out.machine.log"
$clockLog = "$Out.clocks.csv"
$exe = Join-Path $publish 'HyperPlot.E2E.exe'
$stopped = $false

function Write-Out([string] $text) {
    $text | Out-File -FilePath $Out -Append -Encoding utf8
}

function Write-Queue([string] $text) {
    "r3c526 $text $((Get-Date).ToString('HH:mm:ss'))" | Out-File -FilePath $queue -Append -Encoding utf8
}

# One step: the scene with a time limit. A step that fails, or that is stopped at its limit (no result), stops the queue.
function Invoke-Step([string] $name, [int] $minutes, [string[]] $arguments) {
    if ($script:stopped) {
        Write-Out "=== $name not run: an earlier step stopped the queue"
        return
    }

    $began = Get-Date
    Write-Queue "$name start"
    Write-Out "=== $name started $($began.ToString('HH:mm:ss')): $($arguments -join ' ')"
    $stdout = "$Out.step.out"
    $stderr = "$Out.step.err"
    $process = Start-Process -FilePath $exe -ArgumentList $arguments -NoNewWindow -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    $null = $process.Handle
    $code = 'timeout'
    if ($process.WaitForExit($minutes * 60000)) {
        $code = $process.ExitCode
    }
    else {
        Stop-Process -Id $process.Id -Force
        $process.WaitForExit()
    }

    Get-Content -LiteralPath $stdout, $stderr | Out-File -FilePath $Out -Append -Encoding utf8
    Remove-Item -LiteralPath $stdout, $stderr -Force
    Write-Out "=== $name ended $((Get-Date).ToString('HH:mm:ss')) after $([math]::Round(((Get-Date) - $began).TotalSeconds, 1)) s, exit $code"
    Write-Queue "$name exit $code"
    if ("$code" -ne '0') {
        $script:stopped = $true
    }
}

Set-Content -Path $Out -Value "# hit test and selection run (Issue #526), $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), commit $(git -C $repo rev-parse --short HEAD), pilot $($Pilot.IsPresent)" -Encoding utf8
$watcher = $null
$clocks = $null
try {
    # No executable of an earlier build stays to be measured when this publish fails.
    if (Test-Path $publish) {
        Remove-Item -LiteralPath $publish -Recurse -Force
    }

    $env:PATH += ';C:\Program Files (x86)\Microsoft Visual Studio\Installer'
    Write-Queue 'publish start'
    Write-Out '## publish'
    dotnet publish (Join-Path $repo 'benchmarks\HyperPlot.E2E') -c Release -r win-x64 -o $publish 2>&1 |
        ForEach-Object { "$_" } | Out-File -FilePath $Out -Append -Encoding utf8
    $published = $LASTEXITCODE
    Write-Queue "publish exit $published"
    if ($published -ne 0 -or -not (Test-Path $exe)) {
        throw "publish failed with exit $published"
    }

    Write-Out "## executable $((Get-Item $exe).Length) bytes, written $((Get-Item $exe).LastWriteTime.ToString('HH:mm:ss'))"
    New-Item -ItemType Directory -Force -Path $data | Out-Null
    $watcher = Start-Process pwsh -WindowStyle Hidden -PassThru -ArgumentList @(
        '-NoProfile', '-File', (Join-Path $repo 'benchmarks\HyperPlot.DataPath\watch-machine.ps1'), '-Log', $machineLog, '-Scene', 'HyperPlot.E2E'
    )
    $clocks = Start-Process nvidia-smi -WindowStyle Hidden -PassThru -ArgumentList @(
        '--query-gpu=timestamp,clocks.gr,pstate', '--format=csv', '-lms', '200', '-f', $clockLog
    )

    $many = $Pilot ? '1e7' : '1e9'
    $frames = $Pilot ? '60' : '300'
    $rounds = $Pilot ? '1' : '3'
    Invoke-Step 'selection-points' 10 @('selection', '1e7', 'points', $frames, $rounds)
    Invoke-Step 'hittest-uniform' 25 @('hittest', $data, $many, 'uniform')
    Invoke-Step 'hittest-clustered' 40 @('hittest', $data, $many, 'cluster', '6')
    Invoke-Step 'selection-fills' 5 @('selection', '200000', 'density', $frames, $rounds)
    Invoke-Step 'selection-tiles' 5 @('selection', '1000000', 'density', $frames, $rounds)
}
catch {
    Write-Out "## FAILED: $_"
    $stopped = $true
}
finally {
    if ($null -ne $watcher) {
        New-Item -ItemType File -Force -Path "$machineLog.stop" | Out-Null
        $watcher.WaitForExit(30000) | Out-Null
    }

    if ($null -ne $clocks -and -not $clocks.HasExited) {
        Stop-Process -Id $clocks.Id -Force
    }

    foreach ($folder in @($data, $publish)) {
        if (Test-Path $folder) {
            Remove-Item -LiteralPath $folder -Recurse -Force
        }
    }

    Write-Out "## stopped early: $stopped; cleaned up $(Get-Date -Format 'HH:mm:ss'); left: $(@($data, $publish) | Where-Object { Test-Path $_ } | Join-String -Separator ', ')"
    Write-Out "DONE $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
    Write-Queue 'done'
}
