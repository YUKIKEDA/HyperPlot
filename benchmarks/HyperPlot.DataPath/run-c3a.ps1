# Runs the spatial tile index scene of row C3a (design §9, docs/benchmarks/c3a-spatial-index.md) on 1e9 points of each
# distribution: three rounds, each pair of compared sides in a balanced order (.cursor/rules/bench.mdc), then the build at
# each RowsPerUnit with 8 workers over warm files, then Dispose during the writing. Publish first:
#   dotnet publish benchmarks/HyperPlot.DataPath -c Release -r win-x64 -o .dev/c3a-datapath
# Usage: ./benchmarks/HyperPlot.DataPath/run-c3a.ps1 -File D:\...\.dev\c3a-points
# The x and y files (4 GB each) are written by the scenes and deleted at the end. Each build puts its copy (12 GB) in a
# temporary file of Path.GetTempPath(), which the index deletes.
param(
    [Parameter(Mandatory)] [string] $File,
    [string] $Exe = '.dev/c3a-datapath/HyperPlot.DataPath.exe',
    [int] $Rounds = 3,
    [int[]] $RowsPerUnit = @(16384, 65536, 262144, 1048576)
)

$ErrorActionPreference = 'Stop'
try {
    foreach ($distribution in @('uniform', 'clustered')) {
        # The same seed gives the same bytes: write twice and compare the hashes.
        & $Exe generate-points --file $File --distribution $distribution
        & $Exe hash-points --file $File
        & $Exe generate-points --file $File --distribution $distribution
        & $Exe hash-points --file $File
    }

    for ($round = 1; $round -le $Rounds; $round++) {
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

    foreach ($distribution in @('uniform', 'clustered')) {
        & $Exe generate-points --file $File --distribution $distribution
        $order = if ($distribution -eq 'uniform') { $RowsPerUnit } else { [array]::Reverse($RowsPerUnit); $RowsPerUnit }
        foreach ($rows in $order) {
            & $Exe spatial --file $File --distribution $distribution --workers 8 --rows-per-unit $rows
        }

        # Dispose at the start of the writing and while its units run, eight times each.
        & $Exe spatial-dispose --file $File --distribution $distribution --workers 8
        & $Exe spatial-dispose --file $File --distribution $distribution
    }
}
finally {
    foreach ($path in @("$File.x", "$File.y")) {
        if (Test-Path $path) {
            Remove-Item $path
        }
    }
}
