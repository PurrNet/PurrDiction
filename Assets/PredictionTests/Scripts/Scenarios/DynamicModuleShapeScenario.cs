using System;
using System.Text;
using Cysharp.Threading.Tasks;
using PurrNet;
using PurrNet.Packing;
using PurrNet.Pooling;
using PurrNet.Prediction;
using UnityEngine;

public class DynamicModuleShapeScenario : Scenario
{
    [SerializeField] private DeterministicTickCounter _counter;
    [SerializeField] private float _timeout = 90f;
    [SerializeField] private float _settleSeconds = 2f;

    private const int DigestChannel = 700;

    private GameObject _driverPrefab;
    private GameObject _identityPrefab;
    private int _driverPrefabId;
    private int _identityPrefabId;

    public override void Setup(ScenarioContext ctx, NetworkManager manager)
    {
        _identityPrefab = PredictionTestUtils.CreatePrefab<DynamicModuleShapeIdentity>("DynamicModuleShapeIdentity");
        PredictionTestUtils.RegisterPrefab(ctx, _identityPrefab, true, 1);

        _driverPrefab = PredictionTestUtils.CreatePrefab<DynamicModuleShapeDriver>("DynamicModuleShapeDriver");
        _driverPrefab.GetComponent<DynamicModuleShapeDriver>().identityPrefab = _identityPrefab;
        PredictionTestUtils.RegisterPrefab(ctx, _driverPrefab);
    }

    public override async UniTask<ScenarioResult> RunScenario(ScenarioContext ctx)
    {
        DynamicModuleShapeIdentity.ResetStats();

        var pm = ctx.predictionManager;
        pm.TryGetPrefab(_driverPrefab, out _driverPrefabId);
        pm.TryGetPrefab(_identityPrefab, out _identityPrefabId);

        if (!pm.hierarchy.Create(_driverPrefab).HasValue)
            return ScenarioResult.Fail("failed to create dynamic-module shape driver");

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
                $"dynamic module shape timeout: drivers={PredictionTestUtils.CountInstances(pm, _driverPrefabId)} " +
                $"identities={PredictionTestUtils.CountInstances(pm, _identityPrefabId)} failures={DynamicModuleShapeIdentity.failureCount}");
        }

        await UniTask.WaitForSeconds(_settleSeconds, cancellationToken: ctx.cancellationToken);

        if (DynamicModuleShapeIdentity.failureCount != 0)
            return ScenarioResult.Fail($"dynamic module shape failures={DynamicModuleShapeIdentity.failureCount}");

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

            if (details.instanceId.TryGetComponent<DynamicModuleShapeDriver>(pm, out var driver)
                && driver.currentState.spawns >= DynamicModuleShapeDriver.TotalSpawns)
                return true;
        }

        return false;
    }

    private string BuildDigest(ScenarioContext ctx)
    {
        var sb = new StringBuilder();
        sb.Append(PredictionTestUtils.WorldDigest(ctx, _counter));
        sb.Append("|dynamicShapeFailures=").Append(DynamicModuleShapeIdentity.failureCount);
        PredictionTestUtils.AppendIdentities<DynamicModuleShapeDriver>(ctx.predictionManager, _driverPrefabId, sb, driver => driver.Digest());
        PredictionTestUtils.AppendIdentities<DynamicModuleShapeIdentity>(ctx.predictionManager, _identityPrefabId, sb, identity => identity.Digest());
        return sb.ToString();
    }
}

public class DynamicModuleShapeDriver : PredictedIdentity<DynamicModuleShapeDriver.DriverState>
{
    public const int TotalSpawns = 128;

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
        var position = new Vector3(spawn % 8, spawn / 8 * 0.15f, 0f);
        hierarchy.Create(identityPrefab, position, Quaternion.identity, owner);

        state.spawns += 1;
        state.ticks += 1;
    }

    public string Digest()
    {
        return $"{id.objectId.instanceId.value}:{currentState.ticks}:{currentState.spawns}";
    }
}

public class DynamicModuleShapeIdentity : PredictedIdentity<DynamicModuleShapeIdentity.ShapeState>
{
    public static int failureCount;

    public int lifetimeTicks = 7;

    public struct ShapeState : IPredictedData<ShapeState>
    {
        public uint age;
        public int shape;
        public bool checkedFirstTick;
        public bool modulesRegistered;
        public bool alphaDisposed;
        public bool gammaAdded;
        public bool deleteRequested;
        public int failures;

        public void Dispose() { }
    }

    public static void ResetStats()
    {
        failureCount = 0;
    }

    protected override ShapeState GetInitialState()
    {
        var state = default(ShapeState);
        if (modules.Count != 0)
        {
            state.failures += modules.Count;
            failureCount += 1;
        }

        return state;
    }

    protected override void Simulate(ref ShapeState state, float delta)
    {
        if (!state.checkedFirstTick)
            state.checkedFirstTick = true;

        if (!state.modulesRegistered)
        {
            state.modulesRegistered = true;
            state.shape = (int)(id.objectId.instanceId.value % 5);
            RegisterShape(state.shape);
        }

        if (!state.alphaDisposed && state.age >= 2 && TryGetModule<DynamicShapeAlphaModule>(out var alpha))
        {
            state.alphaDisposed = true;
            alpha.Dispose();
        }

        if (!state.gammaAdded && state.age >= 3 && state.shape is 3 or 4)
        {
            state.gammaAdded = true;
            _ = new DynamicShapeGammaModule(this);
        }

        if (state.deleteRequested)
            return;

        state.age += 1;
        if (state.age < lifetimeTicks)
            return;

        state.deleteRequested = true;
        hierarchy.Delete(id.objectId);
    }

