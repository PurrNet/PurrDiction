using System;
using System.Globalization;
using System.Reflection;
using System.Text;
using Cysharp.Threading.Tasks;
using PurrNet;
using PurrNet.Prediction;
using UnityEngine;

public class ProjectileChainScenario : Scenario
{
    [SerializeField] private DeterministicTickCounter _counter;
    [SerializeField] private float _timeout = 90f;
    [SerializeField] private float _settleSeconds = 2f;

    private const int DigestChannel = 500;

    private GameObject _driverPrefab;
    private GameObject _projectilePrefab;
    private GameObject _muzzlePrefab;
    private GameObject _hitPrefab;

    private int _driverPrefabId;
    private int _projectilePrefabId;
    private int _muzzlePrefabId;
    private int _hitPrefabId;
    private HistoricalTrafficTrace _historicalTrafficTrace;

    private void Update() => _historicalTrafficTrace?.Sample();

    public override void Setup(ScenarioContext ctx, NetworkManager manager)
    {
        _hitPrefab = PredictionTestUtils.CreatePrefab<ProjectileChainEffect>("ProjectileChainHit");
        _hitPrefab.GetComponent<ProjectileChainEffect>().lifetimeTicks = 12;
        PredictionTestUtils.RegisterPrefab(ctx, _hitPrefab, true, 1);

        _muzzlePrefab = PredictionTestUtils.CreatePrefab<ProjectileChainEffect>("ProjectileChainMuzzle");
        _muzzlePrefab.GetComponent<ProjectileChainEffect>().lifetimeTicks = 4;
        PredictionTestUtils.RegisterPrefab(ctx, _muzzlePrefab, true, 1);

        _projectilePrefab = PredictionTestUtils.CreatePrefab<ProjectileChainProjectile>("ProjectileChainProjectile");
        _projectilePrefab.GetComponent<ProjectileChainProjectile>().hitPrefab = _hitPrefab;
        PredictionTestUtils.RegisterPrefab(ctx, _projectilePrefab, true, 1);

        _driverPrefab = PredictionTestUtils.CreatePrefab<ProjectileChainDriver>("ProjectileChainDriver");
        var driver = _driverPrefab.GetComponent<ProjectileChainDriver>();
        driver.projectilePrefab = _projectilePrefab;
        driver.muzzlePrefab = _muzzlePrefab;
        PredictionTestUtils.RegisterPrefab(ctx, _driverPrefab);
    }

