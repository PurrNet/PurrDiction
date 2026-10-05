using System;
using System.Text;
using Cysharp.Threading.Tasks;
using PurrNet;
using PurrNet.Prediction;
using UnityEngine;

public static class DesyncCorrectionSignals
{
    public static ulong victimId;
    public static bool victimReceived;
    public static bool victimDone;
    public static bool cycleComplete;

    [ObserversRpc(runLocally: true)]
    public static void BroadcastVictim(ulong playerId)
    {
        victimId = playerId;
        victimReceived = true;
    }

    [ServerRpc(requireOwnership: false)]
    public static void ReportVictimDone()
    {
        victimDone = true;
    }

    [ObserversRpc(runLocally: true)]
    public static void BroadcastCycleComplete()
    {
        cycleComplete = true;
    }
}

public class DesyncCorrectionScenario : Scenario
{
    [SerializeField] private float _timeout = 60f;
    [SerializeField] private float _settleSeconds = 6f;

    private const int DigestChannel = 1400;
    private const ulong CorruptionOffset = 40;

    private GameObject _probePrefab;
    private int _probePrefabId;
    private int _serverDetections;
    private int _correctionsApplied;
    private int _localDesyncs;
    private int _unexpectedDesyncs;
    private ulong _fullFramesAtStart;
    private ulong _faultStartsAt;
    private DesyncProbe _observedProbe;

    public override void Setup(ScenarioContext ctx, NetworkManager manager)
    {
        _probePrefab = PredictionTestUtils.CreatePrefab<DesyncProbe>("DesyncProbe");
        _probePrefab.GetComponent<DesyncProbe>().desyncPolicy = DesyncPolicyOverride.Correct;
        PredictionTestUtils.RegisterPrefab(ctx, _probePrefab);
    }

    public override async UniTask<ScenarioResult> RunScenario(ScenarioContext ctx)
    {
        var pm = ctx.predictionManager;
        pm.TryGetPrefab(_probePrefab, out _probePrefabId);

        _serverDetections = 0;
        _correctionsApplied = 0;
        _localDesyncs = 0;
        _unexpectedDesyncs = 0;
        _fullFramesAtStart = pm.fullFramesReceivedTotal;
        _faultStartsAt = 0;
        _observedProbe = null;

        Action<PredictedIdentity, PlayerID, ulong, DesyncPolicy> onDetected = (identity, player, tick, policy) =>
        {
            if (!(identity is DesyncProbe))
            {
                _unexpectedDesyncs++;
                return;
            }
            _serverDetections++;
            if (policy == DesyncPolicy.Correct)
                _correctionsApplied++;
        };
        Action<PredictedIdentity, ulong, DesyncPolicy> onLocal = (identity, tick, policy) =>
        {
            if (!(identity is DesyncProbe probe))
            {
                _unexpectedDesyncs++;
                return;
            }
            _localDesyncs++;
            probe.corruptFromTick = 0;
        };

        pm.onDesyncDetected += onDetected;
        pm.onLocalDesync += onLocal;

        try
        {
            return await Run(ctx, pm);
        }
        finally
        {
            // Bound fault injection to this scenario even if its assertions time out.
            if (_observedProbe)
                _observedProbe.corruptFromTick = 0;
            pm.onDesyncDetected -= onDetected;
            pm.onLocalDesync -= onLocal;
        }
    }

    private async UniTask<ScenarioResult> Run(ScenarioContext ctx, PredictionManager pm)
    {
        if (ctx.isServer)
        {
            if (!pm.hierarchy.Create(_probePrefab, Vector3.zero, Quaternion.identity).HasValue)
                return ScenarioResult.Fail("failed to create desync probe");
        }

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => FindProbe(pm) != null,
                _timeout,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail("desync probe never appeared");
        }

        _observedProbe = FindProbe(pm);

        await UniTask.WaitForSeconds(1f, cancellationToken: ctx.cancellationToken);

