<#
.SYNOPSIS
Runs one prediction-scenario profile with a dedicated server or host and separate clients.
.EXAMPLE
.\run-prediction-scenarios.ps1 -PlayerPath .\Builds\PurrDictionTests.exe -Mode host -TotalPlayers 4 -DryRun
.EXAMPLE
.\run-prediction-scenarios.ps1 -PlayerPath .\Builds\PurrDictionTests.exe -ExtraArguments @('-policyRegressionScenariosOnly','-desyncPolicy','Report')
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$PlayerPath,
    [ValidateSet('server','host')][string]$Mode = 'server',
    [ValidateRange(2,64)][int]$TotalPlayers = 4,
    [ValidateRange(1,1000)][int]$TickRate = 60,
    [ValidateRange(0,60000)][int]$LatencyMin = 40,
    [ValidateRange(0,60000)][int]$LatencyMax = 80,
    [ValidateRange(0,100)][int]$PacketLoss = 0,
    [string[]]$ExtraArguments = @(),
    [string[]]$ExpectedScenarioNames = @(),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'test-results/prediction-scenarios'),
    [ValidateRange(30,86400)][int]$TimeoutSeconds = 900,
    [ValidateRange(1,3600)][int]$StartupTimeoutSeconds = 30,
    [ValidateRange(1,65535)][int]$Port = 17800,
    [ValidateRange(1,1024)][int]$JobWorkerCount = 2,
    [switch]$DryRun
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$continuityValidatorPath = Join-Path $PSScriptRoot 'prediction-scenario-continuity.ps1'
. $continuityValidatorPath
function Write-Json([string]$Path, $Value) { $Value | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $Path -Encoding UTF8 }
function Join-WindowsArguments([string[]]$Tokens) {
    # Start-Process otherwise loses token boundaries; escape quotes and trailing backslashes.
    return (($Tokens | ForEach-Object {
        $escaped = [regex]::Replace($_, '(\\*)"', '$1$1\"')
        '"' + [regex]::Replace($escaped, '(\\+)$', '$1$1') + '"'
    }) -join ' ')
}
function Export-Peer($Peer) { return $Peer | Select-Object * -ExcludeProperty process }
function Start-Peer($Peer) {
    $Peer.process = Start-Process -FilePath $PlayerPath -ArgumentList (Join-WindowsArguments $Peer.arguments) `
        -WorkingDirectory ([IO.Path]::GetDirectoryName($PlayerPath)) -WindowStyle Hidden -PassThru
    $null = $Peer.process.Handle # Retain the handle so ExitCode survives fast process termination.
    $Peer.processId = $Peer.process.Id
    $Peer.startedAtUtc = [DateTime]::UtcNow.ToString('O')
}
function Read-PeerResults($Peer) {
    $issues = [Collections.Generic.List[string]]::new()
    if ($Peer.failure) { $issues.Add($Peer.failure) }
    if ($null -eq $Peer.exitCode -or $Peer.exitCode -ne 0 -or $Peer.killedByRunner -or $Peer.timedOut) {
        $issues.Add("Exit=$($Peer.exitCode); killed=$($Peer.killedByRunner); timedOut=$($Peer.timedOut).")
    }
    # Failed processes still write valuable per-scenario diagnostics. Parse them independently.
    try {
        $raw = Get-Content -LiteralPath $Peer.resultsPath -Raw
        if (-not $raw.TrimStart().StartsWith('[')) { throw 'Scenario results must be a JSON array.' }
        $results = @($raw | ConvertFrom-Json)
        $Peer.failedScenarios = @($results | Where-Object {
            $null -ne $_ -and $_.PSObject.Properties['result'] -and $null -ne $_.result -and
            $_.result.PSObject.Properties['success'] -and $_.result.success -is [bool] -and -not $_.result.success
        } | Select-Object name,result)
        if ($results.Count -lt 2) { throw 'Expected bootstrap and at least one scenario result.' }
        foreach ($result in $results) {
            if ($null -eq $result -or [string]::IsNullOrWhiteSpace($result.name) -or $null -eq $result.result -or $result.result.success -isnot [bool]) {
                throw 'Malformed scenario name/result/success.'
            }
        }
        $Peer.scenarioNames = @($results | ForEach-Object { $_.name })
        $Peer.scenarioCount = $results.Count
        if ($Peer.scenarioNames[0] -cne 'PredictionBootstrap') { $issues.Add('First scenario is not PredictionBootstrap.') }
        if ($Peer.failedScenarios.Count) { $issues.Add("$($Peer.failedScenarios.Count) scenarios failed.") }
    } catch { $issues.Add($_.Exception.Message) }
    if ($requireFullPredictionContinuity) {
        $Peer.continuity = Test-FullPredictionContinuity -LogPath $Peer.logPath -Role $Peer.role
        foreach ($failure in $Peer.continuity.failures) { $issues.Add("Continuity: $failure") }
    }
    $Peer.success = $issues.Count -eq 0
    $Peer.failure = if ($issues.Count) { $issues -join ' ' } else { $null }
}

if ($LatencyMin -gt $LatencyMax) { throw 'LatencyMin must not exceed LatencyMax.' }
if ($ExpectedScenarioNames.Count -and ($ExpectedScenarioNames.Count -lt 2 -or $ExpectedScenarioNames[0] -cne 'PredictionBootstrap' -or
    @($ExpectedScenarioNames | Where-Object { [string]::IsNullOrWhiteSpace($_) }).Count)) {
    throw 'ExpectedScenarioNames must list PredictionBootstrap followed by the exact nonempty scenario inventory.'
}
$reserved = @('-role','-count','-tickRate','-latencyMin','-latencyMax','-packetLoss','-results','-logFile',
    '-port','-serverHost','-connectTimeout','-batchmode','-nographics','-job-worker-count')
foreach ($token in $ExtraArguments) {
    if ($null -eq $token -or $token.IndexOf([char]0) -ge 0) { throw 'ExtraArguments contains a null token or NUL character.' }
    if ($reserved -contains $token) { throw "ExtraArguments must not override runner-owned argument $token." }
}
$requireFullPredictionContinuity = $ExtraArguments -ccontains '-fullPredictionCadenceRegressionOnly'
$effectiveExtraArguments = @($ExtraArguments)
if ($requireFullPredictionContinuity) {
    $fullPredictionNames = @(Get-FullPredictionScenarioNames)
    if ($ExpectedScenarioNames.Count -and
        [string]::Join('|', $ExpectedScenarioNames) -cne [string]::Join('|', $fullPredictionNames)) {
        throw 'Full-prediction continuity requires the complete seven-scenario inventory.'
    }
    $ExpectedScenarioNames = $fullPredictionNames
    if ($effectiveExtraArguments -cnotcontains '-physicsEventTrace') { $effectiveExtraArguments += '-physicsEventTrace' }
}
$PlayerPath = [IO.Path]::GetFullPath($PlayerPath)
if (-not $DryRun -and -not (Test-Path -LiteralPath $PlayerPath -PathType Leaf)) { throw "Player not found: $PlayerPath" }
$runId = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8)
$runDirectory = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) "$Mode-players$TotalPlayers-$runId"
$null = New-Item -ItemType Directory -Path $runDirectory
$externalClients = if ($Mode -eq 'host') { $TotalPlayers - 1 } else { $TotalPlayers }
$shared = @('-batchmode','-nographics','-job-worker-count',"$JobWorkerCount",'-count',"$TotalPlayers",'-port',"$Port",
    '-connectTimeout',"$TimeoutSeconds",'-tickRate',"$TickRate",'-latencyMin',"$LatencyMin",'-latencyMax',"$LatencyMax",'-packetLoss',"$PacketLoss") + $effectiveExtraArguments
$peers = [Collections.Generic.List[object]]::new()
for ($index = 0; $index -le $externalClients; $index++) {
    $role = if ($index -eq 0) { $Mode } else { 'client' }
    $name = if ($index -eq 0) { $Mode } else { "client-$index" }
    $resultPath = Join-Path $runDirectory ($name + '.results.json')
    $logPath = Join-Path $runDirectory ($name + '.log')
    $tokens = $shared + @('-role',$role,'-results',$resultPath,'-logFile',$logPath)
    if ($role -eq 'client') { $tokens += @('-serverHost','127.0.0.1') }
    $peers.Add([pscustomobject]@{ name=$name; role=$role; arguments=$tokens; resultsPath=$resultPath; logPath=$logPath;
        process=$null; processId=$null; startedAtUtc=$null; exitCode=$null; timedOut=$false; killedByRunner=$false;
        success=$null; failure=$null; scenarioNames=@(); scenarioCount=0; failedScenarios=@(); continuity=$null })
}
$managedDirectory = Join-Path ([IO.Path]::GetDirectoryName($PlayerPath)) ([IO.Path]::GetFileNameWithoutExtension($PlayerPath) + '_Data/Managed')
$managedHashes = @()
if (Test-Path -LiteralPath $managedDirectory -PathType Container) {
    $managedHashes = @(Get-ChildItem -LiteralPath $managedDirectory -Filter '*.dll' -File | Sort-Object Name | ForEach-Object {
        [pscustomobject]@{ name=$_.Name; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
}
$binaryHash = if (Test-Path -LiteralPath $PlayerPath -PathType Leaf) { (Get-FileHash -LiteralPath $PlayerPath -Algorithm SHA256).Hash } else { $null }
$manifest = [ordered]@{ runId=$runId; startedAtUtc=[DateTime]::UtcNow.ToString('O'); dryRun=[bool]$DryRun;
    playerPath=$PlayerPath; binarySha256=$binaryHash; managedAssemblyHashes=$managedHashes;
    scriptSha256=(Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash;
    continuityValidatorSha256=(Get-FileHash -LiteralPath $continuityValidatorPath -Algorithm SHA256).Hash;
    fullPredictionContinuityRequired=$requireFullPredictionContinuity;
    mode=$Mode; totalPlayers=$TotalPlayers; externalClients=$externalClients; processCount=$peers.Count;
    tickRate=$TickRate; latencyMin=$LatencyMin; latencyMax=$LatencyMax; packetLoss=$PacketLoss; port=$Port;
    timeoutSeconds=$TimeoutSeconds; startupTimeoutSeconds=$StartupTimeoutSeconds; jobWorkerCount=$JobWorkerCount;
    extraArguments=$effectiveExtraArguments; requestedExtraArguments=$ExtraArguments;
    expectedScenarioNames=$ExpectedScenarioNames; outputDirectory=$runDirectory;
    execution='One profile; hidden batchmode/nographics. TotalPlayers includes host, excludes dedicated server. No rendered FPS claim.';
    peers=@($peers | ForEach-Object { Export-Peer $_ }) }
Write-Json (Join-Path $runDirectory 'manifest.json') $manifest
$case = [ordered]@{ success=$null; status='planned'; failure=$null; elapsedSeconds=0; dryRun=[bool]$DryRun;
    mode=$Mode; totalPlayers=$TotalPlayers; externalClients=$externalClients; scenarioOrderMatched=$false;
    scenarioExpectationSource=$(if ($requireFullPredictionContinuity) { 'full-prediction continuity inventory' } elseif ($ExpectedScenarioNames.Count) { 'explicit ExpectedScenarioNames' } else { $Mode });
    continuity=[ordered]@{required=$requireFullPredictionContinuity;success=$null;passedPeers=0;failedPeers=0;
        validatorSha256=$manifest.continuityValidatorSha256};
    expectedScenarioNames=$ExpectedScenarioNames;
    directory=$runDirectory; peers=@($peers | ForEach-Object { Export-Peer $_ }) }
if ($DryRun) {
    $case.status = 'dry-run'
    Write-Json (Join-Path $runDirectory 'case.json') $case
    Write-Host "Dry run: $($peers.Count) planned processes; $runDirectory"
    return
}
$case.status = 'running'
Write-Json (Join-Path $runDirectory 'case.json') $case
$timer = [Diagnostics.Stopwatch]::StartNew()
try {
    Start-Peer $peers[0]
    $ready = $false
    while ($timer.Elapsed.TotalSeconds -lt [Math]::Min($StartupTimeoutSeconds,$TimeoutSeconds)) {
        if ($peers[0].process.HasExited) { throw "$Mode exited before local connection readiness." }
        if (Test-Path -LiteralPath $peers[0].logPath) {
            $ready = [bool](Select-String -LiteralPath $peers[0].logPath -SimpleMatch 'local connection ready' -Quiet)
            if ($ready) { break }
        }
        Start-Sleep -Milliseconds 250
    }
    if (-not $ready) { throw "$Mode startup exceeded ${StartupTimeoutSeconds}s." }
    for ($index = 1; $index -lt $peers.Count; $index++) { Start-Peer $peers[$index] }
    while (@($peers | Where-Object { -not $_.process.HasExited }).Count) {
        if ($timer.Elapsed.TotalSeconds -ge $TimeoutSeconds) {
            foreach ($peer in $peers) { if (-not $peer.process.HasExited) { $peer.timedOut = $true } }
            throw "Profile exceeded ${TimeoutSeconds}s."
        }
        Start-Sleep -Milliseconds 250
    }
} catch { $case.failure = $_.Exception.Message } finally {
    foreach ($peer in $peers) {
        if ($null -eq $peer.process) { continue }
        try {
            if (-not $peer.process.HasExited) {
                # Only handles created by this invocation are eligible for termination.
                try { $peer.process.Kill(); $peer.killedByRunner = $true }
                catch [InvalidOperationException] { if (-not $peer.process.HasExited) { throw } }
                $null = $peer.process.WaitForExit(5000)
            }
            if (-not $peer.process.HasExited) { throw "Owned process $($peer.processId) did not exit after cleanup." }
            $peer.process.WaitForExit()
            $peer.exitCode = $peer.process.ExitCode
        } catch { $peer.failure = $_.Exception.Message } finally { $peer.process.Dispose() }
    }
    $timer.Stop()
}
foreach ($peer in $peers) { Read-PeerResults $peer }
$expectedNames = if ($ExpectedScenarioNames.Count) { @($ExpectedScenarioNames) } else { @($peers[0].scenarioNames) }
$case.expectedScenarioNames = $expectedNames
$case.scenarioOrderMatched = $expectedNames.Count -ge 2
foreach ($peer in $peers) {
    $matches = $peer.scenarioCount -eq $expectedNames.Count
    if ($matches) { for ($index = 0; $index -lt $expectedNames.Count; $index++) { if ($peer.scenarioNames[$index] -cne $expectedNames[$index]) { $matches = $false; break } } }
    if (-not $matches) { $case.scenarioOrderMatched = $false; $peer.success = $false; $peer.failure = "$($peer.failure) Scenario count/name/order differs from $($case.scenarioExpectationSource).".Trim() }
}
if ($requireFullPredictionContinuity) {
    $case.continuity.passedPeers = @($peers | Where-Object { $null -ne $_.continuity -and $_.continuity.success }).Count
    $case.continuity.failedPeers = $peers.Count - $case.continuity.passedPeers
    $case.continuity.success = $case.continuity.failedPeers -eq 0
}
$case.success = -not $case.failure -and $case.scenarioOrderMatched -and @($peers | Where-Object { -not $_.success }).Count -eq 0
$case.status = if ($case.success) { 'passed' } else { 'failed' }
$case.elapsedSeconds = $timer.Elapsed.TotalSeconds
$case.peers = @($peers | ForEach-Object { Export-Peer $_ })
Write-Json (Join-Path $runDirectory 'case.json') $case
$peers | Select-Object name,role,processId,exitCode,success,timedOut,killedByRunner,scenarioCount,failure | Export-Csv -LiteralPath (Join-Path $runDirectory 'processes.csv') -NoTypeInformation -Encoding UTF8
Write-Host "Scenario profile $($case.status): $runDirectory"
if (-not $case.success) { exit 1 }
