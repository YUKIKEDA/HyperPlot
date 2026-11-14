# SPIKE (row K0c, Issue #489): the full measurement of the surface LOD.
# Usage (PowerShell, from the repository root, with C:\Program Files (x86)\Microsoft Visual Studio\Installer on PATH,
# after `dotnet run --project tools/ShaderGen -- surface`):
#   ./benchmarks/HyperPlot.SurfaceLod/run-k0c.ps1 -Out <folder outside the repository> [-Rounds 3] [-ImageOnly]
# Publishes the NativeAOT Release build, then runs each scene (smooth, noisy, spikes; uniform and non-uniform grid) in
# rounds, the three tile sizes in a balanced order inside each round (64, 128, 256, 256, 128, 64), with the GPU's clock
# and the machine's state logged beside them. Then a face of 260 for 64² tiles (16 a face) against 258 (9 a face) on the
# spikes scene, and the crack check on WARP for each tile size, stitched and not stitched. Every run's output is kept in
# -Out. Each run builds its data (about 0.6 GB, some seconds), then draws 720 frames at 60 per second (12 s).
# -ImageOnly publishes and runs only the crack check (the six images); the runs, clocks.csv, and machine.log already in
# -Out stay as they are.
# -Cracks publishes and runs only the crack count of Issue #489's decision 4, on WARP: for each data set (smooth, noisy,
# spikes) and grid (uniform, nonuniform), one process draws 12 frames of the orbit (every 50th of 600) stitched and not
# stitched, and counts the background pixels whose 8 neighbors are all surface. Tiles of 64 in faces of 260 (decision 2)
# by default; -CrackTiles 64,128,256 adds the others (faces of 258). Each run has a deadline (-RunTimeoutSeconds) and its
# exit code is checked; the script ends with 1 when a run failed or timed out. Output in <Out>\cracks: each run's .txt,
# .csv (a row a frame and stitching), -pixels.csv (the counted pixels of the stitched images), the BMPs of the stitched
# frames that count any and of frame 0 not stitched, and cracks-summary.csv.
# Expected, before measuring: stitched 0 on the smooth and noisy data in every frame; on the spikes data a few pixels may
# count where the background shows between two spikes (not a crack; the -pixels.csv and the BMP tell which); not stitched,
# tens to thousands a frame wherever a finer tile meets a coarser one. About 30 s a run, 6 runs (3 minutes) for 64 alone,
# about 10 minutes for the three tile sizes. -NoPublish reuses <Out>\publish.
param(
    [Parameter(Mandatory = $true)] [string] $Out,
    [int] $Rounds = 3,
    [switch] $ImageOnly,
    [switch] $Cracks,
    [int[]] $CrackTiles = @(64),
    [int] $RunTimeoutSeconds = 300,
    [switch] $NoPublish
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "../..")
$env:PATH = "C:\Program Files (x86)\Microsoft Visual Studio\Installer;$env:PATH"
New-Item -ItemType Directory -Force $Out | Out-Null
$Out = (Resolve-Path $Out).Path
$publish = Join-Path $Out "publish"
if (-not $NoPublish) {
    dotnet publish (Join-Path $root "benchmarks/HyperPlot.SurfaceLod") -c Release -r win-x64 -o $publish
    if ($LASTEXITCODE -ne 0) { throw "publish failed" }
}

$exe = Join-Path $publish "HyperPlot.SurfaceLod.exe"
if (-not (Test-Path $exe)) { throw "not published: $exe" }

if ($Cracks) {
    $folder = Join-Path $Out "cracks"
    New-Item -ItemType Directory -Force $folder | Out-Null
    $failed = 0
    $rows = @()
    foreach ($tile in $CrackTiles) {
        $face = if ($tile -eq 64) { 260 } else { 258 }
        foreach ($data in "smooth", "noisy", "spikes") {
            foreach ($grid in "uniform", "nonuniform") {
                $name = "cracks-$tile-$data-$grid"
                $log = Join-Path $folder "$name.txt"
                [string[]] $arguments = @(
                    "cracks", "$tile", $data, $grid, "--face", "$face",
                    "--csv", (Join-Path $folder "$name.csv"),
                    "--pixels", (Join-Path $folder "$name-pixels.csv"),
                    "--dump", $folder
                )
                $started = Get-Date
                # Start-Process joins the arguments with spaces; a path with a space is quoted.
                $quoted = $arguments | ForEach-Object { if ($_ -match "\s") { "`"$_`"" } else { $_ } }
                $process = Start-Process -FilePath $exe -ArgumentList $quoted -NoNewWindow -PassThru `
                    -RedirectStandardOutput $log -RedirectStandardError (Join-Path $folder "$name.err.txt")
                $null = $process.Handle # keeps the handle, so that ExitCode is read after the wait
                $code = if ($process.WaitForExit($RunTimeoutSeconds * 1000)) { $process.ExitCode } else {
                    $process.Kill($true)
                    $process.WaitForExit()
                    "timeout"
                }
                $seconds = [math]::Round(((Get-Date) - $started).TotalSeconds, 1)
                Add-Content $log "# exit=$code seconds=$seconds args=$($arguments -join ' ')"
                if ($code -ne 0) {
                    $failed++
                    Write-Warning "run failed ($code): $log"
                    continue
                }

                $total = Get-Content $log | Where-Object { $_ -match "^cracks " } | Select-Object -First 1
                $rows += [pscustomobject]@{
                    tile = $tile; face = $face; data = $data; grid = $grid; seconds = $seconds
                    stitch_total = if ($total -match " stitch_total=(\d+)") { [int]$Matches[1] } else { $null }
                    stitch_frames = if ($total -match " stitch_frames_with_any=(\d+)") { [int]$Matches[1] } else { $null }
                    nostitch_total = if ($total -match "nostitch_total=(\d+)") { [int]$Matches[1] } else { $null }
                    nostitch_frames = if ($total -match "nostitch_frames_with_any=(\d+)") { [int]$Matches[1] } else { $null }
                    missing_images = if ($total -match "images_with_missing_tiles=(\d+)") { [int]$Matches[1] } else { $null }
                }
            }
        }
    }

    $rows | Export-Csv -NoTypeInformation (Join-Path $folder "cracks-summary.csv")
    $rows | Format-Table -AutoSize | Out-String | Write-Host
    if ($failed -gt 0) {
        Write-Warning "$failed run(s) failed or timed out"
        exit 1
    }

    exit 0
}

if (-not $ImageOnly) {
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
}

foreach ($tile in 64, 128, 256) {
    foreach ($tag in "stitch", "nostitch") {
        # @(...) around the if: an if statement unrolls a one-element array to its string, and splatting a string
        # passes its characters one by one ("-", "-", "n", ...).
        $stitch = @(if ($tag -eq "nostitch") { "--no-stitch" })
        & $exe image $tile spikes nonuniform --out (Join-Path $Out "image-$tile-$tag.bmp") @stitch *> (Join-Path $Out "image-$tile-$tag.txt")
        if ($LASTEXITCODE -ne 0) { Write-Warning "image failed: $tile $tag" }
    }
}
