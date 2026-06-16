using System;
using System.Text;
using Cysharp.Threading.Tasks;
using PurrNet;
using PurrNet.Prediction;
using PurrNet.Transports;
using UnityEngine;

public static class ProjectileChainReconnectSignals
{
    public static ulong victimId;
    public static bool victimReceived;
    public static bool victimRejoined;
    public static bool cycleComplete;

    public static void Reset()
    {
        victimId = 0;
        victimReceived = false;
        victimRejoined = false;
        cycleComplete = false;
    }

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

    [ObserversRpc(runLocally: true)]
    public static void BroadcastCycleComplete()
    {
        cycleComplete = true;
    }
}

public class ProjectileChainReconnectScenario : Scenario
{
    [SerializeField] private DeterministicTickCounter _counter;
    [SerializeField] private float _timeout = 120f;
    [SerializeField] private float _reconnectTimeout = 30f;
    [SerializeField] private float _stayDisconnectedSeconds = 1f;
    [SerializeField] private float _settleSeconds = 3f;

    private const int DigestChannel = 800;
    private const int ReconnectAfterShots = 32;

    private GameObject _driverPrefab;
    private GameObject _projectilePrefab;
    private GameObject _muzzlePrefab;
    private GameObject _hitPrefab;

    private int _driverPrefabId;
    private int _projectilePrefabId;
    private int _muzzlePrefabId;
    private int _hitPrefabId;

    public override void Setup(ScenarioContext ctx, NetworkManager manager)
    {
        _hitPrefab = PredictionTestUtils.CreatePrefab<ProjectileChainEffect>("ProjectileChainReconnectHit");
        _hitPrefab.GetComponent<ProjectileChainEffect>().lifetimeTicks = 12;
        PredictionTestUtils.RegisterPrefab(ctx, _hitPrefab, true, 1);

        _muzzlePrefab = PredictionTestUtils.CreatePrefab<ProjectileChainEffect>("ProjectileChainReconnectMuzzle");
        _muzzlePrefab.GetComponent<ProjectileChainEffect>().lifetimeTicks = 4;
        PredictionTestUtils.RegisterPrefab(ctx, _muzzlePrefab, true, 1);

        _projectilePrefab = PredictionTestUtils.CreatePrefab<ProjectileChainProjectile>("ProjectileChainReconnectProjectile");
        _projectilePrefab.GetComponent<ProjectileChainProjectile>().hitPrefab = _hitPrefab;
        PredictionTestUtils.RegisterPrefab(ctx, _projectilePrefab, true, 1);

        _driverPrefab = PredictionTestUtils.CreatePrefab<ProjectileChainDriver>("ProjectileChainReconnectDriver");
        var driver = _driverPrefab.GetComponent<ProjectileChainDriver>();
        driver.projectilePrefab = _projectilePrefab;
        driver.muzzlePrefab = _muzzlePrefab;
        PredictionTestUtils.RegisterPrefab(ctx, _driverPrefab);
    }

    public override async UniTask<ScenarioResult> RunScenario(ScenarioContext ctx)
    {
        ProjectileChainReconnectSignals.Reset();

        var pm = ctx.predictionManager;
        pm.TryGetPrefab(_driverPrefab, out _driverPrefabId);
        pm.TryGetPrefab(_projectilePrefab, out _projectilePrefabId);
        pm.TryGetPrefab(_muzzlePrefab, out _muzzlePrefabId);
        pm.TryGetPrefab(_hitPrefab, out _hitPrefabId);

        if (!pm.hierarchy.Create(_driverPrefab).HasValue)
            return ScenarioResult.Fail("failed to create projectile reconnect driver");

        var choreography = await RunSplit(ctx, RunAsClient, RunAsServer);
        if (!choreography.success)
            return choreography;

        return await FinishAndCompare(ctx);
    }

    private async UniTask<ScenarioResult> RunAsServer(ScenarioContext ctx)
    {
        var pm = ctx.predictionManager;
        var victim = PickVictim(ctx);
        if (!victim.HasValue)
            return ScenarioResult.Fail("no eligible client to disconnect during projectile burst");

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => DriverShots(pm) >= ReconnectAfterShots
                      && PredictionTestUtils.CountInstances(pm, _projectilePrefabId) > 0,
                _timeout,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail(
                $"projectile burst never became active: shots={DriverShots(pm)} projectiles={PredictionTestUtils.CountInstances(pm, _projectilePrefabId)}");
        }

