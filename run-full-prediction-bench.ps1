<#
.SYNOPSIS
Runs a sequential dedicated-server/client CPU benchmark using one Development Mono player.
.DESCRIPTION
Build separately with PurrNet.Prediction.Benchmarks.Editor.FullPredictionBenchmarkBuild.BuildWindowsDev.
Each run creates a unique output directory; existing results are never deleted. All players run
hidden with -batchmode -nographics. This measures simulation CPU work, not rendered frame rate.

LatencyMs configures BOTH endpoints. The pinned LiteNetLib delays outbound and inbound packets
by floor(value / 2), so even values >=12 add approximately value ms one way, twice that per RTT.
Half-delays <=5ms are suppressed. The manifest records the nominal effective delay explicitly.

Example, from PowerShell:
  .\run-full-prediction-bench.ps1 -ClientCounts 1,2,4 -LatencyMs 0,50 -ReconcileMs 0,33.333 -Repeats 3
  .\run-full-prediction-bench.ps1 -ClientCounts 1,2,4 -TotalBodies 48 -DryRun
#>
[CmdletBinding()]
param(
    [ValidateSet('Physics', 'Regression', 'Trail')][string]$Scenario = 'Physics',
    [int[]]$ClientCounts = @(1, 2, 4),
    [int[]]$LatencyMs = @(0, 50),
    [double[]]$ReconcileMs = @(0, 33.333),
    [ValidateRange(1, 100)][int]$Repeats = 1,
    [ValidateRange(1, 10000)][int]$BodiesPerPlayer = 8,
    [ValidateRange(0, 10000)][int]$SharedBodies = 16,
    [ValidateRange(0, 100000)][int]$TotalBodies = 0,
    [ValidateRange(0, 127)][int]$EventMask = 0,
    [ValidateRange(1, 1000)][int]$TickRate = 60,
    [ValidateRange(0.1, 3600)][double]$Seconds = 10,
    [ValidateRange(0, 3600)][double]$SettleSeconds = 3,
    [ValidateRange(1, 1024)][int]$JobWorkerCount = 2,
    [ValidateRange(1, 65535)][int]$BasePort = 17800,
    [ValidateRange(1, 3600)][int]$RunTimeoutSeconds = 180,
    [ValidateRange(1, 3600)][int]$ServerStartTimeoutSeconds = 30,
    [string]$PlayerPath = (Join-Path $PSScriptRoot 'test-results/full-prediction-player/PurrDictionTests.exe'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'test-results/full-prediction-bench'),
    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$invariantCulture = [Globalization.CultureInfo]::InvariantCulture

function Write-JsonFile([string]$Path, $Value) {
    $Value | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $Path -Encoding UTF8
}

function Format-Number([double]$Value) {
    return $Value.ToString('0.###', $invariantCulture)
}

function Join-WindowsArguments([string[]]$Arguments) {
    # Start-Process joins ArgumentList without preserving token boundaries. Quote using the
    # Windows command-line rules, including backslashes before quotes and at the end of paths.
    return (($Arguments | ForEach-Object {
        $escaped = [regex]::Replace($_, '(\\*)"', '$1$1\"')
        $escaped = [regex]::Replace($escaped, '(\\+)$', '$1$1')
        '"' + $escaped + '"'
    }) -join ' ')
}

function New-PlayerRecord([string]$Role, [string]$Stem, [string[]]$Arguments, [string]$CaseDirectory) {
    $resultPath = Join-Path $CaseDirectory ($Stem + '.results.json')
    $metricsPath = Join-Path $CaseDirectory ($Stem + '.metrics.json')
    $logPath = Join-Path $CaseDirectory ($Stem + '.log')
    $tokens = $Arguments + @('-role', $Role, '-results', $resultPath,
        '-fpMetrics', $metricsPath, '-logFile', $logPath)
    if ($Role -eq 'client') { $tokens += @('-serverHost', '127.0.0.1') }
    return [pscustomobject]@{
        role = $Role; name = $Stem; arguments = $tokens; process = $null
        processId = $null; startedAtUtc = $null; exitCode = $null; cpuSeconds = $null
        workingSetBytesAtLastPoll = $null; timedOut = $false; killedByRunner = $false
        resultsPath = $resultPath; metricsPath = $metricsPath; logPath = $logPath
        success = $false; failure = $null
    }
}

function Start-BenchmarkPlayer($Record) {
    $Record.process = Start-Process -FilePath $PlayerPath -ArgumentList (Join-WindowsArguments $Record.arguments) `
        -WorkingDirectory $PSScriptRoot -WindowStyle Hidden -PassThru
    # Retain the handle before the process exits, so ExitCode remains available on Windows.
    $null = $Record.process.Handle
    $Record.processId = $Record.process.Id
    $Record.startedAtUtc = [DateTime]::UtcNow.ToString('O')
}

function Update-ProcessSample($Record) {
    if ($null -eq $Record.process) { return }
    try {
        $Record.process.Refresh()
        $Record.cpuSeconds = $Record.process.TotalProcessorTime.TotalSeconds
        if (-not $Record.process.HasExited) {
            $Record.workingSetBytesAtLastPoll = $Record.process.WorkingSet64
        }
    } catch {
        # Keep the last sample if the operating system has already released process counters.
    }
}

function Export-ProcessRecord($Record) {
    return $Record | Select-Object * -ExcludeProperty process
}

if (-not $ClientCounts.Count -or @($ClientCounts | Where-Object { $_ -lt 1 }).Count) {
    throw 'ClientCounts must contain positive client counts.'
}
if (-not $LatencyMs.Count -or @($LatencyMs | Where-Object { $_ -lt 0 }).Count) {
    throw 'LatencyMs must contain non-negative values.'
}
if (-not $ReconcileMs.Count -or @($ReconcileMs | Where-Object { $_ -lt 0 -or [double]::IsNaN($_) -or [double]::IsInfinity($_) }).Count) {
    throw 'ReconcileMs must contain finite, non-negative intervals.'
}
if ($TotalBodies -gt 0 -and $TotalBodies -lt ($SharedBodies + ($ClientCounts | Measure-Object -Maximum).Maximum)) {
    throw 'TotalBodies must leave at least one owned body for each client after SharedBodies.'
}
$caseCount = [long]$ClientCounts.Count * $LatencyMs.Count * $ReconcileMs.Count * $Repeats
if (($BasePort + $caseCount - 1) -gt 65535) { throw 'The case matrix exceeds the available port range.' }
if ($RunTimeoutSeconds -le ($Seconds + $SettleSeconds)) { throw 'RunTimeoutSeconds must exceed the measurement and settling duration.' }

$PlayerPath = [IO.Path]::GetFullPath($PlayerPath)
if (-not $DryRun -and -not (Test-Path -LiteralPath $PlayerPath -PathType Leaf)) {
    throw "Player not found: $PlayerPath. Build FullPredictionBenchmarkBuild.BuildWindowsDev first."
}
$runId = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$runDirectory = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) $runId
$null = New-Item -ItemType Directory -Path $runDirectory

$gitHead = $null
try { $gitHead = (& git -C $PSScriptRoot rev-parse HEAD 2>$null | Out-String).Trim() } catch { }
$binaryHash = $null
if (Test-Path -LiteralPath $PlayerPath -PathType Leaf) { $binaryHash = (Get-FileHash -LiteralPath $PlayerPath -Algorithm SHA256).Hash }
$managedDirectory = Join-Path ([IO.Path]::GetDirectoryName($PlayerPath)) ([IO.Path]::GetFileNameWithoutExtension($PlayerPath) + '_Data/Managed')
$managedHashes = @()
if (Test-Path -LiteralPath $managedDirectory) {
    $managedHashes = @(Get-ChildItem -LiteralPath $managedDirectory -Filter '*.dll' -File | Sort-Object Name | ForEach-Object {
        [pscustomobject]@{ name = $_.Name; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
}
$workingStatus = (& git -C $PSScriptRoot status --short | Out-String).Trim()
$buildMetadata = $null
if (Test-Path -LiteralPath ($PlayerPath + '.build.json') -PathType Leaf) {
    $buildMetadata = Get-Content -LiteralPath ($PlayerPath + '.build.json') -Raw | ConvertFrom-Json
}
$cpu = $null
$memoryBytes = $null
try {
    $cpu = @(Get-CimInstance Win32_Processor | Select-Object Name, NumberOfCores, NumberOfLogicalProcessors)
    $memoryBytes = (Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory
} catch { }
$manifest = [ordered]@{
    runId = $runId; startedAtUtc = [DateTime]::UtcNow.ToString('O'); dryRun = [bool]$DryRun
    gitHead = $gitHead; workingStatus = $workingStatus; playerPath = $PlayerPath; binarySha256 = $binaryHash; managedAssemblyHashes = $managedHashes; build = $buildMetadata
    scriptSha256 = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash
    projectUnityVersion = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'ProjectSettings/ProjectVersion.txt') -First 1)
    os = [Environment]::OSVersion.VersionString; logicalProcessors = [Environment]::ProcessorCount
    cpu = $cpu; totalPhysicalMemoryBytes = $memoryBytes; powerShellVersion = $PSVersionTable.PSVersion.ToString()
    execution = 'Sequential cases; one dedicated server plus N clients on this machine; hidden batchmode nographics'
    measurement = 'Simulation CPU workload; no rendered FPS claim. Per-process cpuSeconds includes setup and teardown.'
    latencySemantics = 'Same fixed argument at both endpoints; each adds floor(argument/2) outbound and inbound. Half-delays <=5ms are suppressed. Nominal RTT is twice nominal effective one-way delay, plus network/scheduling.'
    scenario = $Scenario; predictedEventMask = $EventMask; clientCounts = $ClientCounts; latencyMs = $LatencyMs; reconcileMs = $ReconcileMs; repeats = $Repeats
    cadenceOrder = 'Provided order on odd repeats, reversed on even repeats'
    bodiesPerPlayer = $BodiesPerPlayer; sharedBodies = $SharedBodies; totalBodiesOverride = $TotalBodies
    tickRate = $TickRate; seconds = $Seconds; settleSeconds = $SettleSeconds; jobWorkerCount = $JobWorkerCount
    basePort = $BasePort; runTimeoutSeconds = $RunTimeoutSeconds; serverStartTimeoutSeconds = $ServerStartTimeoutSeconds
    caseCount = $caseCount; outputDirectory = $runDirectory
}
Write-JsonFile (Join-Path $runDirectory 'manifest.json') $manifest
Write-Host "Full prediction benchmark: $caseCount sequential cases; output $runDirectory"

$allCases = [Collections.Generic.List[object]]::new()
$caseIndex = 0
foreach ($repeat in 1..$Repeats) {
    $intervalOrder = @($ReconcileMs)
    if ($repeat % 2 -eq 0) { [array]::Reverse($intervalOrder) }
    foreach ($clients in $ClientCounts) {
        foreach ($latency in $LatencyMs) {
            foreach ($interval in $intervalOrder) {
                $intervalText = Format-Number $interval
                $caseId = '{0:D3}-clients{1}-latency{2}-reconcile{3}-repeat{4}' -f $caseIndex, $clients, $latency, $intervalText, $repeat
                $caseDirectory = Join-Path $runDirectory $caseId
                $null = New-Item -ItemType Directory -Path $caseDirectory
                $halfLatency = [math]::Floor($latency / 2.0)
                $effectiveOneWay = if ($halfLatency -gt 5) { 2 * $halfLatency } else { 0 }
                $bodyCount = if ($TotalBodies -gt 0) { $TotalBodies } else { $clients * $BodiesPerPlayer + $SharedBodies }
                $scenarioFlag = switch ($Scenario) {
                    'Physics' { '-fullPredictionPhysicsBenchmark' }
                    'Regression' { '-fullPredictionCadenceRegressionOnly' }
                    'Trail' { '-trailScenarioOnly' }
                }
                $shared = @('-batchmode', '-nographics', '-job-worker-count', "$JobWorkerCount",
                    $scenarioFlag, '-fpEventMask', "$EventMask", '-count', "$clients", '-port', "$($BasePort + $caseIndex)",
                    '-connectTimeout', "$RunTimeoutSeconds", '-tickRate', "$TickRate",
                    '-fpBodiesPerPlayer', "$BodiesPerPlayer", '-fpSharedBodies', "$SharedBodies",
                    '-fpSeconds', (Format-Number $Seconds), '-fpSettleSeconds', (Format-Number $SettleSeconds),
                    '-latencyMin', "$latency", '-latencyMax', "$latency", '-packetLoss', '0')
                if ($TotalBodies -gt 0) { $shared += @('-fpTotalBodies', "$TotalBodies") }
                $records = [Collections.Generic.List[object]]::new()
                # The dedicated server always uses the unchanged cadence. Only clients receive A/B.
                $records.Add((New-PlayerRecord 'server' 'server' ($shared + @('-fpReconcileMs', '0')) $caseDirectory))
                for ($client = 1; $client -le $clients; $client++) {
                    $records.Add((New-PlayerRecord 'client' "client-$client" ($shared + @('-fpReconcileMs', $intervalText)) $caseDirectory))
                }
                $case = [pscustomobject]@{
                    caseId = $caseId; repeat = $repeat; clients = $clients; totalBodies = $bodyCount
                    configuredLatencyMs = $latency; nominalEffectiveAddedOneWayMs = $effectiveOneWay
                    nominalEffectiveAddedRttMs = 2 * $effectiveOneWay; clientReconcileMs = $interval
                    serverReconcileMs = 0; jobWorkerCount = $JobWorkerCount; port = $BasePort + $caseIndex
                    binarySha256 = $binaryHash; success = $true; failure = $null; elapsedSeconds = 0
                    directory = $caseDirectory; processes = @($records | ForEach-Object { Export-ProcessRecord $_ })
                }
                Write-JsonFile (Join-Path $caseDirectory 'case.json') $case
                Write-Host "[$($caseIndex + 1)/$caseCount] $caseId, $bodyCount bodies, workers=$JobWorkerCount"
                $timer = [Diagnostics.Stopwatch]::StartNew()
                try {
                    if (-not $DryRun) {
                        Start-BenchmarkPlayer $records[0]
                        $serverReady = $false
                        while ($timer.Elapsed.TotalSeconds -lt [math]::Min($ServerStartTimeoutSeconds, $RunTimeoutSeconds)) {
                            if ($records[0].process.HasExited) { throw 'Dedicated server exited before becoming ready.' }
                            if (Test-Path -LiteralPath $records[0].logPath) {
                                $serverReady = [bool](Select-String -LiteralPath $records[0].logPath -SimpleMatch 'local connection ready' -Quiet)
                                if ($serverReady) { break }
                            }
                            Start-Sleep -Milliseconds 250
                        }
                        if (-not $serverReady) { throw 'Dedicated server startup timed out.' }
                        for ($i = 1; $i -lt $records.Count; $i++) { Start-BenchmarkPlayer $records[$i] }
                        do {
                            $anyRunning = $false
                            foreach ($record in $records) {
                                Update-ProcessSample $record
                                if (-not $record.process.HasExited) { $anyRunning = $true }
                            }
                            if (-not $anyRunning) { break }
                            Start-Sleep -Milliseconds 250
                        } while ($timer.Elapsed.TotalSeconds -lt $RunTimeoutSeconds)
                        if ($anyRunning) {
                            foreach ($record in $records) {
                                if (-not $record.process.HasExited) { $record.timedOut = $true }
                            }
                            throw "Case exceeded ${RunTimeoutSeconds}s timeout."
                        }
                    }
                } catch {
                    $case.success = $false
                    $case.failure = $_.Exception.Message
                    Write-Warning "$caseId failed: $($case.failure)"
                } finally {
                    foreach ($record in $records) {
                        if ($null -ne $record.process) {
                            try {
                                Update-ProcessSample $record
                                if (-not $record.process.HasExited) {
                                    # The retained Process objects belong only to this case. Never kill by name.
                                    try {
                                        $record.process.Kill()
                                        $record.killedByRunner = $true
                                    } catch [InvalidOperationException] {
                                        if (-not $record.process.HasExited) { throw }
                                    }
                                    $null = $record.process.WaitForExit(5000)
                                }
                                if ($record.process.HasExited) {
                                    $record.process.WaitForExit()
                                    $record.exitCode = $record.process.ExitCode
                                } else {
                                    throw "Launched process $($record.processId) did not exit after cleanup."
                                }
                            } catch {
                                $case.success = $false
                                $record.failure = $_.Exception.Message
                                Write-Warning "Cleanup for $($record.name) PID $($record.processId): $($record.failure)"
                            } finally {
                                $record.process.Dispose()
                            }
                        }
                    }
                }
                $timer.Stop()
                $case.elapsedSeconds = $timer.Elapsed.TotalSeconds
                foreach ($record in $records) {
                    if ($DryRun) { $record.success = $true; continue }
                    try {
                        if ($record.exitCode -ne 0 -or $record.killedByRunner) { throw "Process exit code $($record.exitCode), killed=$($record.killedByRunner)" }
                        $results = @(Get-Content -LiteralPath $record.resultsPath -Raw | ConvertFrom-Json)
                        if (-not $results.Count -or @($results | Where-Object { $null -eq $_ -or $null -eq $_.result -or -not $_.result.success }).Count) {
                            throw 'Scenario result missing or unsuccessful.'
                        }
                        if ($Scenario -eq 'Physics') {
                            $metrics = Get-Content -LiteralPath $record.metricsPath -Raw | ConvertFrom-Json
                            if ($null -eq $metrics) { throw 'Metrics JSON is empty.' }
                            if (-not $metrics.success -or -not $metrics.verifiedStateMatch) { throw 'Physics workload validation failed.' }
                        }
                        $record.success = $true
                    } catch {
                        $record.failure = $_.Exception.Message
                        $case.success = $false
                    }
                }
                $case.processes = @($records | ForEach-Object { Export-ProcessRecord $_ })
                Write-JsonFile (Join-Path $caseDirectory 'case.json') $case
                $allCases.Add($case)
                Write-JsonFile (Join-Path $runDirectory 'cases.json') @($allCases.ToArray())
                $caseIndex++
            }
        }
    }
}

$summary = foreach ($case in $allCases) {
    foreach ($process in $case.processes) {
        [pscustomobject]@{
            caseId = $case.caseId; clients = $case.clients; totalBodies = $case.totalBodies
            oneWayLatencyMs = $case.nominalEffectiveAddedOneWayMs; clientReconcileMs = $case.clientReconcileMs
            repeat = $case.repeat; role = $process.name; success = $process.success; exitCode = $process.exitCode
            cpuSeconds = $process.cpuSeconds; metricsPath = $process.metricsPath; failure = $process.failure
        }
    }
}
$summary | Export-Csv -LiteralPath (Join-Path $runDirectory 'processes.csv') -NoTypeInformation -Encoding UTF8
$failedCases = @($allCases | Where-Object { -not $_.success }).Count
Write-Host "Completed $caseCount cases; failed=$failedCases; output $runDirectory"
if ($failedCases -gt 0) { exit 1 }
