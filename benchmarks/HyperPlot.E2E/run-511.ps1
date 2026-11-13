# Times the wheel frame after hover frames (WheelAfterHoverScene) on the head of PR #511 and on the code before it (the
# merge base with origin/main, with this scene copied in), for the review of Issue #378's fix: a new long text now takes
# a grown entry of the full shaping cache, so the long texts a whole frame draws can be shaped again after a hover.
# Builds both with NativeAOT (Release, win-x64) into .dev/511-e2e-head and .dev/511-e2e-base of the main checkout; the
# base's worktree is .dev/511-base. Each round runs every case on both builds in a balanced order (A, B, B, A, then
# B, A, A, B), pinned to logical processor 2 and unpinned (.cursor/rules/bench.mdc), and logs the machine beside it.
# Usage (PowerShell, from any folder): ./benchmarks/HyperPlot.E2E/run-511.ps1 D:\...\r511.txt [-Rounds 3] [-Cycles 100]
# Writes every run's output, headed by its round, build, affinity, and case, to the file named; the machine's log goes to
# <file>.machine.log.
param(
    [Parameter(Mandatory, Position = 0)] [string] $Out,
    [int] $Rounds = 3,
    [int] $Cycles = 100,
    [int[]] $Labels = @(0, 4096),
    [int[]] $Hovers = @(0, 1, 8)
)

$ErrorActionPreference = 'Stop'
$head = (git -C $PSScriptRoot rev-parse --show-toplevel).Trim()
$root = Split-Path (git -C $head rev-parse --path-format=absolute --git-common-dir).Trim() -Parent
$base = Join-Path $root '.dev/511-base'
$scene = 'benchmarks/HyperPlot.E2E'
$Out = [IO.Path]::GetFullPath($Out)

# The base: the code before the PR, with the scene's files of the head (benchmarks/ is the same at the merge base but for
# them).
git -C $head fetch origin main
$baseCommit = (git -C $head merge-base HEAD origin/main).Trim()
if (-not (Test-Path $base)) {
    git -C $head worktree add --detach $base $baseCommit
}
else {
    # The worktree is this script's own: its only changes are the copies below, made again after the checkout.
    git -C $base checkout --force --detach $baseCommit
}

foreach ($file in @('WheelAfterHoverScene.cs', 'HoverScene.cs', 'Program.cs')) {
    Copy-Item (Join-Path $head "$scene/$file") (Join-Path $base "$scene/$file") -Force
}

# A copy that did not land measures the old code (.cursor/rules/bench.mdc): check both sides before building.
if (-not (Select-String -Quiet -Path (Join-Path $base "$scene/Program.cs") -Pattern 'wheel-after-hover')) {
    throw 'The base does not have the scene.'
}

if (Select-String -Quiet -Path (Join-Path $base 'src/HyperPlot.Core/Rendering/TextEngine.cs') -Pattern 'void BeginFrame') {
    throw 'The base already has the fix of Issue #378.'
}

if (-not (Select-String -Quiet -Path (Join-Path $head 'src/HyperPlot.Core/Rendering/TextEngine.cs') -Pattern 'void BeginFrame')) {
    throw 'The head does not have the fix of Issue #378.'
}

# The link step of NativeAOT finds vswhere.exe on this folder (.cursor/rules/bench.mdc).
$env:PATH = "C:\Program Files (x86)\Microsoft Visual Studio\Installer;$env:PATH"
$builds = [ordered]@{
    head = Join-Path $root '.dev/511-e2e-head'
    base = Join-Path $root '.dev/511-e2e-base'
}
foreach ($name in $builds.Keys) {
    $source = if ($name -eq 'head') { $head } else { $base }
    dotnet publish (Join-Path $source $scene) -c Release -r win-x64 -o $builds[$name]
    if ($LASTEXITCODE -ne 0) {
        throw "The $name build failed."
    }
}

$log = "$Out.machine.log"
$watch = Start-Process pwsh -PassThru -WindowStyle Hidden -ArgumentList @(
    '-NoProfile', '-File', (Join-Path $head 'benchmarks/HyperPlot.DataPath/watch-machine.ps1'),
    '-Log', $log, '-Scene', 'HyperPlot.E2E'
)
$run = "$Out.run.txt"
Set-Content $Out "PR #511 wheel-after-hover; head $((git -C $head rev-parse HEAD).Trim()), base $baseCommit; $(Get-Date -Format o)"

function Invoke-Scene([string] $build, [string] $affinity, [int] $labelCount, [int] $hoverCount, [int] $round) {
    Add-Content $Out "round=$round build=$build affinity=$affinity labels=$labelCount hovers=$hoverCount"
    $exe = Join-Path $builds[$build] 'HyperPlot.E2E.exe'
    $process = Start-Process $exe -PassThru -NoNewWindow -RedirectStandardOutput $run `
        -ArgumentList @('wheel-after-hover', "$labelCount", "$hoverCount", "$Cycles")
    try {
        if ($affinity -eq 'pinned') {
            $process.ProcessorAffinity = [IntPtr]4
        }

        # A run past its deadline is reported, not dropped (.cursor/rules/bench.mdc).
        if (-not $process.WaitForExit(600000)) {
            Add-Content $Out 'timeout after 600 s'
        }
        elseif ($process.ExitCode -ne 0) {
            Add-Content $Out "exit code $($process.ExitCode)"
        }
    }
    finally {
        if (-not $process.HasExited) {
            $process.Kill()
            $process.WaitForExit()
        }
    }

    Add-Content $Out (Get-Content $run)
}

try {
    for ($round = 1; $round -le $Rounds; $round++) {
        $order = if ($round % 2) { @('head', 'base', 'base', 'head') } else { @('base', 'head', 'head', 'base') }
        foreach ($labelCount in $Labels) {
            foreach ($hoverCount in $Hovers) {
                foreach ($affinity in @('pinned', 'unpinned')) {
                    foreach ($build in $order) {
                        Invoke-Scene $build $affinity $labelCount $hoverCount $round
                    }
                }
            }
        }
    }
}
finally {
    New-Item -ItemType File "$log.stop" -Force | Out-Null
    $null = $watch.WaitForExit(30000)
    if (Test-Path $run) {
        Remove-Item $run
    }
}
