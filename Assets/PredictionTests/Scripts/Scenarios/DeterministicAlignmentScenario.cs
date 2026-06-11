using System;
using Cysharp.Threading.Tasks;
using PurrNet;
using UnityEngine;

public class DeterministicAlignmentScenario : Scenario
{
    [SerializeField] private DeterministicTickCounter _counter;
    [SerializeField] private DeterministicTimedSpawner _spawner;
    [SerializeField] private float _timeout = 60f;
    [SerializeField] private float _settleSeconds = 2f;

    private const int DigestChannel = 100;

    public override void Setup(ScenarioContext ctx, NetworkManager manager)
    {
        var marker = PredictionTestUtils.CreatePrefab<PredictedMarker>("TimedMarker");
        PredictionTestUtils.RegisterPrefab(ctx, marker);
        _spawner.markerPrefab = marker;
    }

    public override async UniTask<ScenarioResult> RunScenario(ScenarioContext ctx)
    {
        var pm = ctx.predictionManager;
        pm.TryGetPrefab(PawnIdentity.pawnPrefab, out var pawnPrefabId);

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => _spawner.spawnedCount >= _spawner.spawnsPerWave,
                _timeout,
                ctx.cancellationToken);

            await UniTaskUtils.WaitWithTimeout(
                () => PawnIdentity.AllStable(pm, pawnPrefabId),
                _timeout,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail(
                $"quiesce timeout: spawned={_spawner.spawnedCount}/{_spawner.spawnsPerWave} pawnsStable={PawnIdentity.AllStable(pm, pawnPrefabId)}");
        }

        await UniTask.WaitForSeconds(_settleSeconds, cancellationToken: ctx.cancellationToken);

        var digest = PredictionTestUtils.WorldDigest(ctx, _counter);
        return await DigestExchange.Compare(ctx, DigestChannel, digest, 30f);
    }
}
