# SPIKE (row K0c, Issue #489): the full measurement of the surface LOD.
# Usage (PowerShell, from the repository root, with C:\Program Files (x86)\Microsoft Visual Studio\Installer on PATH,
# after `dotnet run --project tools/ShaderGen -- surface`):
#   ./benchmarks/HyperPlot.SurfaceLod/run-k0c.ps1 -Out <folder outside the repository> [-Rounds 3]
# Publishes the NativeAOT Release build, then runs each scene (smooth, noisy, spikes; uniform and non-uniform grid) in
# rounds, the three tile sizes in a balanced order inside each round (64, 128, 256, 256, 128, 64), with the GPU's clock
# and the machine's state logged beside them. Then a face of 260 for 64² tiles (16 a face) against 258 (9 a face) on the
# spikes scene, and the crack check on WARP for each tile size, stitched and not stitched. Every run's output is kept in
# -Out. Each run builds its data (about 0.6 GB, some seconds), then draws 720 frames at 60 per second (12 s).
param(
    [Parameter(Mandatory = $true)] [string] $Out,
    [int] $Rounds = 3
)

$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force $Out | Out-Null
$publish = Join-Path $Out "publish"
dotnet publish benchmarks/HyperPlot.SurfaceLod -c Release -r win-x64 -o $publish
if ($LASTEXITCODE -ne 0) { throw "publish failed" }
$exe = Join-Path $publish "HyperPlot.SurfaceLod.exe"

$smi = Start-Process nvidia-smi -ArgumentList "--query-gpu=timestamp,clocks.gr,pstate --format=csv -lms 200" `
    -RedirectStandardOutput (Join-Path $Out "clocks.csv") -WindowStyle Hidden -PassThru
$machineLog = Join-Path $Out "machine.log"
$machine = Start-Process pwsh -ArgumentList "-NoProfile -File benchmarks/HyperPlot.DataPath/watch-machine.ps1 -Log `"$machineLog`" -Scene HyperPlot.SurfaceLod" `
    -WindowStyle Hidden -PassThru
try {
    for ($round = 1; $round -le $Rounds; $round++) {
        foreach ($data in "smooth", "spikes", "noisy") {
            foreach ($grid in "uniform", "nonuniform") {
                foreach ($tile in 64, 128, 256, 256, 128, 64) {
                    $name = "r$round-$data-$grid-$tile-$([guid]::NewGuid().ToString('N').Substring(0, 4))"
                    & $exe run $tile $data $grid --csv (Join-Path $Out "$name.csv") *> (Join-Path $Out "$name.txt")
                    if ($LASTEXITCODE -ne 0) { Write-Warning "run failed: $name" }
                }
            }
        }

        # The face's shape (Issue #489's added question): 64² tiles in faces of 260 (4 × 65) against 258 (3 × 65).
        foreach ($face in 258, 260, 260, 258) {
            $name = "r$round-face$face-spikes-uniform-64-$([guid]::NewGuid().ToString('N').Substring(0, 4))"
            & $exe run 64 spikes uniform --face $face *> (Join-Path $Out "$name.txt")
            if ($LASTEXITCODE -ne 0) { Write-Warning "run failed: $name" }
        }
    }
}
finally {
    Stop-Process -Id $smi.Id -ErrorAction SilentlyContinue
    New-Item -ItemType File -Force "$machineLog.stop" | Out-Null
    $machine | Wait-Process -Timeout 30 -ErrorAction SilentlyContinue
}

foreach ($tile in 64, 128, 256) {
    foreach ($tag in "stitch", "nostitch") {
        $stitch = if ($tag -eq "nostitch") { @("--no-stitch") } else { @() }
        & $exe image $tile spikes nonuniform --out (Join-Path $Out "image-$tile-$tag.bmp") @stitch *> (Join-Path $Out "image-$tile-$tag.txt")
        if ($LASTEXITCODE -ne 0) { Write-Warning "image failed: $tile $tag" }
    }
}
