using System;
using Cysharp.Threading.Tasks;
using PurrNet;
using PurrNet.Transports;
using UnityEngine;

public static class ReconnectSignals
{
    public static ulong victimId;
    public static bool victimReceived;
    public static bool victimRejoined;

    [ObserversRpc(runLocally: true)]
    public static void BroadcastVictim(ulong playerId)
    {
        victimId = playerId;
        victimReceived = true;
    }

    [ServerRpc(requireOwnership: false)]
    public static void ReportVictimRejoined()
    {
        victimRejoined = true;
    }
}

public class ReconnectScenario : Scenario
{
    [SerializeField] private DeterministicTickCounter _counter;
    [SerializeField] private DeterministicTimedSpawner _spawner;
    [SerializeField] private float _timeout = 120f;
    [SerializeField] private float _reconnectTimeout = 30f;
    [SerializeField] private float _stayDisconnectedSeconds = 1f;
    [SerializeField] private float _settleSeconds = 3f;

    private const int DigestChannel = 300;

    public override async UniTask<ScenarioResult> RunScenario(ScenarioContext ctx)
    {
        var choreography = ctx.isServer
            ? await RunAsServer(ctx)
            : await RunAsClient(ctx);

        if (!choreography.success)
            return choreography;

        return await FinishAndCompare(ctx);
    }

    private async UniTask<ScenarioResult> RunAsServer(ScenarioContext ctx)
    {
        var victim = PickVictim(ctx);
        if (!victim.HasValue)
            return ScenarioResult.Fail("no eligible client to disconnect");

        var victimId = victim.Value.id.value;
        ReconnectSignals.victimRejoined = false;
        ReconnectSignals.BroadcastVictim(victimId);

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => ReconnectSignals.victimRejoined,
                _reconnectTimeout + _stayDisconnectedSeconds,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail(
                $"victim {victimId} never reported back after the disconnect/reconnect cycle " +
                $"(still connected: {IsPlayerConnected(ctx, victimId)})");
        }

        return ScenarioResult.Ok();
    }

    private async UniTask<ScenarioResult> RunAsClient(ScenarioContext ctx)
    {
        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => ReconnectSignals.victimReceived,
                _reconnectTimeout,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail("victim broadcast never arrived");
        }

        var manager = ctx.networkManager;
        bool isVictim = ctx.role == NetworkRole.Client
                        && manager.isLocalPlayerReady
                        && manager.localPlayer.id.value == ReconnectSignals.victimId;

        if (isVictim)
        {
            manager.StopClient();

            await UniTaskUtils.WaitWithTimeout(
                () => manager.clientState == ConnectionState.Disconnected,
                _reconnectTimeout,
                ctx.cancellationToken);

            await UniTask.WaitForSeconds(_stayDisconnectedSeconds, cancellationToken: ctx.cancellationToken);

            manager.StartClient();

            await UniTaskUtils.WaitWithTimeout(
                () => manager.isClient && manager.isLocalPlayerReady,
                _reconnectTimeout,
                ctx.cancellationToken);

            var pm = ctx.predictionManager;
            await UniTaskUtils.WaitWithTimeout(
                () => pm && pm.isSpawned,
                _reconnectTimeout,
                ctx.cancellationToken);

            var startTick = pm.localTick;
            await UniTaskUtils.WaitWithTimeout(
                () => pm.localTick > startTick + 5,
                _reconnectTimeout,
                ctx.cancellationToken);

            ReconnectSignals.ReportVictimRejoined();
        }

        return ScenarioResult.Ok();
    }

    private async UniTask<ScenarioResult> FinishAndCompare(ScenarioContext ctx)
    {
        var pm = ctx.predictionManager;
        pm.TryGetPrefab(PawnIdentity.pawnPrefab, out var pawnPrefabId);

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => _spawner.spawnedCount >= _spawner.totalSpawns,
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
                $"quiesce timeout: spawned={_spawner.spawnedCount}/{_spawner.totalSpawns} pawnsStable={PawnIdentity.AllStable(pm, pawnPrefabId)}");
        }

        await UniTask.WaitForSeconds(_settleSeconds, cancellationToken: ctx.cancellationToken);

        var digest = PredictionTestUtils.WorldDigest(ctx, _counter);
        return await DigestExchange.Compare(ctx, DigestChannel, digest, 30f);
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

    private static bool IsPlayerConnected(ScenarioContext ctx, ulong playerId)
    {
        var players = ctx.networkManager.players;
        for (int i = 0; i < players.Count; i++)
            if (players[i].id.value == playerId) return true;
        return false;
    }
}
