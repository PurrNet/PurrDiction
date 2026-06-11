using PurrNet.Prediction;
using UnityEngine;

public class BounceRig : DeterministicIdentity<BounceRig.RigState>
{
    [SerializeField] private sfloat _spawnDelay = 3f;

    public GameObject ballPrefab { get; set; }

    public int requiredPlayers { get; set; }

    public bool hasSpawned => currentState.spawned;

    public struct RigState : IPredictedData<RigState>
    {
        public sfloat timer;
        public bool spawned;

        public void Dispose() { }
    }

    protected override RigState GetInitialState()
    {
        return new RigState
        {
            timer = _spawnDelay,
            spawned = false
        };
    }

    protected override void Simulate(ref RigState state, sfloat delta)
    {
        if (state.spawned || !ballPrefab)
            return;

        if (predictionManager.players.players.Count < requiredPlayers)
            return;

        state.timer -= delta;
        if (state.timer > 0)
            return;

        predictionManager.hierarchy.Create(ballPrefab, new Vector3(0f, 5f, 0f), Quaternion.identity);
        state.spawned = true;
    }
}
