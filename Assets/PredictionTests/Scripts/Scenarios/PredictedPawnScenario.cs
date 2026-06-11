using System;
using Cysharp.Threading.Tasks;
using PurrNet;
using PurrNet.Prediction;
using UnityEngine;

public class PredictedPawnScenario : Scenario
{
    [SerializeField] private DeterministicTickCounter _counter;
    [SerializeField] private PredictedPlayerSpawner _playerSpawner;
    [SerializeField] private float _timeout = 90f;
    [SerializeField] private float _settleSeconds = 3f;

    private const int DigestChannel = 200;

    public override void Setup(ScenarioContext ctx, NetworkManager manager)
    {
        var pawn = PredictionTestUtils.CreatePrefab<PawnIdentity>("PredictedPawn");
        PredictionTestUtils.RegisterPrefab(ctx, pawn);
        PawnIdentity.pawnPrefab = pawn;
        _playerSpawner.playerPrefab = pawn;

        var projectile = PredictionTestUtils.CreatePrefab<PredictedMarker>("PawnProjectile");
        PredictionTestUtils.RegisterPrefab(ctx, projectile);
        PawnIdentity.projectilePrefab = projectile;
    }

    public override async UniTask<ScenarioResult> RunScenario(ScenarioContext ctx)
    {
        var pm = ctx.predictionManager;
        pm.TryGetPrefab(PawnIdentity.pawnPrefab, out var pawnPrefabId);

        int expectedPawns = ctx.expectedConnections;

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => PredictionTestUtils.CountInstances(pm, pawnPrefabId) >= expectedPawns,
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
                $"pawn timeout: pawns={PredictionTestUtils.CountInstances(pm, pawnPrefabId)}/{expectedPawns} stable={PawnIdentity.AllStable(pm, pawnPrefabId)}");
        }

        await UniTask.WaitForSeconds(_settleSeconds, cancellationToken: ctx.cancellationToken);

        var digest = PredictionTestUtils.WorldDigest(ctx, _counter);
        return await DigestExchange.Compare(ctx, DigestChannel, digest, 30f);
    }
}
