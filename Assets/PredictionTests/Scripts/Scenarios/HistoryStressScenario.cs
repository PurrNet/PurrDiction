using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using PurrNet.Packing;
using PurrNet.Pooling;
using PurrNet.Prediction;
using UnityEngine;

public sealed class HistoryStressScenario : Scenario
{
    private const int DigestChannel = 910;
    private const int DefaultObjectCount = 256;
    private const int DefaultPayloadLength = 32;
    private const int DefaultListPayloadLength = 32;
    private const int DefaultTargetSteps = 240;
    private const float DefaultTimeoutSeconds = 120f;

    private GameObject _prefab;
    private int _prefabId;
    private int _objectCount;
    private int _payloadLength;
    private int _listPayloadLength;
    private int _targetSteps;
    private float _timeoutSeconds;

    public override void Setup(ScenarioContext ctx, PurrNet.NetworkManager manager)
    {
        _objectCount = GetIntArgument("-historyStressObjects", DefaultObjectCount);
        _payloadLength = GetIntArgument("-historyStressPayload", DefaultPayloadLength);
        _listPayloadLength = GetIntArgument("-historyStressListPayload", DefaultListPayloadLength);
        _targetSteps = GetIntArgument("-historyStressTicks", DefaultTargetSteps);
        _timeoutSeconds = GetIntArgument("-historyStressTimeout", (int)DefaultTimeoutSeconds);

        _prefab = PredictionTestUtils.CreatePrefab<HistoryStressIdentity>("HistoryStressIdentity");
        var stress = _prefab.GetComponent<HistoryStressIdentity>();
        stress.payloadLength = Math.Max(0, _payloadLength);
        stress.listPayloadLength = Math.Max(0, _listPayloadLength);
        stress.targetSteps = Math.Max(1, _targetSteps);
        PredictionTestUtils.RegisterPrefab(ctx, _prefab);
    }

    public override async UniTask<ScenarioResult> RunScenario(ScenarioContext ctx)
    {
        var pm = ctx.predictionManager;
        if (!pm.TryGetPrefab(_prefab, out _prefabId))
            return ScenarioResult.Fail("failed to resolve history stress prefab id");

        if (ctx.isServer)
            SpawnStressObjects(pm);

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => CountStressObjects(pm) >= _objectCount,
                _timeoutSeconds,
                ctx.cancellationToken);

            await UniTaskUtils.WaitWithTimeout(
                () => AllStressObjectsComplete(pm),
                _timeoutSeconds,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail(
                $"history stress timeout: objects={CountStressObjects(pm)}/{_objectCount} complete={CountCompleteStressObjects(pm)}");
        }

