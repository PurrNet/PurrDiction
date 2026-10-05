<#
.SYNOPSIS
Runs offline continuity-checker regressions and optional preserved-run comparisons. Never launches Unity.
.EXAMPLE
./test-prediction-scenario-continuity.ps1
.EXAMPLE
./test-prediction-scenario-continuity.ps1 -RejectedRunDirectories <v4-server>,<v4-host> -AcceptedRunDirectories <v5-server>,<v5-host>
#>
[CmdletBinding()]
param(
    [string[]]$RejectedRunDirectories = @(),
    [string[]]$AcceptedRunDirectories = @(),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'test-results/continuity-checker')
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'prediction-scenario-continuity.ps1')
$runId = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8)
$output = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) $runId
$null = New-Item -ItemType Directory -Path $output
$checks = [Collections.Generic.List[object]]::new()
$names = @('PredictionBootstrap', 'BounceScenario', 'DeterministicAlignmentScenario',
    'PredictedPawnScenario', 'TickAgreementScenario', 'DeterministicGauntletScenario', 'ProjectileChainScenario')
$initial = '[PhysicsEventTrace] fullReset previous=0 current=100'
$tail = '[PhysicsEventTrace] fullReset previous=200 current=210'
$event = '[PhysicsEventTrace] phase=dispatch tick=120 dimension=3 type=Enter trigger=False me=PredictedID(28, 1) other=PredictedID(0, 0) speed=9.3 resolved=True'
$resync = '[HistoryResyncTrace] resyncRequest player=1 failedTick=200 current=205 previousLatch=0 previousAck=199 coveringFullTick=0'
$base = @('[PredictionTests] Client starting scenario 0: PredictionBootstrap', $initial,
    '[PredictionTests] Client finished scenario 0: PredictionBootstrap PASS (5 ms)',
    '[PredictionTests] Client starting scenario 1: BounceScenario',
    '[PredictionTests] Client finished scenario 1: BounceScenario PASS (5 ms)',
    '[PredictionTests] Client starting scenario 2: DeterministicAlignmentScenario',
    '[PredictionTests] Client finished scenario 2: DeterministicAlignmentScenario PASS (5 ms)',
    '[PredictionTests] Client starting scenario 3: PredictedPawnScenario',
    '[PredictionTests] Client finished scenario 3: PredictedPawnScenario PASS (5 ms)',
    '[PredictionTests] Client starting scenario 4: TickAgreementScenario',
    '[PredictionTests] Client finished scenario 4: TickAgreementScenario PASS (5 ms)',
    '[PredictionTests] Client starting scenario 5: DeterministicGauntletScenario',
    '[PredictionTests] Client finished scenario 5: DeterministicGauntletScenario PASS (5 ms)',
    '[PredictionTests] Client starting scenario 6: ProjectileChainScenario',
    '[PredictionTests] Client finished scenario 6: ProjectileChainScenario PASS (5 ms)')

function Check-Log([string]$Name, [string[]]$Lines, [bool]$Expected, [string]$Reason = '', [string]$Role = 'client') {
    $path = Join-Path $output ($Name + '.log')
    [IO.File]::WriteAllLines($path, $Lines)
    $result = Test-FullPredictionContinuity -LogPath $path -Role $Role
    $reasonMatches = -not $Reason -or [bool]($result.failures | Where-Object { $_ -like "*$Reason*" })
    $checks.Add([pscustomobject]@{name=$Name;success=$result.success -eq $Expected -and $reasonMatches;
        expected=$Expected;actual=$result.success;expectedReason=$Reason;validation=$result})
}
function Add-Line([string[]]$Lines, [int]$At, [string]$Line) {
    $copy = [Collections.Generic.List[string]]::new()
    $copy.AddRange($Lines)
    $copy.Insert($At, $Line)
    return $copy.ToArray()
}

