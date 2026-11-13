# Times the wheel frame after hover frames (WheelAfterHoverScene) on three builds, for the review of PR #511 (Issue #378):
#   base:   the code before the PR (the merge base with origin/main);
#   oldfix: PR #511's first fix (commit fef5398: a new long text takes a grown entry of the full shaping cache, so a whole
#           frame after a hover can shape every long text it draws again);
#   head:   the head (the hover's and the measurement's boxes shape their text outside the cache, OwnedText).
# Each gets the head's scene files. Builds each with NativeAOT (Release, win-x64) into .dev/511-e2e-<build> of the main
# checkout; the base's worktree is .dev/511-base (kept), the old fix's .dev/511-oldfix (removed at the end). Each round
# runs every case on the three builds in a balanced order (A, B, C, C, B, A; the first build turns from round to round),
# pinned to logical processor 2 and unpinned (.cursor/rules/bench.mdc), and logs the machine beside it.
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
$scene = 'benchmarks/HyperPlot.E2E'
$engine = 'src/HyperPlot.Core/Rendering/TextEngine.cs'
$owned = 'src/HyperPlot.Core/Rendering/OwnedText.cs'
$Out = [IO.Path]::GetFullPath($Out)
$oldFixCommit = 'fef5398'
$trees = [ordered]@{
    head = $head
    oldfix = Join-Path $root '.dev/511-oldfix'
    base = Join-Path $root '.dev/511-base'
}

git -C $head fetch origin main
$baseCommit = (git -C $head merge-base HEAD origin/main).Trim()
$commits = @{ base = $baseCommit; oldfix = $oldFixCommit }

function Test-Has([string] $build, [string] $path, [string] $pattern) {
    $file = Join-Path $trees[$build] $path
    (Test-Path $file) -and (-not $pattern -or (Select-String -Quiet -Path $file -Pattern $pattern))
}

$watch = $null
$run = "$Out.run.txt"
$log = "$Out.machine.log"
try {
    foreach ($build in @('base', 'oldfix')) {
        $tree = $trees[$build]
        if (-not (Test-Path $tree)) {
            git -C $head worktree add --detach $tree $commits[$build]
        }
        else {
            # The worktree is this script's own: its only changes are the copies below, made again after the checkout.
            git -C $tree checkout --force --detach $commits[$build]
        }

        foreach ($file in @('WheelAfterHoverScene.cs', 'HoverScene.cs', 'Program.cs')) {
            Copy-Item (Join-Path $head "$scene/$file") (Join-Path $tree "$scene/$file") -Force
        }
    }

    # A copy or a checkout that did not land measures other code (.cursor/rules/bench.mdc): check each tree before building.
    foreach ($build in $trees.Keys) {
        if (-not (Test-Has $build "$scene/Program.cs" 'wheel-after-hover')) {
            throw "The $build tree does not have the scene."
        }
    }

    if ((Test-Has 'base' $engine 'void BeginFrame') -or (Test-Has 'base' $owned $null)) {
        throw 'The base tree has a fix of Issue #378.'
    }

    if (-not (Test-Has 'oldfix' $engine 'void BeginFrame') -or (Test-Has 'oldfix' $owned $null)) {
        throw 'The oldfix tree is not the first fix of PR #511.'
    }

    if ((Test-Has 'head' $engine 'void BeginFrame') -or -not (Test-Has 'head' $owned $null)) {
        throw 'The head tree is not the fix with OwnedText.'
    }

    # The link step of NativeAOT finds vswhere.exe on this folder (.cursor/rules/bench.mdc).
    $env:PATH = "C:\Program Files (x86)\Microsoft Visual Studio\Installer;$env:PATH"
    $builds = [ordered]@{}
    foreach ($build in $trees.Keys) {
        $builds[$build] = Join-Path $root ".dev/511-e2e-$build"
        dotnet publish (Join-Path $trees[$build] $scene) -c Release -r win-x64 -o $builds[$build]
        if ($LASTEXITCODE -ne 0) {
            throw "The $build build failed."
        }
    }

    $watch = Start-Process pwsh -PassThru -WindowStyle Hidden -ArgumentList @(
        '-NoProfile', '-File', (Join-Path $head 'benchmarks/HyperPlot.DataPath/watch-machine.ps1'),
        '-Log', $log, '-Scene', 'HyperPlot.E2E'
    )
    Set-Content $Out ("PR #511 wheel-after-hover; head $((git -C $head rev-parse HEAD).Trim()), oldfix $oldFixCommit, " +
        "base $baseCommit; $(Get-Date -Format o)")

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

    $names = @($trees.Keys)
    for ($round = 1; $round -le $Rounds; $round++) {
        # A, B, C, C, B, A, with each build first in one round of three.
        $first = @(0..2 | ForEach-Object { $names[($_ + $round - 1) % 3] })
        $order = $first + @($first[2], $first[1], $first[0])
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
    if ($watch) {
        New-Item -ItemType File "$log.stop" -Force | Out-Null
        $null = $watch.WaitForExit(30000)
    }

    if (Test-Path $run) {
        Remove-Item $run
    }

    if (Test-Path $trees['oldfix']) {
        git -C $head worktree remove --force $trees['oldfix']
    }
}
