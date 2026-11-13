# SPIKE (row K0a, Issue #477): the full measurement of the two ways to draw 3D points.
# Usage (PowerShell, from the repository root, with C:\Program Files (x86)\Microsoft Visual Studio\Installer on PATH):
#   ./benchmarks/HyperPlot.Points3D/run-k0a.ps1 -Out <folder outside the repository> [-Rounds 3]
# Publishes the NativeAOT Release build, then runs each scene in rounds, three variants in a balanced order inside each
# round (A disc, A square, B, B, A square, A disc): A's discs of 2 pixels as row R3a draws markers, A's 1-pixel squares
# (the pixels B covers), and B, with the GPU's clock logged beside them; then the determinism check twice on WARP and once on the
# hardware. Every run's output is kept in -Out.
param(
    [Parameter(Mandatory = $true)] [string] $Out,
    [int] $Rounds = 3
)

$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force $Out | Out-Null
$publish = Join-Path $Out "publish"
dotnet publish benchmarks/HyperPlot.Points3D -c Release -r win-x64 -o $publish
if ($LASTEXITCODE -ne 0) { throw "publish failed" }
$exe = Join-Path $publish "HyperPlot.Points3D.exe"

$smi = Start-Process nvidia-smi -ArgumentList "--query-gpu=timestamp,clocks.gr,pstate --format=csv -lms 200" `
    -RedirectStandardOutput (Join-Path $Out "clocks.csv") -WindowStyle Hidden -PassThru
try {
    for ($round = 1; $round -le $Rounds; $round++) {
        foreach ($count in "1e7", "1e6") {
            foreach ($dist in "uniform", "cluster") {
                $variants = @(
                    @{ Name = "markers-disc2"; Args = @("markers") },
                    @{ Name = "markers-square1"; Args = @("markers") },
                    @{ Name = "compute-1"; Args = @("compute") }
                )
                foreach ($at in 0, 1, 2, 2, 1, 0) {
                    $variant = $variants[$at]
                    $extra = if ($variant.Name -eq "markers-square1") { @("--footprint", "1") } else { @() }
                    $log = Join-Path $Out "r$round-$($variant.Name)-$count-$dist-$([guid]::NewGuid().ToString('N').Substring(0, 4)).txt"
                    & $exe run $variant.Args[0] $count $dist @extra *> $log
                    if ($LASTEXITCODE -ne 0) { Write-Warning "run failed: $log" }
                }
            }
        }
    }
}
finally {
    Stop-Process -Id $smi.Id -ErrorAction SilentlyContinue
}

foreach ($count in "1e6", "1e7") {
    foreach ($dist in "uniform", "cluster") {
        foreach ($run in 1, 2) {
            & $exe determinism $count $dist *> (Join-Path $Out "det-warp-$count-$dist-run$run.txt")
        }

        & $exe determinism $count $dist --hardware *> (Join-Path $Out "det-hw-$count-$dist.txt")
    }
}