Check-Log 'uninterrupted' $base $true
Check-Log 'bootstrap-and-post-pass-checkpoints' ($base + $tail) $true
Check-Log 'post-pass-send' ($base + '[PhysicsEventTrace] fullSend player=2 current=210 baseline=200 distressed=False eventHistory=True') $true
Check-Log 'post-pass-resync' ($base + $resync) $true
Check-Log 'bootstrap-after-bootstrap-pass' (Add-Line ($base | Where-Object { $_ -cne $initial }) 2 $initial) $true
Check-Log 'reset-during-first-scenario-even-from-zero' (Add-Line $base 4 $initial) $false 'Unexpected full checkpoint'
Check-Log 'checkpoint-between-scenarios' (Add-Line $base 7 $tail) $false 'Unexpected full checkpoint'
Check-Log 'nonzero-checkpoint-before-first-scenario' (Add-Line $base 1 $tail) $false 'Unexpected full checkpoint'
Check-Log 'checkpoint-before-final-pass-cannot-hide-in-tail' ((Add-Line $base 14 $tail) + $tail) $false 'Unexpected full checkpoint'
Check-Log 'send-during-scenario' (Add-Line $base 14 '[PhysicsEventTrace] fullSend player=2 current=210 baseline=200 distressed=False eventHistory=True') $false 'Unexpected full checkpoint'
Check-Log 'resync-without-completed-checkpoint' (Add-Line $base 14 $resync) $false 'Unexpected history resync'
Check-Log 'missing-traces' ($base | Where-Object { $_ -cne $initial }) $false 'traces are absent'
Check-Log 'capped-shared-traces' ($base + @($event) * 511) $false '512-entry cap'
Check-Log 'uncapped-shared-traces' ($base + @($event) * 510) $true
Check-Log 'malformed-trace' ($base -creplace 'previous=0', 'previous=unknown') $false 'Malformed or unknown recovery trace'
Check-Log 'unknown-trace' ($base + '[PhysicsEventTrace] unrecognizedRecord') $false 'Malformed or unknown recovery trace'
Check-Log 'overflowing-trace-tick' ($base -creplace 'current=100', 'current=9999999999999999999999999') $false 'Cannot validate continuity'
Check-Log 'missing-final-pass' $base[0..13] $false 'scenario boundaries'
Check-Log 'missing-middle-pair' ($base | Where-Object { $_ -cnotmatch 'scenario 3:' }) $false 'scenario boundaries'
Check-Log 'duplicate-middle-boundary' (Add-Line $base 7 $base[7]) $false 'scenario boundaries'
$swapped = $base.Clone(); $swapped[7] = $base[8]; $swapped[8] = $base[7]
Check-Log 'reversed-middle-boundaries' $swapped $false 'scenario boundaries'
Check-Log 'wrong-scenario-name' ($base -creplace 'PredictedPawnScenario', 'UnexpectedScenario') $false 'scenario boundaries'
Check-Log 'wrong-role' $base $false 'scenario boundaries' 'host'
Check-Log 'failed-middle-scenario' ($base -creplace 'PredictedPawnScenario PASS', 'PredictedPawnScenario FAIL') $false 'scenario boundaries'
Check-Log 'malformed-middle-boundary' ($base -creplace 'starting scenario 3:', 'starting scenario invalid:') $false 'Malformed scenario boundary'
Check-Log 'overflowing-boundary-index' ($base -creplace 'scenario 3:', 'scenario 9999999999999999999999999:') $false 'Cannot validate continuity'
Check-Log 'prediction-apply-error' (Add-Line $base 14 '[PredictionManager] Cannot apply prediction frame 200: Missing input. A full sync was requested for the missing baseline.') $false 'error or exception'
Check-Log 'exception-without-stack' (Add-Line $base 14 'NullReferenceException: fixture failure') $false 'error or exception'
Check-Log 'unity-error-stack' (Add-Line $base 14 'UnityEngine.Debug:LogError (object)') $false 'error or exception'
foreach ($role in @('server','host')) {
    $displayRole = if ($role -ceq 'server') { 'Server' } else { 'Host' }
    $roleLog = $base -creplace 'Client', $displayRole
    $roleLog[1] = '[PhysicsEventTrace] fullSend player=2 current=100 baseline=0 distressed=False eventHistory=True'
    Check-Log ($role + '-bootstrap') $roleLog $true '' $role
}
$missing = Test-FullPredictionContinuity -LogPath (Join-Path $output 'absent.log') -Role client
$checks.Add([pscustomobject]@{name='missing-log';success=-not $missing.success;validation=$missing})

