using PurrNet.Prediction;
using UnityEngine;

public class ProjectileChainDriver : PredictedIdentity<ProjectileChainDriver.DriverState>
{
    public const int TotalShots = 48;

    public GameObject projectilePrefab;
    public GameObject muzzlePrefab;

    public struct DriverState : IPredictedData<DriverState>
    {
        public uint ticks;
        public int shots;

        public void Dispose() { }
    }

    protected override void Simulate(ref DriverState state, float delta)
    {
        if (!projectilePrefab || !muzzlePrefab || state.shots >= TotalShots)
            return;

        state.ticks += 1;

        var shot = state.shots;
        var lane = shot % 8;
        var wave = shot / 8;
        var position = new Vector3(lane, wave * 0.25f, 0f);

        hierarchy.Create(projectilePrefab, position, Quaternion.identity, owner);
        hierarchy.Create(muzzlePrefab, position, Quaternion.identity, owner);
        state.shots += 1;
    }

    public string Digest()
    {
        return $"{id.objectId.instanceId.value}:{currentState.ticks}:{currentState.shots}";
    }
}