    public override async UniTask<ScenarioResult> RunScenario(ScenarioContext ctx)
    {
        var pm = ctx.predictionManager;
        using var trafficTrace = StartHistoricalTrafficTrace(pm);
        pm.TryGetPrefab(_driverPrefab, out _driverPrefabId);
        pm.TryGetPrefab(_projectilePrefab, out _projectilePrefabId);
        pm.TryGetPrefab(_muzzlePrefab, out _muzzlePrefabId);
        pm.TryGetPrefab(_hitPrefab, out _hitPrefabId);

        if (!pm.hierarchy.Create(_driverPrefab).HasValue)
            return ScenarioResult.Fail("failed to create projectile chain driver");

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
                $"projectile chain timeout: drivers={PredictionTestUtils.CountInstances(pm, _driverPrefabId)} " +
                $"projectiles={PredictionTestUtils.CountInstances(pm, _projectilePrefabId)} " +
                $"muzzles={PredictionTestUtils.CountInstances(pm, _muzzlePrefabId)} " +
                $"hits={PredictionTestUtils.CountInstances(pm, _hitPrefabId)}");
        }

        trafficTrace?.SetPhase("settle");
        await UniTask.WaitForSeconds(_settleSeconds, cancellationToken: ctx.cancellationToken);

        trafficTrace?.SetPhase("digest");
        return await DigestExchange.Compare(ctx, DigestChannel, BuildDigest(ctx), 30f);
    }

    private HistoricalTrafficTrace StartHistoricalTrafficTrace(PredictionManager manager)
    {
        if (!CommandLineUtils.HasFlag("-historicalTrafficTrace"))
            return null;
        _historicalTrafficTrace = new HistoricalTrafficTrace(this, manager);
        return _historicalTrafficTrace;
    }

    private sealed class HistoricalTrafficTrace : IDisposable
    {
        private static readonly FieldInfo VerifiedTick = typeof(PredictionManager).GetField(
            "_verifiedServerTick", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo AcknowledgedTick = typeof(PredictionManager).GetField(
            "_ackedServerTick", BindingFlags.Instance | BindingFlags.NonPublic);

        private readonly ProjectileChainScenario _owner;
        private readonly PredictionManager _manager;
        private string _phase = "chain";
        private ulong _lastTick;
        private double _lastTime;

        internal HistoricalTrafficTrace(ProjectileChainScenario owner, PredictionManager manager)
        {
            _owner = owner;
            _manager = manager;
            Emit("start");
        }

        internal void SetPhase(string phase)
        {
            _phase = phase;
            Emit("phase");
        }

        internal void Sample()
        {
            if (!_manager)
                return;
            ulong tick = _manager.localTick;
            if (tick >= _lastTick && tick - _lastTick < 10 &&
                Time.realtimeSinceStartupAsDouble - _lastTime < 1d)
                return;
            Emit("sample");
        }

        private void Emit(string point)
        {
            if (!_manager)
                return;
            _lastTick = _manager.localTick;
            _lastTime = Time.realtimeSinceStartupAsDouble;
            string role = _manager.isServer ? (_manager.isClient ? "host" : "server") : "client";
            var line = new StringBuilder(480);
            line.Append("[HistoricalTrafficTrace] scenario=ProjectileChain point=").Append(point)
                .Append(" phase=").Append(_phase).Append(" role=").Append(role)
                .Append(" tick=").Append(_lastTick)
                .Append(" verified=").Append(VerifiedTick?.GetValue(_manager) ?? "unavailable")
                .Append(" ack=").Append(AcknowledgedTick?.GetValue(_manager) ?? "unavailable")
                .Append(" maxAckLagTicks=").Append(_manager.lastMaxAckLagTicks)
                .Append(" frame=").Append(Time.frameCount)
                .Append(" time=").Append(_lastTime.ToString("F3", CultureInfo.InvariantCulture))
                .Append(" deleteBits=").Append(_manager.deltaSectionDeleteBitsTotal)
                .Append(" hierarchyBits=").Append(_manager.deltaSectionHierarchyBitsTotal)
                .Append(" inputBits=").Append(_manager.deltaSectionInputBitsTotal)
                .Append(" stateBits=").Append(_manager.deltaSectionStateBitsTotal)
                .Append(" deltaFrames=").Append(_manager.deltaFramesWrittenTotal)
                .Append(" deltaBytes=").Append(_manager.deltaFrameBytesTotal)
                .Append(" maxDeltaBytes=").Append(_manager.maxDeltaFrameBytes)
                .Append(" fullFrames=").Append(_manager.fullFramesSentTotal)
                .Append(" fullBytes=").Append(_manager.fullFrameBytesTotal);
            Debug.LogFormat(LogType.Log, LogOption.NoStacktrace, _owner, "{0}", line.ToString());
        }

        public void Dispose()
        {
            Emit("end");
            if (ReferenceEquals(_owner._historicalTrafficTrace, this))
                _owner._historicalTrafficTrace = null;
        }
    }

    private bool DriverFinished(PredictionManager pm)
    {
        ref var state = ref pm.hierarchy.currentState;
        for (var i = 0; i < state.spawnedPrefabs.Count; i++)
        {
            var details = state.spawnedPrefabs[i];
            if (details.prefabId != _driverPrefabId)
                continue;

            if (details.instanceId.TryGetComponent<ProjectileChainDriver>(pm, out var driver)
                && driver.currentState.shots >= ProjectileChainDriver.TotalShots)
                return true;
        }

        return false;
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
}
