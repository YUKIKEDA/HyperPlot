# SPIKE (row K0d, Issue #490): the full measurement of a 1e7-triangle mesh, opaque and through WBOIT.
# Usage (PowerShell, from the repository root, with C:\Program Files (x86)\Microsoft Visual Studio\Installer on PATH):
#   ./benchmarks/HyperPlot.Mesh3D/run-k0d.ps1 -Out <folder outside the repository> [-Rounds 3]
# Compiles the spike's shaders, publishes the NativeAOT Release build, then, with the GPU's clock logged beside them:
#   1. P-13: the orbit (600 frames after 120 of warm-up, paced at 60 Hz) of each mesh, in rounds, opaque and WBOIT in a
#      balanced order inside each round (opaque, oit, oit, opaque);
#   2. the error of WBOIT against the exact sorted reference, at 4 frames of the orbit, for the paper's weight eq7 (the
#      default), eq10 (the depth buffer's), and none, with the images of eq7 dumped;
#   3. the determinism check twice on WARP and once on the hardware.
# Every run's output is kept in -Out.
#
# Expected before measuring (.cursor/rules/bench.mdc), on the reference machine (RTX 3060 Ti, 1,920 × 1,080):
#   - Opaque orbit: GPU p95 3 to 8 ms (1e7 triangles, about 5e6 vertices pulled from a structured buffer; most triangles
#     cover less than a pixel, so triangle setup and vertex work bound it, not pixels); worst case 12 ms. CPU p95 under
#     0.5 ms (one draw). cpu_plus_gpu p95 within P-13's 16.7 ms.
#   - WBOIT orbit: the same geometry pass with two blended targets plus a full-screen composite (about 0.1 to 0.3 ms):
#     GPU p95 0.3 to 1.5 ms above opaque, worst case 1.5 times opaque. Within 16.7 ms. The redraw of Issue #483's
#     decision 4 costs one such frame.
#   - Error of WBOIT (eq7) against the sorted reference, ΔE00: on pixels of one layer, at most 1 (WBOIT is exact for one
#     layer up to the 8-bit rounding and R8's revealage). Where layers overlap, with α = 0.5 and two layers of colors F
#     (front) and B (back), equal weights give 0.375F + 0.375B against the reference's 0.5F + 0.25B, an error of
#     0.125(F - B): blob overlap p99 3 to 8, max 10 to 20 (front and back lit differently, colors from both ends of the
#     ramp); surface covered p99 under 2 (mostly one layer), overlap p99 2 to 6. eq7's weight is about 250 / z²: 5 times
#     between the nearest and farthest depth of this view (|z| from 2.8 to 6.3), but 1.2 to 1.6 between two layers of the
#     blob 0.4 to 1 apart, so eq7 is only a little better than none; eq10 about the same. The unsorted "over" in submission order: about twice WBOIT's error on the blob, where half the
#     pixels get the back after the front.
#   - WBOIT on the GPU against WBOIT in double on the CPU: ΔE00 p99 under 1, max 2 to 3 where many layers drive R8's
#     revealage near 0 (8 layers of α = 0.5 reach 1/256, one step of R8).
#   - Determinism: WARP bit-exact for both modes and both meshes, and the same SHA-256 in the two WARP runs (design §13.7's
#     inference from D3D11.3 §4.2 and §17.5). The hardware: probably bit-exact too, not required.
param(
    [Parameter(Mandatory = $true)] [string] $Out,
    [int] $Rounds = 3
)

$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force $Out | Out-Null
dotnet run --project tools/ShaderGen -- mesh3d
if ($LASTEXITCODE -ne 0) { throw "ShaderGen failed" }
$publish = Join-Path $Out "publish"
dotnet publish benchmarks/HyperPlot.Mesh3D -c Release -r win-x64 -o $publish
if ($LASTEXITCODE -ne 0) { throw "publish failed" }
$exe = Join-Path $publish "HyperPlot.Mesh3D.exe"

$smi = Start-Process nvidia-smi -ArgumentList "--query-gpu=timestamp,clocks.gr,pstate --format=csv -lms 200" `
    -RedirectStandardOutput (Join-Path $Out "clocks.csv") -WindowStyle Hidden -PassThru
try {
    for ($round = 1; $round -le $Rounds; $round++) {
        foreach ($mesh in "blob", "surface") {
            foreach ($mode in "opaque", "oit", "oit", "opaque") {
                $log = Join-Path $Out "r$round-$mode-$mesh-$([guid]::NewGuid().ToString('N').Substring(0, 4)).txt"
                & $exe run $mode $mesh --csv ($log -replace '\.txt$', '.csv') *> $log
                if ($LASTEXITCODE -ne 0) { Write-Warning "run failed: $log" }
            }
        }
    }
}
finally {
    Stop-Process -Id $smi.Id -ErrorAction SilentlyContinue
}

foreach ($mesh in "blob", "surface") {
    foreach ($weight in "eq7", "eq10", "none") {
        # @(...) around the if: an if statement unrolls a one-element array to its string, and splatting a string
        # passes its characters one by one.
        $extra = @(if ($weight -eq "eq7") { "--dump"; Join-Path $Out "images" })
        $log = Join-Path $Out "error-$mesh-$weight.txt"
        & $exe error $mesh --weight $weight @extra *> $log
        if ($LASTEXITCODE -ne 0) { Write-Warning "error run failed: $log" }
    }
}

foreach ($mesh in "blob", "surface") {
    foreach ($run in 1, 2) {
        & $exe determinism $mesh *> (Join-Path $Out "det-warp-$mesh-run$run.txt")
    }

    & $exe determinism $mesh --hardware *> (Join-Path $Out "det-hw-$mesh.txt")
}