        return await DigestExchange.Compare(ctx, DigestChannel, BuildDigest(pm), 30f);
    }

    private void SpawnStressObjects(PredictionManager pm)
    {
        var existing = CountStressObjects(pm);
        var side = Mathf.CeilToInt(Mathf.Sqrt(_objectCount));

        for (var i = existing; i < _objectCount; i++)
        {
            var position = new Vector3(i % side, 0f, i / side);
            if (!pm.hierarchy.Create(_prefab, position, Quaternion.identity).HasValue)
                Debug.LogError($"Failed to create history stress object {i}.");
        }
    }

    private bool AllStressObjectsComplete(PredictionManager pm)
    {
        var count = 0;
        ref var state = ref pm.hierarchy.currentState;
        for (var i = 0; i < state.spawnedPrefabs.Count; i++)
        {
            var details = state.spawnedPrefabs[i];
            if (details.prefabId != _prefabId)
                continue;

            count++;
            if (!details.instanceId.TryGetComponent<HistoryStressIdentity>(pm, out var identity) ||
                identity.currentState.step < _targetSteps)
                return false;
        }

        return count >= _objectCount;
    }

    private int CountStressObjects(PredictionManager pm)
    {
        return PredictionTestUtils.CountInstances(pm, _prefabId);
    }

    private int CountCompleteStressObjects(PredictionManager pm)
    {
        var count = 0;
        ref var state = ref pm.hierarchy.currentState;
        for (var i = 0; i < state.spawnedPrefabs.Count; i++)
        {
            var details = state.spawnedPrefabs[i];
            if (details.prefabId != _prefabId)
                continue;

            if (details.instanceId.TryGetComponent<HistoryStressIdentity>(pm, out var identity) &&
                identity.currentState.step >= _targetSteps)
                count++;
        }

        return count;
    }

    private string BuildDigest(PredictionManager pm)
    {
        var entries = new List<InstanceDetails>();
        ref var state = ref pm.hierarchy.currentState;
        for (var i = 0; i < state.spawnedPrefabs.Count; i++)
        {
            var details = state.spawnedPrefabs[i];
            if (details.prefabId == _prefabId)
                entries.Add(details);
        }

        entries.Sort((a, b) => a.instanceId.instanceId.value.CompareTo(b.instanceId.instanceId.value));

        uint aggregate = 2166136261u;
        uint minStep = uint.MaxValue;
        uint maxStep = 0;

        for (var i = 0; i < entries.Count; i++)
        {
            if (!entries[i].instanceId.TryGetComponent<HistoryStressIdentity>(pm, out var identity))
                continue;

            var stress = identity.currentState;
            minStep = Math.Min(minStep, stress.step);
            maxStep = Math.Max(maxStep, stress.step);

            aggregate = Mix(aggregate, (uint)entries[i].instanceId.instanceId.value);
            aggregate = Mix(aggregate, stress.step);
            aggregate = Mix(aggregate, stress.checksum);
            aggregate = Mix(aggregate, stress.payload.isDisposed ? 0u : (uint)stress.payload.Count);
            aggregate = Mix(aggregate, stress.dirtyIndices.isDisposed ? 0u : (uint)stress.dirtyIndices.Count);
            aggregate = Mix(aggregate, stress.vectorSamples.isDisposed ? 0u : (uint)stress.vectorSamples.Count);
            aggregate = Mix(aggregate, stress.rotationSamples.isDisposed ? 0u : (uint)stress.rotationSamples.Count);
        }

        if (entries.Count == 0)
            minStep = 0;

        return $"historyStress;objects={entries.Count};payload={_payloadLength};listPayload={_listPayloadLength};target={_targetSteps};minStep={minStep};maxStep={maxStep};checksum={aggregate}";
    }

    private static uint Mix(uint hash, uint value)
    {
        return (hash ^ value) * 16777619u;
    }

    private static int GetIntArgument(string name, int fallback)
    {
        return CommandLineUtils.TryGetArgument(name, out var value) && int.TryParse(value, out var parsed)
            ? parsed
            : fallback;
    }
}

public sealed class HistoryStressIdentity : PredictedIdentity<HistoryStressIdentity.HistoryStressState>
{
    public int payloadLength = 32;
    public int listPayloadLength = 32;
    public int targetSteps = 240;

    protected override HistoryStressState GetInitialState()
    {
        var payload = DisposableArray<uint>.Create(Math.Max(0, payloadLength));
        var dirtyIndices = DisposableList<uint>.Create(Math.Max(0, listPayloadLength));
        var vectorSamples = DisposableList<Vector3>.Create(Math.Max(0, listPayloadLength));
        var rotationSamples = DisposableList<Quaternion>.Create(Math.Max(0, listPayloadLength));
        var seed = unchecked((uint)id.objectId.instanceId.value);

        for (var i = 0; i < payload.Count; i++)
            payload[i] = unchecked(seed + (uint)i * 2654435761u);

        for (var i = 0; i < listPayloadLength; i++)
        {
            dirtyIndices.Add(unchecked(seed + (uint)i * 31u));
            vectorSamples.Add(CreateVectorSample(seed, i));
            rotationSamples.Add(CreateRotationSample(seed, i));
        }

        return new HistoryStressState
        {
            payload = payload,
            dirtyIndices = dirtyIndices,
            vectorSamples = vectorSamples,
            rotationSamples = rotationSamples,
            position = CreateVectorSample(seed, 1),
            velocity = CreateVectorSample(seed, 2) * 0.01f,
            acceleration = CreateVectorSample(seed, 3) * 0.001f,
            angularVelocity = CreateVectorSample(seed, 4) * 0.1f,
            scale = Vector3.one + CreateVectorSample(seed, 5) * 0.0001f,
            rotation = CreateRotationSample(seed, 1),
            targetRotation = CreateRotationSample(seed, 2),
            checksum = seed
        };
    }

