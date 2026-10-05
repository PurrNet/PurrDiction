# Dot-source this helper to validate full-prediction logs without launching a player.
function Get-FullPredictionScenarioNames {
    return @('PredictionBootstrap', 'BounceScenario', 'DeterministicAlignmentScenario',
        'PredictedPawnScenario', 'TickAgreementScenario', 'DeterministicGauntletScenario',
        'ProjectileChainScenario')
}

function Test-FullPredictionContinuity {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$true)][string]$LogPath,
        [Parameter(Mandatory=$true)][ValidateSet('server','host','client')][string]$Role
    )
    $failures = [Collections.Generic.List[string]]::new()
    $boundaries = [Collections.Generic.List[object]]::new()
    $checkpoints = [Collections.Generic.List[object]]::new()
    $resyncs = [Collections.Generic.List[object]]::new()
    $logErrors = [Collections.Generic.List[object]]::new()
    $traceCount = 0
    $logHash = $null
    $firstScenarioLine = $null
    $finalPassLine = $null
    $names = @(Get-FullPredictionScenarioNames)
    $expectedRole = @{server='Server';host='Host';client='Client'}[$Role]
    $boundaryPattern = '^\[PredictionTests\] (Server|Host|Client) (starting|finished) scenario (\d+): (\w+)(?: (PASS|FAIL)(?: \([^\r\n]*\))?)?$'
    $sendPattern = '^\[PhysicsEventTrace\] fullSend player=(\S+) current=(\d+) baseline=(\d+) distressed=(True|False) eventHistory=(True|False)$'
    $resetPattern = '^\[PhysicsEventTrace\] fullReset previous=(\d+) current=(\d+)$'
    $resyncPattern = '^\[HistoryResyncTrace\] resync(Request|Serve|Coalesced|Covered) player=(\S+) failedTick=(\d+) current=(\d+) previousLatch=(\d+) previousAck=(\d+) coveringFullTick=(\d+)$'
    $eventPattern = '^\[PhysicsEventTrace\] phase=(capture|dispatch) tick=\d+ dimension=[23] type=(Enter|Exit) trigger=(True|False) me=PredictedID\(\d+, \d+\) other=PredictedID\(\d+, \d+\)(?: speed=\S+ resolved=(True|False))?$'
    $errorPattern = '^(?:\[PredictionManager\] Cannot apply prediction frame \d+:|UnityEngine\.Debug:Log(?:Error|Exception)(?:Format)?\b|(?:[\w.]+)?Exception:|Unhandled (?:Exception|exception)|Fatal error|Crash!!!)'
    try {
        $lines = [IO.File]::ReadAllLines([IO.Path]::GetFullPath($LogPath))
        $logHash = (Get-FileHash -LiteralPath $LogPath -Algorithm SHA256).Hash
        for ($index = 0; $index -lt $lines.Length; $index++) {
            $line = $lines[$index]
            $number = $index + 1
            $boundary = [regex]::Match($line, $boundaryPattern)
            if ($boundary.Success) {
                $boundaries.Add([pscustomobject]@{line=$number;role=$boundary.Groups[1].Value;
                    phase=$boundary.Groups[2].Value;index=[int]$boundary.Groups[3].Value;
                    name=$boundary.Groups[4].Value;result=$boundary.Groups[5].Value})
            } elseif ($line -cmatch '^\[PredictionTests\].*(starting|finished) scenario') {
                $failures.Add("Malformed scenario boundary at line ${number}.")
            }
            if ($line -cmatch $errorPattern) {
                $logErrors.Add([pscustomobject]@{line=$number;message=$line})
            }
            if ($line -cnotmatch '^\[(PhysicsEventTrace|HistoryResyncTrace)\]') { continue }
            $traceCount++
            $send = [regex]::Match($line, $sendPattern)
            $reset = [regex]::Match($line, $resetPattern)
            $resync = [regex]::Match($line, $resyncPattern)
            if ($send.Success) {
                $checkpoints.Add([pscustomobject]@{line=$number;kind='send';player=$send.Groups[1].Value;
                    current=[ulong]$send.Groups[2].Value;previous=[ulong]$send.Groups[3].Value;classification=$null})
            } elseif ($reset.Success) {
                $checkpoints.Add([pscustomobject]@{line=$number;kind='reset';player=$null;
                    current=[ulong]$reset.Groups[2].Value;previous=[ulong]$reset.Groups[1].Value;classification=$null})
            } elseif ($resync.Success) {
                $resyncs.Add([pscustomobject]@{line=$number;phase=$resync.Groups[1].Value;player=$resync.Groups[2].Value;
                    failedTick=[ulong]$resync.Groups[3].Value;current=[ulong]$resync.Groups[4].Value;classification=$null})
            } elseif ($line -cnotmatch $eventPattern) {
                $failures.Add("Malformed or unknown recovery trace at line ${number}.")
            }
        }
        # Require the whole inventory, including the middle boundaries: a final PASS alone
        # cannot prove that earlier scenarios ran or that recovery traces cover their interval.
        $validBoundaries = $boundaries.Count -eq 2 * $names.Count
        if ($validBoundaries) {
            for ($index = 0; $index -lt $names.Count; $index++) {
                $start = $boundaries[2 * $index]
                $finish = $boundaries[2 * $index + 1]
                if ($start.role -cne $expectedRole -or $finish.role -cne $expectedRole -or
                    $start.phase -cne 'starting' -or $finish.phase -cne 'finished' -or
                    $start.index -ne $index -or $finish.index -ne $index -or
                    $start.name -cne $names[$index] -or $finish.name -cne $names[$index] -or
                    $start.result -cne '' -or $finish.result -cne 'PASS') {
                    $validBoundaries = $false
                    break
                }
            }
        }
        if ($validBoundaries) {
            $firstScenarioLine = $boundaries[2].line
            $finalPassLine = $boundaries[$boundaries.Count - 1].line
        } else {
            $failures.Add('Missing, malformed, failed, or out-of-order scenario boundaries; expected all seven scenarios exactly once.')
        }
        # Physics events and recovery share the same runtime trace limit.
        if ($traceCount -le 0 -or $traceCount -ge 512) {
            $failures.Add("Recovery traces are absent or reached their shared 512-entry cap (count=$traceCount).")
        }
        foreach ($checkpoint in $checkpoints) {
            if ($validBoundaries -and $checkpoint.line -gt $finalPassLine) {
                $checkpoint.classification = 'post-final-pass-teardown'
            } elseif ($validBoundaries -and $checkpoint.previous -eq 0 -and $checkpoint.line -lt $firstScenarioLine) {
                $checkpoint.classification = 'initial-bootstrap'
            } else {
                $checkpoint.classification = 'unexpected-recovery'
                $failures.Add("Unexpected full checkpoint $($checkpoint.kind) at line $($checkpoint.line), tick $($checkpoint.current), previous/baseline $($checkpoint.previous).")
            }
        }
        foreach ($resync in $resyncs) {
            if ($validBoundaries -and $resync.line -gt $finalPassLine) {
                $resync.classification = 'post-final-pass-teardown'
            } else {
                $resync.classification = 'unexpected-recovery'
                $failures.Add("Unexpected history resync $($resync.phase) at line $($resync.line), failed tick $($resync.failedTick).")
            }
        }
        if ($logErrors.Count) { $failures.Add("Log contains $($logErrors.Count) error or exception records.") }
    } catch {
        $failures.Add("Cannot validate continuity: $($_.Exception.Message)")
    }
    return [pscustomobject]@{required=$true;success=$failures.Count -eq 0;logPath=$LogPath;logSha256=$logHash;
        firstScenarioLine=$firstScenarioLine;finalPassLine=$finalPassLine;traceCount=$traceCount;
        boundaries=$boundaries.ToArray();checkpoints=$checkpoints.ToArray();resyncs=$resyncs.ToArray();
        logErrors=$logErrors.ToArray();failures=$failures.ToArray()}
}
