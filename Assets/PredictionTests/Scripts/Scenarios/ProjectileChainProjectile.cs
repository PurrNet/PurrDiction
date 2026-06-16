using PurrNet.Packing;
using PurrNet.Pooling;
using PurrNet.Prediction;
using UnityEngine;

public class ProjectileChainProjectile : PredictedIdentity<ProjectileChainProjectile.ProjectileState>
{
    private const uint ImpactTick = 5;
    private const uint DeleteTick = 7;

    public GameObject hitPrefab;

    public struct ProjectileState : IPredictedData<ProjectileState>, IDuplicate<ProjectileState>
    {
        public uint age;
        public uint checksum;
        public bool impactSpawned;
        public bool deleteRequested;
        public DisposableList<uint> overlaps;

        public void Dispose()
        {
            overlaps.Dispose();
        }

        public ProjectileState Duplicate()
        {
            return new ProjectileState
            {
                age = age,
                checksum = checksum,
                impactSpawned = impactSpawned,
                deleteRequested = deleteRequested,
                overlaps = overlaps.isDisposed ? DisposableList<uint>.Create(8) : overlaps.Duplicate()
            };
        }
    }

    protected override ProjectileState GetInitialState()
    {
        return new ProjectileState
        {
            overlaps = DisposableList<uint>.Create(8)
        };
    }

    protected override void Simulate(ref ProjectileState state, float delta)
    {
        if (state.deleteRequested)
            return;

        if (state.overlaps.isDisposed)
            state.overlaps = DisposableList<uint>.Create(8);

        state.age += 1;
        state.checksum = unchecked(state.checksum * 16777619u + state.age + id.objectId.instanceId.value);

        var targetCount = (int)((state.age + id.objectId.instanceId.value) % 9);
        while (state.overlaps.Count > targetCount)
            state.overlaps.RemoveAt(state.overlaps.Count - 1);
        while (state.overlaps.Count < targetCount)
            state.overlaps.Add(unchecked(state.checksum + (uint)state.overlaps.Count));

        if (!state.impactSpawned && state.age >= ImpactTick && hitPrefab)
        {
            state.impactSpawned = true;
            hierarchy.Create(hitPrefab, transform.position, Quaternion.identity, owner);
        }

        if (state.age < DeleteTick)
            return;

        state.deleteRequested = true;
        hierarchy.Delete(id.objectId);
    }

    public string Digest()
    {
        var count = currentState.overlaps.isDisposed ? -1 : currentState.overlaps.Count;
        return $"{id.objectId.instanceId.value}:{currentState.age}:{currentState.checksum}:{currentState.impactSpawned}:{currentState.deleteRequested}:{count}";
    }
}
