# Logs the state of the machine while a data-path scene runs, one line every few seconds, so that a slow run can be told
# from a slow machine (docs/benchmarks/c3a-spatial-index.md): the processor's performance against its nominal frequency,
# its utility, the available and the modified memory, the CPU seconds that the scene and that every other process used
# since the last line, and the other processes that used the most.
# Usage: ./benchmarks/HyperPlot.DataPath/watch-machine.ps1 -Log D:\...\machine.log [-Seconds 5]
# Stop it by creating the file "<Log>.stop"; the script deletes that file when it ends.
param(
    [Parameter(Mandatory)] [string] $Log,
    [int] $Seconds = 5,
    [string] $Scene = 'HyperPlot.DataPath'
)

$ErrorActionPreference = 'Stop'
$stop = "$Log.stop"
$before = @{}
try {
    while (-not (Test-Path $stop)) {
        $processor = Get-CimInstance Win32_PerfFormattedData_Counters_ProcessorInformation -Filter "Name='_Total'"
        $memory = Get-CimInstance Win32_PerfFormattedData_PerfOS_Memory
        $now = @{}
        $names = @{}
        foreach ($process in Get-Process) {
            if ($null -ne $process.CPU) {
                $now[$process.Id] = $process.CPU
                $names[$process.Id] = $process.Name
            }
        }

        $sceneSeconds = 0.0
        $otherSeconds = 0.0
        $others = @()
        foreach ($id in $now.Keys) {
            # A process that started since the last line counts from 0.
            $used = $now[$id] - ($before.ContainsKey($id) ? $before[$id] : 0.0)
            if ($before.Count -eq 0 -or $used -le 0) {
                continue
            }

            if ($names[$id] -eq $Scene) {
                $sceneSeconds += $used
            }
            else {
                $otherSeconds += $used
                $others += [pscustomobject]@{ Name = $names[$id]; Used = $used }
            }
        }

        $top = ($others | Sort-Object Used -Descending | Select-Object -First 3 | ForEach-Object { '{0}:{1:F1}' -f $_.Name, $_.Used }) -join ','
        $line = 'at_utc={0:HH:mm:ss} performance_percent={1} utility_percent={2} available_mb={3} modified_mb={4} pages_per_s={5} scene_cpu_s={6:F1} other_cpu_s={7:F1} top={8}' -f `
            (Get-Date).ToUniversalTime(), $processor.PercentProcessorPerformance, $processor.PercentProcessorUtility, $memory.AvailableMBytes, `
            [long]($memory.ModifiedPageListBytes / 1MB), $memory.PagesPersec, $sceneSeconds, $otherSeconds, $top
        Add-Content -Path $Log -Value $line
        $before = $now
        Start-Sleep -Seconds $Seconds
    }
}
finally {
    if (Test-Path $stop) {
        Remove-Item $stop
    }
}
