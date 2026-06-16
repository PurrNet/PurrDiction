using PurrNet.Prediction;

public class ProjectileChainEffect : PredictedIdentity<ProjectileChainEffect.EffectState>
{
    public int lifetimeTicks = 8;

    public struct EffectState : IPredictedData<EffectState>
    {
        public uint age;
        public bool deleteRequested;

        public void Dispose() { }
    }

    protected override void Simulate(ref EffectState state, float delta)
    {
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
        return $"{id.objectId.instanceId.value}:{currentState.age}:{currentState.deleteRequested}";
    }
}