        ProjectileChainReconnectSignals.BroadcastVictim(victim.Value.id.value);

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => ProjectileChainReconnectSignals.victimRejoined,
                _reconnectTimeout + _stayDisconnectedSeconds,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail(
                $"victim {victim.Value.id.value} never rejoined during projectile burst " +
                $"(still connected: {IsPlayerConnected(ctx, victim.Value.id.value)})");
        }

        ProjectileChainReconnectSignals.BroadcastCycleComplete();
        return ScenarioResult.Ok();
    }

    private async UniTask<ScenarioResult> RunAsClient(ScenarioContext ctx)
    {
        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => ProjectileChainReconnectSignals.victimReceived,
                _reconnectTimeout,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail("projectile reconnect victim broadcast never arrived");
        }

        var manager = ctx.networkManager;
        bool isVictim = ctx.role == NetworkRole.Client
                        && manager.isLocalPlayerReady
                        && manager.localPlayer.id.value == ProjectileChainReconnectSignals.victimId;

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

            ProjectileChainReconnectSignals.ReportVictimRejoined();
        }

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => ProjectileChainReconnectSignals.cycleComplete,
                _reconnectTimeout + _stayDisconnectedSeconds,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail("projectile reconnect cycle-complete broadcast never arrived");
        }

        return ScenarioResult.Ok();
    }

    private async UniTask<ScenarioResult> FinishAndCompare(ScenarioContext ctx)
    {
        var pm = ctx.predictionManager;
        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => DriverFinished(pm)
                      && PredictionTestUtils.CountInstances(pm, _projectilePrefabId) == 0
                      && PredictionTestUtils.CountInstances(pm, _muzzlePrefabId) == 0
                      && PredictionTestUtils.CountInstances(pm, _hitPrefabId) == 0,
                _timeout,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail(
                $"projectile reconnect timeout: shots={DriverShots(pm)} " +
                $"projectiles={PredictionTestUtils.CountInstances(pm, _projectilePrefabId)} " +
                $"muzzles={PredictionTestUtils.CountInstances(pm, _muzzlePrefabId)} " +
                $"hits={PredictionTestUtils.CountInstances(pm, _hitPrefabId)}");
        }

        await UniTask.WaitForSeconds(_settleSeconds, cancellationToken: ctx.cancellationToken);

        return await DigestExchange.Compare(ctx, DigestChannel, BuildDigest(ctx), 30f);
    }

    private bool DriverFinished(PredictionManager pm)
    {
        return DriverShots(pm) >= ProjectileChainDriver.TotalShots;
    }

    private int DriverShots(PredictionManager pm)
    {
        ref var state = ref pm.hierarchy.currentState;
        for (var i = 0; i < state.spawnedPrefabs.Count; i++)
        {
            var details = state.spawnedPrefabs[i];
            if (details.prefabId != _driverPrefabId)
                continue;

            if (details.instanceId.TryGetComponent<ProjectileChainDriver>(pm, out var driver))
                return driver.currentState.shots;
        }

        return 0;
    }

    private string BuildDigest(ScenarioContext ctx)
    {
        var pm = ctx.predictionManager;
        var sb = new StringBuilder();
        sb.Append(PredictionTestUtils.WorldDigest(ctx, _counter));
        PredictionTestUtils.AppendIdentities<ProjectileChainDriver>(pm, _driverPrefabId, sb, driver => driver.Digest());
        PredictionTestUtils.AppendIdentities<ProjectileChainProjectile>(pm, _projectilePrefabId, sb, projectile => projectile.Digest());
        PredictionTestUtils.AppendIdentities<ProjectileChainEffect>(pm, _muzzlePrefabId, sb, effect => effect.Digest());
        PredictionTestUtils.AppendIdentities<ProjectileChainEffect>(pm, _hitPrefabId, sb, effect => effect.Digest());
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

    private static bool IsPlayerConnected(ScenarioContext ctx, ulong playerId)
    {
        var players = ctx.networkManager.players;
        for (int i = 0; i < players.Count; i++)
            if (players[i].id.value == playerId) return true;
        return false;
    }
}