    private void RegisterShape(int shape)
    {
        switch (shape)
        {
            case 0:
                break;
            case 1:
                _ = new DynamicShapeAlphaModule(this);
                break;
            case 2:
                _ = new DynamicShapeBetaModule(this);
                break;
            case 3:
                _ = new DynamicShapeAlphaModule(this);
                _ = new DynamicShapeBetaModule(this);
                break;
            default:
                _ = new DynamicShapeBetaModule(this);
                _ = new DynamicShapeAlphaModule(this);
                break;
        }
    }

    public string Digest()
    {
        return $"{id.objectId.instanceId.value}:{currentState.age}:{currentState.shape}:{currentState.modulesRegistered}:{currentState.alphaDisposed}:{currentState.gammaAdded}:{currentState.deleteRequested}:{currentState.failures}:{modules.Count}";
    }
}

public abstract class DynamicShapeModuleBase<TState> : PredictedModule<TState>
    where TState : struct, IPredictedData<TState>
{
    protected DynamicShapeModuleBase(PredictedIdentity identity) : base(identity) { }

    protected void StepSamples(ref uint age, ref uint checksum, ref DisposableList<uint> samples, uint salt)
    {
        if (samples.isDisposed)
            samples = DisposableList<uint>.Create(4);

        age += 1;
        checksum = unchecked(checksum * 16777619u + age + identity.id.objectId.instanceId.value + salt);

        var targetCount = (int)((age + salt) % 5);
        while (samples.Count > targetCount)
            samples.RemoveAt(samples.Count - 1);
        while (samples.Count < targetCount)
            samples.Add(unchecked(checksum + (uint)samples.Count));
    }
}

public class DynamicShapeAlphaModule : DynamicShapeModuleBase<DynamicShapeAlphaModule.ShapeModuleState>
{
    public DynamicShapeAlphaModule(PredictedIdentity identity) : base(identity) { }

    public struct ShapeModuleState : IPredictedData<ShapeModuleState>, IDuplicate<ShapeModuleState>
    {
        public uint age;
        public uint checksum;
        public DisposableList<uint> samples;

        public void Dispose()
        {
            samples.Dispose();
        }

        public ShapeModuleState Duplicate()
        {
            return new ShapeModuleState
            {
                age = age,
                checksum = checksum,
                samples = samples.isDisposed ? DisposableList<uint>.Create(4) : samples.Duplicate()
            };
        }
    }

    protected override ShapeModuleState GetInitialState()
    {
        return new ShapeModuleState { samples = DisposableList<uint>.Create(4) };
    }

    protected override void Simulate(ref ShapeModuleState state, float delta)
    {
        StepSamples(ref state.age, ref state.checksum, ref state.samples, 11);
    }
}

public class DynamicShapeBetaModule : DynamicShapeModuleBase<DynamicShapeBetaModule.ShapeModuleState>
{
    public DynamicShapeBetaModule(PredictedIdentity identity) : base(identity) { }

    public struct ShapeModuleState : IPredictedData<ShapeModuleState>, IDuplicate<ShapeModuleState>
    {
        public uint age;
        public uint checksum;
        public DisposableList<uint> samples;

        public void Dispose()
        {
            samples.Dispose();
        }

        public ShapeModuleState Duplicate()
        {
            return new ShapeModuleState
            {
                age = age,
                checksum = checksum,
                samples = samples.isDisposed ? DisposableList<uint>.Create(4) : samples.Duplicate()
            };
        }
    }

    protected override ShapeModuleState GetInitialState()
    {
        return new ShapeModuleState { samples = DisposableList<uint>.Create(4) };
    }

    protected override void Simulate(ref ShapeModuleState state, float delta)
    {
        StepSamples(ref state.age, ref state.checksum, ref state.samples, 23);
    }
}

public class DynamicShapeGammaModule : DynamicShapeModuleBase<DynamicShapeGammaModule.ShapeModuleState>
{
    public DynamicShapeGammaModule(PredictedIdentity identity) : base(identity) { }

    public struct ShapeModuleState : IPredictedData<ShapeModuleState>, IDuplicate<ShapeModuleState>
    {
        public uint age;
        public uint checksum;
        public DisposableList<uint> samples;

        public void Dispose()
        {
            samples.Dispose();
        }

        public ShapeModuleState Duplicate()
        {
            return new ShapeModuleState
            {
                age = age,
                checksum = checksum,
                samples = samples.isDisposed ? DisposableList<uint>.Create(4) : samples.Duplicate()
            };
        }
    }

    protected override ShapeModuleState GetInitialState()
    {
        return new ShapeModuleState { samples = DisposableList<uint>.Create(4) };
    }

    protected override void Simulate(ref ShapeModuleState state, float delta)
    {
        StepSamples(ref state.age, ref state.checksum, ref state.samples, 37);
    }
}