# Exercise the production launch-plan integration without starting a process. Flags are
# case-sensitive just like CommandLineUtils; recovery profiles do not inherit this oracle.
$dryProfiles = @(
    @{name='full-auto-trace';arguments=@('-fullPredictionCadenceRegressionOnly');required=$true},
    @{name='full-existing-trace';arguments=@('-fullPredictionCadenceRegressionOnly','-physicsEventTrace');required=$true},
    @{name='intentional-recovery';arguments=@('-baselineRecoveryScenarioOnly');required=$false},
    @{name='case-sensitive-selection';arguments=@('-FULLPREDICTIONCADENCEREGRESSIONONLY');required=$false})
foreach ($profile in $dryProfiles) {
    $dryOutput = Join-Path $output $profile.name
    & (Join-Path $PSScriptRoot 'run-prediction-scenarios.ps1') -PlayerPath (Join-Path $output 'absent-player.exe') `
        -Mode host -TotalPlayers 4 -ExtraArguments $profile.arguments -OutputDirectory $dryOutput -DryRun
    $planDirectory = @(Get-ChildItem -LiteralPath $dryOutput -Directory)[0].FullName
    $manifest = Get-Content -LiteralPath (Join-Path $planDirectory 'manifest.json') -Raw | ConvertFrom-Json
    $case = Get-Content -LiteralPath (Join-Path $planDirectory 'case.json') -Raw | ConvertFrom-Json
    $valid = $manifest.fullPredictionContinuityRequired -eq $profile.required -and $case.continuity.required -eq $profile.required -and
        $case.status -ceq 'dry-run' -and $null -eq $case.continuity.success -and
        $manifest.continuityValidatorSha256 -ceq (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'prediction-scenario-continuity.ps1') -Algorithm SHA256).Hash
    foreach ($peer in $manifest.peers) {
        $traceArguments = @($peer.arguments | Where-Object { $_ -ceq '-physicsEventTrace' }).Count
        $valid = $valid -and $traceArguments -eq [int]$profile.required -and $null -eq $peer.processId
    }
    if ($profile.required) { $valid = $valid -and [string]::Join('|', $manifest.expectedScenarioNames) -ceq [string]::Join('|', $names) }
    $checks.Add([pscustomobject]@{name=$profile.name;success=$valid;manifest=(Join-Path $planDirectory 'manifest.json')})
}

foreach ($cohort in @(@{directories=$RejectedRunDirectories;expected=$false}, @{directories=$AcceptedRunDirectories;expected=$true})) {
    foreach ($directory in $cohort.directories) {
        $manifest = Get-Content -LiteralPath (Join-Path $directory 'manifest.json') -Raw | ConvertFrom-Json
        $rawCase = Get-Content -LiteralPath (Join-Path $directory 'case.json') -Raw | ConvertFrom-Json
        $validations = @($manifest.peers | ForEach-Object {
            Test-FullPredictionContinuity -LogPath (Join-Path $directory ($_.name + '.log')) -Role $_.role
        })
        $actual = @($validations | Where-Object { -not $_.success }).Count -eq 0
        $valid = $manifest.extraArguments -ccontains '-fullPredictionCadenceRegressionOnly' -and
            $rawCase.success -eq $true -and $validations.Count -eq $manifest.processCount -and
            $actual -eq $cohort.expected
        $checks.Add([pscustomobject]@{name=('preserved-' + [IO.Path]::GetFileName($directory));success=$valid;
            directory=$directory;rawSuccess=$rawCase.success;expected=$cohort.expected;actual=$actual;peers=$validations})
    }
}
$failed = @($checks | Where-Object { -not $_.success })
$report = [pscustomobject]@{success=$failed.Count -eq 0;passed=$checks.Count - $failed.Count;failed=$failed.Count;
    checkerSha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'prediction-scenario-continuity.ps1') -Algorithm SHA256).Hash;
    regressionSha256=(Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash;
    failedChecks=@($failed | ForEach-Object { $_.name });checks=$checks.ToArray()}
$report | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $output 'validation.json') -Encoding UTF8
Write-Host "Continuity checker: $($report.passed) passed, $($report.failed) failed. $output"
if (-not $report.success) { throw "Continuity regressions failed: $($report.failedChecks -join ', ')." }