        if (ctx.isServer)
        {
            var victim = PickVictim(ctx);
            if (!victim.HasValue)
                return ScenarioResult.Fail("no eligible client to corrupt");

            DesyncCorrectionSignals.BroadcastVictim(victim.Value.id.value);

            try
            {
                await UniTaskUtils.WaitWithTimeout(
                    () => DesyncCorrectionSignals.victimDone && _serverDetections > 0,
                    _timeout,
                    ctx.cancellationToken);
            }
            catch (TimeoutException)
            {
                return ScenarioResult.Fail(
                    $"server never observed the desync: victimDone={DesyncCorrectionSignals.victimDone}; {Report(pm)}");
            }

            if (_correctionsApplied == 0)
                return ScenarioResult.Fail($"the server did not apply the Correct policy: {Report(pm)}");

            DesyncCorrectionSignals.BroadcastCycleComplete();
        }
        else
        {
            try
            {
                await UniTaskUtils.WaitWithTimeout(
                    () => DesyncCorrectionSignals.victimReceived,
                    _timeout,
                    ctx.cancellationToken);
            }
            catch (TimeoutException)
            {
                return ScenarioResult.Fail("victim broadcast never arrived");
            }

            var manager = ctx.networkManager;
            bool isVictim = ctx.role == NetworkRole.Client
                            && manager.isLocalPlayerReady
                            && manager.localPlayer.id.value == DesyncCorrectionSignals.victimId;

            if (isVictim)
            {
                var probe = FindProbe(pm);
                if (probe == null)
                    return ScenarioResult.Fail("victim lost the desync probe");

                _faultStartsAt = pm.localTick + CorruptionOffset;
                probe.corruptFromTick = _faultStartsAt;

                try
                {
                    await UniTaskUtils.WaitWithTimeout(
                        () => _localDesyncs > 0,
                        _timeout,
                        ctx.cancellationToken);
                }
                catch (TimeoutException)
                {
                    return ScenarioResult.Fail(
                        $"corruption was never detected: {Report(pm)}");
                }

                if (probe.corruptionsApplied == 0)
                    return ScenarioResult.Fail($"the victim received a notice without injecting its fault: {Report(pm)}");

                DesyncCorrectionSignals.ReportVictimDone();
            }

            try
            {
                await UniTaskUtils.WaitWithTimeout(
                    () => DesyncCorrectionSignals.cycleComplete,
                    _timeout,
                    ctx.cancellationToken);
            }
            catch (TimeoutException)
            {
                return ScenarioResult.Fail($"cycle-complete broadcast never arrived: {Report(pm)}");
            }

            if (!isVictim && _localDesyncs > 0)
                return ScenarioResult.Fail($"bystander client was flagged as diverged {_localDesyncs} times");
        }

        await UniTask.WaitForSeconds(_settleSeconds, cancellationToken: ctx.cancellationToken);
        await PredictionTestUtils.AlignDigestTick(ctx, DigestChannel, _timeout);

        var digest = BuildDigest(ctx);
        var digestResult = await DigestExchange.Compare(ctx, DigestChannel, digest, 30f);
        if (!digestResult.success)
            return ScenarioResult.Fail($"{digestResult.message}; {Report(pm)}");

        if (_unexpectedDesyncs > 0)
            return ScenarioResult.Fail($"an unrelated identity diverged during the test: {Report(pm)}");

        var report = Report(pm);
        Debug.Log($"[DesyncCorrection] {ctx.role} {report}");
        return ScenarioResult.Ok(report);
    }

    private string Report(PredictionManager pm)
        => $"detections={_serverDetections}; corrections={_correctionsApplied}; localDesyncs={_localDesyncs}; " +
           $"unexpectedDesyncs={_unexpectedDesyncs}; corruptions={(_observedProbe ? _observedProbe.corruptionsApplied : 0)}; " +
           $"verifiedCorruptions={(_observedProbe ? _observedProbe.verifiedCorruptionsApplied : 0)}; " +
           $"faultStartsAt={_faultStartsAt}; firstCorruptionTick={(_observedProbe ? _observedProbe.firstCorruptionTick : 0)}; " +
           $"lastCorruptionTick={(_observedProbe ? _observedProbe.lastCorruptionTick : 0)}; " +
           $"fullFramesReceived={pm.fullFramesReceivedTotal - _fullFramesAtStart}";

    private DesyncProbe FindProbe(PredictionManager pm)
    {
        ref var state = ref pm.hierarchy.currentState;
        for (var i = 0; i < state.spawnedPrefabs.Count; i++)
        {
            var details = state.spawnedPrefabs[i];
            if (details.prefabId == _probePrefabId &&
                details.instanceId.TryGetComponent<DesyncProbe>(pm, out var probe))
                return probe;
        }
        return null;
    }

    private string BuildDigest(ScenarioContext ctx)
    {
        var pm = ctx.predictionManager;
        var sb = new StringBuilder();
        sb.Append($"probes={PredictionTestUtils.CountInstances(pm, _probePrefabId)};");
        PredictionTestUtils.AppendIdentities<DesyncProbe>(pm, _probePrefabId, sb, probe => probe.Digest());
        return sb.ToString();
    }

    private static PlayerID? PickVictim(ScenarioContext ctx)
    {
        var manager = ctx.networkManager;
        var hostLocal = manager.isLocalPlayerReady && ctx.role == NetworkRole.Host
            ? manager.localPlayer
            : (PlayerID?)null;

        PlayerID? best = null;
        var players = manager.players;
        for (int i = 0; i < players.Count; i++)
        {
            var p = players[i];
            if (p.isServer) continue;
            if (hostLocal.HasValue && hostLocal.Value == p) continue;
            if (!best.HasValue || p.id.value < best.Value.id.value)
                best = p;
        }
        return best;
    }
}
