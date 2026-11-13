# SPIKE (row K0e, Issue #491): the full measurement of the bricked volume.
# Usage (PowerShell, from the repository root, with C:\Program Files (x86)\Microsoft Visual Studio\Installer on PATH):
#   ./benchmarks/HyperPlot.Volume/run-k0e.ps1 -Out <folder outside the repository> [-Data .dev/k0e-data] [-Rounds 3] [-KeepData]
# Publishes the NativeAOT Release build, generates the four fields (512³ and 1024³, smooth and noisy, seeds 1 and 2), then:
#   1. P-14 first: the orbit (600 frames after 120, one every 33.3 ms) of every size, field, transfer function, and threshold,
#      bricks of 64 and 32 alternated (64, 32 in odd rounds, 32, 64 in even ones), with the GPU's clock logged beside them.
#   2. The frames until the view is final, cold and after zooms of 2, 4, 8, at the 4 MB budget a frame.
#   3. The two filters (the sampler's trilinear and 8 loads) on 512³ smooth translucent, alternated.
#   4. The orbit of 512³ with the atlas held to the cache's 256 MB budget (design §5.2).
#   5. The determinism check: 256³ twice on WARP, each filter; 512³ once on the hardware.
# Every run's output is kept in -Out. The generated data is deleted at the end unless -KeepData.
#
# Expected before measuring (RTX 3060 Ti, 1,920 × 1,080; .cursor/rules/bench.mdc):
#   Memory: levels alone 114.3 % of raw (1 + 1/8 + 1/64 + ...); levels and ghosts 125.3 % with 64³ bricks
#     ((66/64)³ × 1.1426) and 137.1 % with 32³ ((34/32)³ × 1.1428), the same at 1024³. 512³ 64: 585 bricks, 336 MB;
#     32: 4,681 bricks, 368 MB. 1024³ 64: 4,681 bricks, 2.69 GB; 32: 37,449 bricks, 2.94 GB (2 atlases each).
#   Orbit p95 of the GPU, practical range: 512³ smooth opaque 4 to 15 ms; 512³ smooth translucent 15 to 50 ms; noisy fields 1.5
#     to 3 times the smooth ones (fewer empty bricks); 1024³ about the same as 512³ for opaque and up to 2 times for translucent
#     (the far half is drawn from level 1). Worst case: 1024³ noisy translucent over 100 ms. P-14 (33.3 ms) expected to hold
#     for opaque, and for translucent only on the smooth 512³ field if at all. 32³ faster than 64³ by finer skipping on smooth
#     fields, slower or equal on noisy ones (more table loads); threshold 0.95 against 0.99 matters only for opaque.
#   Upload: after the first frame's set, the orbit asks for bricks as their level changes with distance: p95 under 4 MB, a
#     few frames over it at 1024³ (a 64³ brick is 562 KB, 7 a frame at 4 MB; a 32³ one 77 KB, 53 a frame).
#   Frames until final: cold 512³ 64 smooth about 40 to 60 frames (the visible bricks over 4 MB), 1024³ 300 to 700 frames
#     (10 to 25 s at 30 fps); zoom 2 at 512³ few (most level-0 bricks already resident), at 1024³ up to 70.
#   Determinism: the 8-load filter identical in both histories; the sampler's filter identical in the same cache and possibly
#     not after the other history (the slot changes the rounding of the atlas coordinate).
param(
    [Parameter(Mandatory = $true)] [string] $Out,
    [string] $Data = ".dev/k0e-data",
    [int] $Rounds = 3,
    [switch] $KeepData
)

$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force $Out | Out-Null
New-Item -ItemType Directory -Force $Data | Out-Null
$publish = Join-Path $Out "publish"
dotnet publish benchmarks/HyperPlot.Volume -c Release -r win-x64 -o $publish
if ($LASTEXITCODE -ne 0) { throw "publish failed" }
$exe = Join-Path $publish "HyperPlot.Volume.exe"

