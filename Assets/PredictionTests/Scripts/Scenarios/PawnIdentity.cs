using PurrNet.Prediction;
using UnityEngine;

public class PawnIdentity : PredictedIdentity<PawnIdentity.PawnInput, PawnIdentity.PawnState>
{
    public const long StopAtSum = 400;
    public const int ProjectileEvery = 100;

    public static GameObject pawnPrefab;
    public static GameObject projectilePrefab;

    public struct PawnInput : IPredictedData
    {
        public int step;

        public void Dispose() { }
    }

    public struct PawnState : IPredictedData<PawnState>
    {
        public long sum;
        public int projectiles;

        public void Dispose() { }
    }

    public bool isStable => currentState.sum >= StopAtSum;

    protected override void GetFinalInput(ref PawnInput input)
    {
        input.step = currentState.sum >= StopAtSum ? 0 : 1 + (int)(predictionManager.localTick % 7);
    }

    protected override void Simulate(PawnInput input, ref PawnState state, float delta)
    {
        if (input.step <= 0)
            return;

        state.sum += input.step;

        int maxProjectiles = (int)(StopAtSum / ProjectileEvery);
        if (projectilePrefab && state.projectiles < maxProjectiles && state.sum >= (state.projectiles + 1) * ProjectileEvery)
        {
            predictionManager.hierarchy.Create(projectilePrefab, new Vector3(0f, state.projectiles, 0f), Quaternion.identity);
            state.projectiles += 1;
        }
    }

    public static bool AllStable(PredictionManager pm, int prefabId)
    {
        ref var state = ref pm.hierarchy.currentState;
        bool any = false;

        for (var i = 0; i < state.spawnedPrefabs.Count; i++)
        {
            var details = state.spawnedPrefabs[i];
            if (details.prefabId != prefabId)
                continue;

            any = true;
            if (!details.instanceId.TryGetComponent<PawnIdentity>(pm, out var pawn) || !pawn.isStable)
                return false;
        }

        return any;
    }
}
