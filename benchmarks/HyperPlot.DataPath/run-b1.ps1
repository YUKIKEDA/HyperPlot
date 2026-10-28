# Runs the data-path scenes of row B1 (design §9, docs/benchmarks/b1-data-layer.md) on 1e9 points, three rounds, each pair
# of compared sides in a balanced order (.cursor/rules/bench.mdc). Publish first:
#   dotnet publish benchmarks/HyperPlot.DataPath -c Release -r win-x64 -o .dev/b1-datapath
# Usage: ./benchmarks/HyperPlot.DataPath/run-b1.ps1 -File D:\...\.dev\b1-series.bin
# The file (4 GB) is written by the scenes and deleted at the end.
param(
    [Parameter(Mandatory)] [string] $File,
    [string] $Exe = '.dev/b1-datapath/HyperPlot.DataPath.exe',
    [int] $Rounds = 3
)

$ErrorActionPreference = 'Stop'
try {
    & $Exe generate --file $File
    & $Exe hash --file $File
    & $Exe generate --file $File
    & $Exe hash --file $File
    for ($round = 1; $round -le $Rounds; $round++) {
        Write-Output "round=$round"
        $buildOrder = if ($round % 2) { @('8', 'default') } else { @('default', '8') }
        foreach ($workers in $buildOrder) {
            if ($workers -eq '8') { & $Exe build --file $File --cold --workers 8 } else { & $Exe build --file $File --cold }
        }

        $viewOrder = if ($round % 2) { @('full', 'zoom') } else { @('zoom', 'full') }
        foreach ($view in $viewOrder) {
            if ($view -eq 'zoom') { & $Exe provisional --file $File --cold --zoom } else { & $Exe provisional --file $File --cold }
        }

        $frameOrder = if ($round % 2) { @('m4', 'reads') } else { @('reads', 'm4') }
        foreach ($mode in $frameOrder) {
            if ($mode -eq 'reads') { & $Exe cold-frame --file $File --reads-only } else { & $Exe cold-frame --file $File }
        }
    }
}
finally {
    if (Test-Path $File) {
        Remove-Item $File
    }
}