function Invoke-Logged([string] $Name, [string[]] $Arguments) {
    $log = Join-Path $Out "$Name-$([guid]::NewGuid().ToString('N').Substring(0, 4)).txt"
    "## $(Get-Date -Format 'HH:mm:ss') $exe $($Arguments -join ' ')" | Out-File $log -Encoding utf8
    & $exe @Arguments *>> $log
    if ($LASTEXITCODE -ne 0) { Write-Warning "exit $LASTEXITCODE`: $log" }
}

try {
    $files = @{}
    foreach ($size in 512, 1024, 256) {
        foreach ($field in @(@{ Name = "smooth"; Seed = 1 }, @{ Name = "noisy"; Seed = 2 })) {
            $file = Join-Path $Data "$($field.Name)-$size.vol"
            $files["$($field.Name)-$size"] = $file
            Invoke-Logged "generate-$($field.Name)-$size" @("generate", "$size", $field.Name, $file, "--seed", "$($field.Seed)")
        }
    }

    $smi = Start-Process nvidia-smi -ArgumentList "--query-gpu=timestamp,clocks.gr,pstate,memory.used --format=csv -lms 200" `
        -RedirectStandardOutput (Join-Path $Out "clocks.csv") -WindowStyle Hidden -PassThru
    try {
        # 1. The orbit.
        for ($round = 1; $round -le $Rounds; $round++) {
            $bricks = if ($round % 2 -eq 1) { @(64, 32) } else { @(32, 64) }
            foreach ($size in 512, 1024) {
                foreach ($field in "smooth", "noisy") {
                    foreach ($tf in "translucent", "opaque") {
                        foreach ($threshold in "0.99", "0.95") {
                            foreach ($brick in $bricks) {
                                Invoke-Logged "r$round-orbit-$field-$size-$tf-t$threshold-b$brick" @(
                                    "run", $files["$field-$size"], "--brick", "$brick", "--tf", $tf, "--threshold", $threshold
                                )
                            }
                        }
                    }
                }
            }
        }

        # 2. The frames until final.
        foreach ($size in 512, 1024) {
            foreach ($field in "smooth", "noisy") {
                foreach ($tf in "translucent", "opaque") {
                    foreach ($brick in 64, 32) {
                        Invoke-Logged "zoom-$field-$size-$tf-b$brick" @("zoom", $files["$field-$size"], "--brick", "$brick", "--tf", $tf)
                    }
                }
            }
        }

        # 3. The filters.
        for ($round = 1; $round -le $Rounds; $round++) {
            $filters = if ($round % 2 -eq 1) { @("sample", "load") } else { @("load", "sample") }
            foreach ($brick in 64, 32) {
                foreach ($filter in $filters) {
                    Invoke-Logged "r$round-filter-$filter-smooth-512-b$brick" @(
                        "run", $files["smooth-512"], "--brick", "$brick", "--filter", $filter
                    )
                }
            }
        }

        # 4. The atlas at the cache's budget.
        foreach ($field in "smooth", "noisy") {
            foreach ($brick in 64, 32) {
                Invoke-Logged "atlas256-$field-512-b$brick" @("run", $files["$field-512"], "--brick", "$brick", "--atlas-mb", "256")
            }
        }
    }
    finally {
        Stop-Process -Id $smi.Id -ErrorAction SilentlyContinue
    }

    # 5. Determinism.
    foreach ($field in "smooth", "noisy") {
        foreach ($tf in "translucent", "opaque") {
            foreach ($brick in 64, 32) {
                foreach ($filter in "sample", "load") {
                    foreach ($run in 1, 2) {
                        Invoke-Logged "det-warp-$field-256-$tf-b$brick-$filter-run$run" @(
                            "determinism", $files["$field-256"], "--brick", "$brick", "--tf", $tf, "--filter", $filter
                        )
                    }

                    Invoke-Logged "det-hw-$field-512-$tf-b$brick-$filter" @(
                        "determinism", $files["$field-512"], "--brick", "$brick", "--tf", $tf, "--filter", $filter, "--hardware"
                    )
                }
            }
        }
    }
}
finally {
    if (-not $KeepData) {
        Remove-Item -Recurse -Force $Data -ErrorAction SilentlyContinue
    }
}
