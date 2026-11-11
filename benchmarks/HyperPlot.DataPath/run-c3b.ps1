# Runs the spatial tile index scene of row C3b (design §9, docs/benchmarks/c3b-density-tiles.md) on 1e9 points of each
# distribution, in the order of what the row decides (.cursor/rules/bench.mdc): one round of the build with its density
# tiles (the time from the full snapshot to the tiles, the tiles' bytes), then the build at the other values of
# DensitySplit with 8 workers over warm files, then two more rounds. Run watch-machine.ps1 beside it. Publish first:
#   dotnet publish benchmarks/HyperPlot.DataPath -c Release -r win-x64 -o .dev/c3b-datapath
# Usage: ./benchmarks/HyperPlot.DataPath/run-c3b.ps1 -File D:\...\.dev\c3b-points
# The x and y files (4 GB each) are written by the scenes and deleted at the end. Each build puts its copy (12 GB) in a
# temporary file of Path.GetTempPath(), which the index deletes.
param(
    [Parameter(Mandatory)] [string] $File,
    [string] $Exe = '.dev/c3b-datapath/HyperPlot.DataPath.exe',
    [int] $Rounds = 3,
    [long[]] $DensitySplit = @(131072, 524288)
)

$ErrorActionPreference = 'Stop'

function Invoke-Round([int] $round) {
    Write-Output "round=$round"
    $distributions = if ($round % 2) { @('uniform', 'clustered') } else { @('clustered', 'uniform') }
    foreach ($distribution in $distributions) {
        $workerOrder = if ($round % 2) { @('8', 'default') } else { @('default', '8') }
        foreach ($workers in $workerOrder) {
            if ($workers -eq '8') {
                & $Exe spatial --file $File --distribution $distribution --cold --workers 8
            }
            else {
                & $Exe spatial --file $File --distribution $distribution --cold
            }
        }
    }
}

try {
    Invoke-Round 1

    foreach ($distribution in @('uniform', 'clustered')) {
        & $Exe generate-points --file $File --distribution $distribution
        & $Exe hash-points --file $File
        $order = if ($distribution -eq 'uniform') { $DensitySplit } else { [array]::Reverse($DensitySplit); $DensitySplit }
        foreach ($split in $order) {
            & $Exe spatial --file $File --distribution $distribution --workers 8 --density-split $split
        }
    }

    for ($round = 2; $round -le $Rounds; $round++) {
        Invoke-Round $round
    }
}
finally {
    foreach ($path in @("$File.x", "$File.y")) {
        if (Test-Path $path) {
            Remove-Item $path
        }
    }
}