    protected override void Simulate(ref HistoryStressState state, float delta)
    {
        if (state.step >= targetSteps)
            return;

        state.step++;
        EnsureLists(ref state, Math.Max(0, listPayloadLength), unchecked((uint)id.objectId.instanceId.value));
        StepTransformPayload(ref state, delta);

        if (!state.payload.isDisposed && state.payload.Count > 0)
        {
            var count = state.payload.Count;
            var first = (int)((state.step + state.checksum) % count);
            var second = (first + 17) % count;

            state.payload[first] = unchecked(state.payload[first] + state.step * 17u + 3u);
            state.payload[second] = unchecked(state.payload[second] ^ (state.step * 1103515245u));

            state.checksum = unchecked(
                (state.checksum ^ state.payload[first]) * 16777619u +
                (state.payload[second] ^ state.step));
        }
        else
        {
            state.checksum = unchecked((state.checksum ^ state.step) * 16777619u);
        }

        StepListPayload(ref state);
        state.checksum = Mix(state.checksum, Quantize(state.position.x));
        state.checksum = Mix(state.checksum, Quantize(state.position.y));
        state.checksum = Mix(state.checksum, Quantize(state.position.z));
        state.checksum = Mix(state.checksum, Quantize(state.rotation.x));
        state.checksum = Mix(state.checksum, Quantize(state.rotation.y));
        state.checksum = Mix(state.checksum, Quantize(state.rotation.z));
        state.checksum = Mix(state.checksum, Quantize(state.rotation.w));
    }

    public struct HistoryStressState : IPredictedData<HistoryStressState>, IDuplicate<HistoryStressState>
    {
        public uint step;
        public uint checksum;
        public Vector3 position;
        public Vector3 velocity;
        public Vector3 acceleration;
        public Vector3 angularVelocity;
        public Vector3 scale;
        public Quaternion rotation;
        public Quaternion targetRotation;
        public DisposableArray<uint> payload;
        public DisposableList<uint> dirtyIndices;
        public DisposableList<Vector3> vectorSamples;
        public DisposableList<Quaternion> rotationSamples;

        public void Dispose()
        {
            payload.Dispose();
            dirtyIndices.Dispose();
            vectorSamples.Dispose();
            rotationSamples.Dispose();
        }

        public HistoryStressState Duplicate()
        {
            return new HistoryStressState
            {
                step = step,
                checksum = checksum,
                position = position,
                velocity = velocity,
                acceleration = acceleration,
                angularVelocity = angularVelocity,
                scale = scale,
                rotation = rotation,
                targetRotation = targetRotation,
                payload = payload.isDisposed ? default : DisposableArray<uint>.Create(payload),
                dirtyIndices = dirtyIndices.isDisposed ? default : dirtyIndices.Duplicate(),
                vectorSamples = vectorSamples.isDisposed ? default : vectorSamples.Duplicate(),
                rotationSamples = rotationSamples.isDisposed ? default : rotationSamples.Duplicate()
            };
        }
    }

