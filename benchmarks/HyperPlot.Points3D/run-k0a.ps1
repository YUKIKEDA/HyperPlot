# SPIKE (row K0a, Issue #477): the measurements of the 3D points prototype.
# Usage (PowerShell 7):
#   ./benchmarks/HyperPlot.Points3D/run-k0a.ps1 -Out <folder outside the repository> [-Rounds 3] [-OctreeOnly]
#     [-Sizes 1e7,1e8] [-Grids 128,64,32] [-TimeoutMinutes 20]
# Publishes the NativeAOT Release build (adds the Visual Studio Installer folder to PATH for vswhere when missing), then,
# with the GPU's clock (clocks.csv) and the machine's state (machine.log) logged beside them:
#   1. The octree (hypotheses 2 to 4, drawing method A by the owner's decision of 2026-11-14), the figures a decision waits
#      for first:
#      a. octree-hash-*: the build at 1e7 points on 8 workers and on 1, whose hashes of the table and the copy must agree
#         (invariant 11, decision 7).
#      b. octree-build-*: the build, the nodes, the memory, and the hit tests of K-7 (pinned to logical processor 2), in
#         rounds, the grids in the reverse order on even rounds.
#      c. octree-orbit-*: the orbit through the octree under the point budgets, in rounds, the grids and the variants in
#         the reverse order on even rounds.
#   2. Unless -OctreeOnly: the two drawing methods (Result of Issue #477): in each round, A's discs of 2 pixels, A's
#      1-pixel squares, and B, in a balanced order (A disc, A square, B, B, A square, A disc), then the determinism check
#      twice on WARP and once on the hardware.
# Every run is a process of its own with a timeout (-TimeoutMinutes), its standard output in <Out>\<name>.txt and its
# standard error in <Out>\<name>.err.txt. A run that fails or times out is listed in <Out>\summary.txt, and the script
# ends with exit code 1; a failed publish stops it at once.
param(
    [Parameter(Mandatory = $true)] [string] $Out,
    [int] $Rounds = 3,
    [switch] $OctreeOnly,
    [string[]] $Sizes = @('1e7', '1e8'),
    [int[]] $Grids = @(128, 64, 32),
    [int] $TimeoutMinutes = 20
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
New-Item -ItemType Directory -Force $Out | Out-Null
$folder = (Resolve-Path $Out).Path
$failures = [System.Collections.Generic.List[string]]::new()
$notes = [System.Collections.Generic.List[string]]::new()

$installer = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer'
if (-not (($env:PATH -split ';') -contains $installer)) {
    $env:PATH += ";$installer"
}

# Start-Process joins its arguments with spaces: an argument with a space is quoted here.
function ConvertTo-Argument([string] $text) {
    if ($text -match '\s') { return "`"$text`"" }
    return $text
}

# Runs one step as a process of its own and waits for it at most $Minutes; returns whether it ended with exit code 0.
function Invoke-Step {
    param(
        [Parameter(Mandatory = $true)] [string] $Name,
        [Parameter(Mandatory = $true)] [string] $FilePath,
        [Parameter(Mandatory = $true)] [string[]] $Arguments,
        [int] $Minutes = $TimeoutMinutes
    )

    $log = Join-Path $folder "$Name.txt"
    $errorLog = Join-Path $folder "$Name.err.txt"
    [string[]] $quoted = @($Arguments | ForEach-Object { ConvertTo-Argument $_ })
    Write-Host "$(Get-Date -Format 'HH:mm:ss') $Name"
    $process = Start-Process -FilePath $FilePath -ArgumentList $quoted -WorkingDirectory $repo -NoNewWindow -PassThru `
        -RedirectStandardOutput $log -RedirectStandardError $errorLog
    # Reading the handle keeps it, so that the exit code can be read after the process ends.
    $null = $process.Handle
    if (-not $process.WaitForExit($Minutes * 60 * 1000)) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        $process.WaitForExit()
        $failures.Add("${Name}: timed out after $Minutes min, stopped ($log)")
        Write-Warning "${Name}: timed out after $Minutes min"
        return $false
    }

    $process.WaitForExit()
    if ($process.ExitCode -ne 0) {
        $failures.Add("${Name}: exit code $($process.ExitCode) ($log)")
        Write-Warning "${Name}: exit code $($process.ExitCode)"
        return $false
    }

    return $true
}

# The grids, or the variants, in the reverse order on even rounds, so that each runs first as often as last.
function Get-RoundOrder([object[]] $items, [int] $round) {
    if ($round % 2 -eq 0) { return @($items[($items.Length - 1)..0]) }
    return @($items)
}

$publish = Join-Path $folder 'publish'
[string[]] $publishArguments = @('publish', (Join-Path $repo 'benchmarks\HyperPlot.Points3D'), '-c', 'Release', '-r', 'win-x64', '-o', $publish)
if (-not (Invoke-Step -Name 'publish' -FilePath 'dotnet' -Arguments $publishArguments -Minutes 30)) {
    $failures | Set-Content -Path (Join-Path $folder 'summary.txt')
    throw "The publish failed: $(Join-Path $folder 'publish.txt')"
}

$exe = Join-Path $publish 'HyperPlot.Points3D.exe'
$machineLog = Join-Path $folder 'machine.log'
$watchScript = Join-Path $repo 'benchmarks\HyperPlot.DataPath\watch-machine.ps1'
$smi = Start-Process nvidia-smi -ArgumentList "--query-gpu=timestamp,clocks.gr,pstate --format=csv -lms 200" `
    -RedirectStandardOutput (Join-Path $folder 'clocks.csv') -WindowStyle Hidden -PassThru
[string[]] $watchArguments = @('-NoProfile', '-File', (ConvertTo-Argument $watchScript), '-Log', (ConvertTo-Argument $machineLog), '-Seconds', '5', '-Scene', 'HyperPlot.Points3D')
$watch = Start-Process pwsh -ArgumentList $watchArguments -WindowStyle Hidden -PassThru
try {
    # 1a. The build does not depend on the workers.
    foreach ($dist in 'uniform', 'cluster') {
        foreach ($grid in $Grids) {
            $hashes = @()
            foreach ($workers in 8, 1) {
                $name = "octree-hash-1e7-$dist-g$grid-w$workers"
                [string[]] $arguments = @('octree-build', '1e7', $dist, '--grid', "$grid", '--workers', "$workers", '--hash', '--hits', '0')
                if (Invoke-Step -Name $name -FilePath $exe -Arguments $arguments) {
                    $line = Select-String -Path (Join-Path $folder "$name.txt") -Pattern '^hash=' | Select-Object -First 1
                    $hash = 'none'
                    if ($null -ne $line) {
                        $hash = $line.Line
                    }

                    $hashes += $hash
                }
            }

            if ($hashes.Count -eq 2 -and $hashes[0] -eq $hashes[1] -and $hashes[0] -ne 'none') {
                $notes.Add("octree build 1e7 $dist grid $grid`: 8 and 1 workers give the same $($hashes[0])")
            }
            else {
                $failures.Add("octree build 1e7 $dist grid $grid`: the hashes on 8 and 1 workers differ or are missing: $($hashes -join ' / ')")
            }
        }
    }

    # 1b. The build, the nodes, the memory, and the hit tests.
    for ($round = 1; $round -le $Rounds; $round++) {
        foreach ($size in $Sizes) {
            foreach ($dist in 'uniform', 'cluster') {
                foreach ($grid in (Get-RoundOrder $Grids $round)) {
                    [string[]] $arguments = @('octree-build', $size, $dist, '--grid', "$grid", '--pin')
                    $null = Invoke-Step -Name "octree-build-r$round-$size-$dist-g$grid" -FilePath $exe -Arguments $arguments
                }
            }
        }
    }

    # 1c. The orbit through the octree under the budgets.
    for ($round = 1; $round -le $Rounds; $round++) {
        foreach ($size in $Sizes) {
            foreach ($dist in 'uniform', 'cluster') {
                foreach ($grid in (Get-RoundOrder $Grids $round)) {
                    [string[]] $arguments = @('octree-orbit', $size, $dist, '--grid', "$grid")
                    if ($round % 2 -eq 0) {
                        $arguments += '--reverse'
                    }

                    $null = Invoke-Step -Name "octree-orbit-r$round-$size-$dist-g$grid" -FilePath $exe -Arguments $arguments
                }
            }
        }
    }

    # 2. The two drawing methods, as the Result of Issue #477 measured them.
    if (-not $OctreeOnly) {
        for ($round = 1; $round -le $Rounds; $round++) {
            foreach ($count in '1e7', '1e6') {
                foreach ($dist in 'uniform', 'cluster') {
                    $variants = @(
                        @{ Name = 'markers-disc2'; Method = 'markers'; Extra = @() },
                        @{ Name = 'markers-square1'; Method = 'markers'; Extra = @('--footprint', '1') },
                        @{ Name = 'compute-1'; Method = 'compute'; Extra = @() }
                    )
                    foreach ($at in 0, 1, 2, 2, 1, 0) {
                        $variant = $variants[$at]
                        $suffix = [guid]::NewGuid().ToString('N').Substring(0, 4)
                        [string[]] $arguments = @('run', $variant.Method, $count, $dist) + $variant.Extra
                        $null = Invoke-Step -Name "r$round-$($variant.Name)-$count-$dist-$suffix" -FilePath $exe -Arguments $arguments
                    }
                }
            }
        }

        foreach ($count in '1e6', '1e7') {
            foreach ($dist in 'uniform', 'cluster') {
                foreach ($run in 1, 2) {
                    $null = Invoke-Step -Name "det-warp-$count-$dist-run$run" -FilePath $exe -Arguments @('determinism', $count, $dist)
                }

                $null = Invoke-Step -Name "det-hw-$count-$dist" -FilePath $exe -Arguments @('determinism', $count, $dist, '--hardware')
            }
        }
    }
}
finally {
    Stop-Process -Id $smi.Id -ErrorAction SilentlyContinue
    New-Item -ItemType File -Force "$machineLog.stop" | Out-Null
    if (-not $watch.WaitForExit(60 * 1000)) {
        Stop-Process -Id $watch.Id -Force -ErrorAction SilentlyContinue
    }
}

$summary = @("ended $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')", "failures: $($failures.Count)") + $failures + @('notes:') + $notes
$summary | Set-Content -Path (Join-Path $folder 'summary.txt')
if ($failures.Count -gt 0) {
    Write-Warning "$($failures.Count) step(s) failed: $(Join-Path $folder 'summary.txt')"
    exit 1
}
