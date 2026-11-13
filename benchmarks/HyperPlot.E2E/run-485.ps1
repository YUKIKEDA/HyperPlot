# Row R3b1's measurement (Issue #485, design §9 "密度と切り替え（行 R3b1、Issue #462）"): the density scene of this project on
# a NativeAOT build. The figures the requirements wait for come first: P-15 (the first display on files just written
# unbuffered, three rounds) and P-3 (the zoom script drawn as density, three rounds), the distributions alternated; then the
# zoom with Draw=Auto at 1e9 (the switch), the switch at 1e8, and the 8 categories at 1e8 (docs/benchmarks/r3b1-density.md).
# Usage, from PowerShell 7 when the machine is free: pwsh -NoProfile -File benchmarks/HyperPlot.E2E/run-485.ps1 <output file>
# The machine log and the GPU clock log go next to the output file (<output>.machine.log, <output>.clocks.csv). The data
# (8 GB a distribution at 1e9) goes under .dev/ and is deleted at the end with the published build.
param(
    [Parameter(Mandatory)] [string] $Out
)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
# The main checkout's .dev/, also from a worktree under it: the parent of the shared .git folder.
$dev = Join-Path (Split-Path -Parent (Resolve-Path (git -C $repo rev-parse --path-format=absolute --git-common-dir))) '.dev'
$data9 = Join-Path $dev 'r3b1-data9'
$data8 = Join-Path $dev 'r3b1-data'
$publish = Join-Path $dev 'r3b1-pub485'
$machineLog = "$Out.machine.log"
$clockLog = "$Out.clocks.csv"
$exe = Join-Path $publish 'HyperPlot.E2E.exe'
$failed = 0

function Write-Out([string] $text) {
    $text | Out-File -FilePath $Out -Append -Encoding utf8
}

# One run of the scene; a run that fails is written and counted, and the next runs (.cursor/rules/bench.mdc).
function Invoke-Scene([string] $name, [string[]] $arguments) {
    $began = Get-Date
    Write-Out "=== $name started $($began.ToString('HH:mm:ss')): density $($arguments -join ' ')"
    & $exe density @arguments 2>&1 | ForEach-Object { "$_" } | Out-File -FilePath $Out -Append -Encoding utf8
    $code = $LASTEXITCODE
    Write-Out "=== $name ended $((Get-Date).ToString('HH:mm:ss')) after $([math]::Round(((Get-Date) - $began).TotalSeconds, 1)) s, exit $code"
    if ($code -ne 0) {
        $script:failed++
    }
}

# Deletes the 1e9 files of a distribution, so that the scene writes them again unbuffered: not in the page cache.
function Remove-Points([string] $distribution) {
    foreach ($axis in @('x', 'y')) {
        $path = Join-Path $data9 "density-$distribution-1000000000.$axis"
        if (Test-Path $path) {
            Remove-Item -LiteralPath $path -Force
        }
    }
}

# The distributions of a round: the first round starts with uniform, the next with clustered.
function Get-Order([int] $round) {
    if ($round % 2) { @('uniform', 'clustered') } else { @('clustered', 'uniform') }
}

Set-Content -Path $Out -Value "# density run (Issue #485), $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), commit $(git -C $repo rev-parse --short HEAD)" -Encoding utf8
$watcher = $null
$clocks = $null
try {
    $env:PATH += ';C:\Program Files (x86)\Microsoft Visual Studio\Installer'
    Write-Out '## publish'
    dotnet publish (Join-Path $repo 'benchmarks\HyperPlot.E2E') -c Release -r win-x64 -o $publish 2>&1 |
        ForEach-Object { "$_" } | Out-File -FilePath $Out -Append -Encoding utf8
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $exe)) {
        throw "publish failed with exit $LASTEXITCODE"
    }

    Write-Out "## executable $((Get-Item $exe).Length) bytes"
    New-Item -ItemType Directory -Force -Path $data9, $data8 | Out-Null
    $watcher = Start-Process pwsh -WindowStyle Hidden -PassThru -ArgumentList @(
        '-NoProfile', '-File', (Join-Path $repo 'benchmarks\HyperPlot.DataPath\watch-machine.ps1'), '-Log', $machineLog, '-Scene', 'HyperPlot.E2E'
    )
    $clocks = Start-Process nvidia-smi -WindowStyle Hidden -PassThru -ArgumentList @(
        '--query-gpu=timestamp,clocks.gr,pstate', '--format=csv', '-lms', '200', '-f', $clockLog
    )

    # P-15 and P-3 at 1e9: "first" on files written now (cold), then the zoom drawn as density on the same files (warm;
    # its first display is P-15's warm figure).
    for ($round = 1; $round -le 3; $round++) {
        foreach ($distribution in Get-Order $round) {
            Remove-Points $distribution
            Invoke-Scene "1e9 $distribution first, round $round" @($data9, '1e9', $distribution, 'first', '600')
            Invoke-Scene "1e9 $distribution zoom density, round $round" @($data9, '1e9', $distribution, 'zoom', '600', 'density')
        }
    }

    # The switch at 1e9: the same zoom with Draw=Auto, once a distribution (warm files).
    foreach ($distribution in @('uniform', 'clustered')) {
        Invoke-Scene "1e9 $distribution zoom auto" @($data9, '1e9', $distribution, 'zoom', '600', 'auto')
    }

    Remove-Points 'uniform'
    Remove-Points 'clustered'

    # The switch and the 8 categories at 1e8, two rounds each, alternated.
    foreach ($mode in @('switch', 'categories')) {
        for ($round = 1; $round -le 2; $round++) {
            foreach ($distribution in Get-Order $round) {
                Invoke-Scene "1e8 $distribution $mode, round $round" @($data8, '1e8', $distribution, $mode, '600')
            }
        }
    }
}
catch {
    Write-Out "## FAILED: $_"
}
finally {
    if ($null -ne $watcher) {
        New-Item -ItemType File -Force -Path "$machineLog.stop" | Out-Null
        $watcher.WaitForExit(30000) | Out-Null
    }

    if ($null -ne $clocks -and -not $clocks.HasExited) {
        Stop-Process -Id $clocks.Id -Force
    }

    foreach ($folder in @($data9, $data8, $publish)) {
        if (Test-Path $folder) {
            Remove-Item -LiteralPath $folder -Recurse -Force
        }
    }

    Write-Out "## runs that failed: $failed; cleaned up $(Get-Date -Format 'HH:mm:ss'); left: $(@($data9, $data8, $publish) | Where-Object { Test-Path $_ } | Join-String -Separator ', ')"
    Write-Out "DONE $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
}