    private static void EnsureLists(ref HistoryStressState state, int targetCount, uint seed)
    {
        if (state.dirtyIndices.isDisposed)
            state.dirtyIndices = DisposableList<uint>.Create(targetCount);
        if (state.vectorSamples.isDisposed)
            state.vectorSamples = DisposableList<Vector3>.Create(targetCount);
        if (state.rotationSamples.isDisposed)
            state.rotationSamples = DisposableList<Quaternion>.Create(targetCount);

        while (state.dirtyIndices.Count < targetCount)
            state.dirtyIndices.Add(unchecked(seed + (uint)state.dirtyIndices.Count * 31u));
        while (state.vectorSamples.Count < targetCount)
            state.vectorSamples.Add(CreateVectorSample(seed, state.vectorSamples.Count));
        while (state.rotationSamples.Count < targetCount)
            state.rotationSamples.Add(CreateRotationSample(seed, state.rotationSamples.Count));

        while (state.dirtyIndices.Count > targetCount)
            state.dirtyIndices.RemoveAt(state.dirtyIndices.Count - 1);
        while (state.vectorSamples.Count > targetCount)
            state.vectorSamples.RemoveAt(state.vectorSamples.Count - 1);
        while (state.rotationSamples.Count > targetCount)
            state.rotationSamples.RemoveAt(state.rotationSamples.Count - 1);
    }

    private static void StepTransformPayload(ref HistoryStressState state, float delta)
    {
        state.velocity += state.acceleration * delta;
        state.position += state.velocity * delta;
        state.scale = new Vector3(
            1f + ((state.step & 15u) * 0.001f),
            1f + (((state.step + 5u) & 15u) * 0.001f),
            1f + (((state.step + 11u) & 15u) * 0.001f));

        var stepRotation = Quaternion.Euler(state.angularVelocity * delta);
        state.rotation = stepRotation * state.rotation;
        state.targetRotation = Quaternion.Euler(
            (state.step % 360u) * 0.25f,
            (state.step % 180u) * 0.5f,
            (state.step % 90u));
    }

    private static void StepListPayload(ref HistoryStressState state)
    {
        if (!state.dirtyIndices.isDisposed && state.dirtyIndices.Count > 0)
        {
            var index = (int)((state.step + state.checksum) % state.dirtyIndices.Count);
            state.dirtyIndices[index] = unchecked(state.dirtyIndices[index] + state.step * 13u + state.checksum);
            state.checksum = Mix(state.checksum, state.dirtyIndices[index]);
        }

        if (!state.vectorSamples.isDisposed && state.vectorSamples.Count > 0)
        {
            var index = (int)((state.step * 3u + state.checksum) % state.vectorSamples.Count);
            var sample = state.vectorSamples[index];
            sample += state.velocity * 0.125f + state.acceleration * state.step;
            state.vectorSamples[index] = sample;
            state.checksum = Mix(state.checksum, Quantize(sample.x));
            state.checksum = Mix(state.checksum, Quantize(sample.y));
            state.checksum = Mix(state.checksum, Quantize(sample.z));
        }

        if (!state.rotationSamples.isDisposed && state.rotationSamples.Count > 0)
        {
            var index = (int)((state.step * 5u + state.checksum) % state.rotationSamples.Count);
            var sample = Quaternion.Euler(
                state.angularVelocity.x * 0.25f,
                state.angularVelocity.y * 0.25f,
                state.angularVelocity.z * 0.25f) * state.rotationSamples[index];
            state.rotationSamples[index] = sample;
            state.checksum = Mix(state.checksum, Quantize(sample.x));
            state.checksum = Mix(state.checksum, Quantize(sample.y));
            state.checksum = Mix(state.checksum, Quantize(sample.z));
            state.checksum = Mix(state.checksum, Quantize(sample.w));
        }
    }

    private static Vector3 CreateVectorSample(uint seed, int index)
    {
        var value = unchecked(seed + (uint)index * 747796405u);
        return new Vector3(
            ((int)(value & 0xFFu) - 128) * 0.01f,
            ((int)((value >> 8) & 0xFFu) - 128) * 0.01f,
            ((int)((value >> 16) & 0xFFu) - 128) * 0.01f);
    }

    private static Quaternion CreateRotationSample(uint seed, int index)
    {
        var value = unchecked(seed + (uint)index * 2891336453u);
        return Quaternion.Euler(
            value & 0xFFu,
            (value >> 8) & 0xFFu,
            (value >> 16) & 0xFFu);
    }

    private static uint Quantize(float value)
    {
        return unchecked((uint)Mathf.RoundToInt(value * 100000f));
    }

    private static uint Mix(uint hash, uint value)
    {
        return (hash ^ value) * 16777619u;
    }
}
