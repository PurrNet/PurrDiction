using System;
using System.Text;
using Cysharp.Threading.Tasks;
using PurrNet;
using PurrNet.Packing;
using PurrNet.Pooling;
using PurrNet.Prediction;
using UnityEngine;

public class StaticModuleReuseScenario : Scenario
{
    [SerializeField] private DeterministicTickCounter _counter;
    [SerializeField] private float _timeout = 90f;
    [SerializeField] private float _settleSeconds = 2f;

    private const int DigestChannel = 600;

    private GameObject _driverPrefab;
    private GameObject _identityPrefab;
    private int _driverPrefabId;
    private int _identityPrefabId;

    public override void Setup(ScenarioContext ctx, NetworkManager manager)
    {
        _identityPrefab = PredictionTestUtils.CreatePrefab<StaticModuleReuseIdentity>("StaticModuleReuseIdentity");
        PredictionTestUtils.RegisterPrefab(ctx, _identityPrefab, true, 1);

        _driverPrefab = PredictionTestUtils.CreatePrefab<StaticModuleReuseDriver>("StaticModuleReuseDriver");
        _driverPrefab.GetComponent<StaticModuleReuseDriver>().identityPrefab = _identityPrefab;
        PredictionTestUtils.RegisterPrefab(ctx, _driverPrefab);
    }

    public override async UniTask<ScenarioResult> RunScenario(ScenarioContext ctx)
    {
        StaticModuleReuseIdentity.ResetStats();

        var pm = ctx.predictionManager;
        pm.TryGetPrefab(_driverPrefab, out _driverPrefabId);
        pm.TryGetPrefab(_identityPrefab, out _identityPrefabId);

        if (!pm.hierarchy.Create(_driverPrefab).HasValue)
            return ScenarioResult.Fail("failed to create static-module reuse driver");

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => DriverFinished(pm)
                      && PredictionTestUtils.CountInstances(pm, _identityPrefabId) == 0,
                _timeout,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail(
                $"static module reuse timeout: drivers={PredictionTestUtils.CountInstances(pm, _driverPrefabId)} " +
                $"identities={PredictionTestUtils.CountInstances(pm, _identityPrefabId)} failures={StaticModuleReuseIdentity.failureCount}");
        }

        await UniTask.WaitForSeconds(_settleSeconds, cancellationToken: ctx.cancellationToken);

        if (StaticModuleReuseIdentity.failureCount != 0)
            return ScenarioResult.Fail($"static module reuse failures={StaticModuleReuseIdentity.failureCount}");

        return await DigestExchange.Compare(ctx, DigestChannel, BuildDigest(ctx), 30f);
    }

    private bool DriverFinished(PredictionManager pm)
    {
        ref var state = ref pm.hierarchy.currentState;
        for (var i = 0; i < state.spawnedPrefabs.Count; i++)
        {
            var details = state.spawnedPrefabs[i];
            if (details.prefabId != _driverPrefabId)
                continue;

            if (details.instanceId.TryGetComponent<StaticModuleReuseDriver>(pm, out var driver)
                && driver.currentState.spawns >= StaticModuleReuseDriver.TotalSpawns)
                return true;
        }

        return false;
    }

    private string BuildDigest(ScenarioContext ctx)
    {
        var sb = new StringBuilder();
        sb.Append(PredictionTestUtils.WorldDigest(ctx, _counter));
        sb.Append("|staticModuleFailures=").Append(StaticModuleReuseIdentity.failureCount);
        PredictionTestUtils.AppendIdentities<StaticModuleReuseDriver>(ctx.predictionManager, _driverPrefabId, sb, driver => driver.Digest());
        PredictionTestUtils.AppendIdentities<StaticModuleReuseIdentity>(ctx.predictionManager, _identityPrefabId, sb, identity => identity.Digest());
        return sb.ToString();
    }
}

public class StaticModuleReuseDriver : PredictedIdentity<StaticModuleReuseDriver.DriverState>
{
    public const int TotalSpawns = 96;

    public GameObject identityPrefab;

    public struct DriverState : IPredictedData<DriverState>
    {
        public uint ticks;
        public int spawns;

        public void Dispose() { }
    }

    protected override void Simulate(ref DriverState state, float delta)
    {
        if (!identityPrefab || state.spawns >= TotalSpawns)
            return;

        var spawn = state.spawns;
        var position = new Vector3(spawn % 6, spawn / 6 * 0.15f, 0f);
        hierarchy.Create(identityPrefab, position, Quaternion.identity, owner);

        state.spawns += 1;
        state.ticks += 1;
    }

    public string Digest()
    {
        return $"{id.objectId.instanceId.value}:{currentState.ticks}:{currentState.spawns}";
    }
}

public class StaticModuleReuseIdentity : PredictedIdentity<StaticModuleReuseIdentity.ReuseState>
{
    public static int failureCount;

    public int lifetimeTicks = 4;

    public struct ReuseState : IPredictedData<ReuseState>
    {
        public uint age;
        public bool checkedFirstTick;
        public bool deleteRequested;
        public int failures;

        public void Dispose() { }
    }

    public static void ResetStats()
    {
        failureCount = 0;
    }

    protected override void LateAwake()
    {
        if (!TryGetModule<StaticReuseModule>(out _))
            _ = new StaticReuseModule(this);
    }

    protected override ReuseState GetInitialState()
    {
        var state = default(ReuseState);
        if (!TryGetModule<StaticReuseModule>(out var module)
            || module.currentState.age != 0
            || module.currentState.samples.isDisposed
            || module.currentState.samples.Count != 0)
        {
            state.failures += 1;
            failureCount += 1;
        }

        return state;
    }

    protected override void Simulate(ref ReuseState state, float delta)
    {
        if (!state.checkedFirstTick)
            state.checkedFirstTick = true;

        if (state.deleteRequested)
            return;

        state.age += 1;
        if (state.age < lifetimeTicks)
            return;

        state.deleteRequested = true;
        hierarchy.Delete(id.objectId);
    }

    public string Digest()
    {
        var moduleDigest = TryGetModule<StaticReuseModule>(out var module)
            ? module.Digest()
            : "missing";

        return $"{id.objectId.instanceId.value}:{currentState.age}:{currentState.checkedFirstTick}:{currentState.deleteRequested}:{currentState.failures}:{moduleDigest}";
    }
}

public class StaticReuseModule : PredictedModule<StaticReuseModule.StaticModuleState>
{
    public StaticReuseModule(PredictedIdentity identity) : base(identity) { }

    public struct StaticModuleState : IPredictedData<StaticModuleState>, IDuplicate<StaticModuleState>
    {
        public uint age;
        public uint checksum;
        public DisposableList<uint> samples;

        public void Dispose()
        {
            samples.Dispose();
        }

        public StaticModuleState Duplicate()
        {
            return new StaticModuleState
            {
                age = age,
                checksum = checksum,
                samples = samples.isDisposed ? DisposableList<uint>.Create(4) : samples.Duplicate()
            };
        }
    }

    protected override StaticModuleState GetInitialState()
    {
        return new StaticModuleState
        {
            samples = DisposableList<uint>.Create(4)
        };
    }

    protected override void Simulate(ref StaticModuleState state, float delta)
    {
        if (state.samples.isDisposed)
            state.samples = DisposableList<uint>.Create(4);

        state.age += 1;
        state.checksum = unchecked(state.checksum * 16777619u + state.age + identity.id.objectId.instanceId.value);

        var targetCount = (int)(state.age % 4);
        while (state.samples.Count > targetCount)
            state.samples.RemoveAt(state.samples.Count - 1);
        while (state.samples.Count < targetCount)
            state.samples.Add(unchecked(state.checksum + (uint)state.samples.Count));
    }

    public string Digest()
    {
        var count = currentState.samples.isDisposed ? -1 : currentState.samples.Count;
        return $"{currentState.age}:{currentState.checksum}:{count}";
    }
}
