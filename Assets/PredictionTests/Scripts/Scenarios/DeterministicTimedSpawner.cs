using PurrNet.Prediction;
using UnityEngine;

public class DeterministicTimedSpawner : DeterministicIdentity<DeterministicTimedSpawner.SpawnerState>
{
    [SerializeField] private sfloat _firstWaveDelay = 5f;
    [SerializeField] private sfloat _secondWaveDelay = 30f;
    [SerializeField] private sfloat _spawnInterval = 1f;
    [SerializeField] private int _spawnsPerWave = 5;

    public GameObject markerPrefab { get; set; }

    public int spawnsPerWave => _spawnsPerWave;
    public int totalSpawns => _spawnsPerWave * 2;
    public int spawnedCount => currentState.spawned;

    public struct SpawnerState : IPredictedData<SpawnerState>
    {
        public sfloat timer;
        public int spawned;

        public void Dispose() { }
    }

    protected override SpawnerState GetInitialState()
    {
        return new SpawnerState
        {
            timer = _firstWaveDelay,
            spawned = 0
        };
    }

    protected override void Simulate(ref SpawnerState state, sfloat delta)
    {
        if (!markerPrefab || state.spawned >= totalSpawns)
            return;

        state.timer -= delta;
        if (state.timer > 0)
            return;

        predictionManager.hierarchy.Create(markerPrefab, new Vector3(state.spawned, 0f, 0f), Quaternion.identity);
        state.spawned += 1;

        state.timer = state.spawned == _spawnsPerWave
            ? _secondWaveDelay - _firstWaveDelay
            : _spawnInterval;
    }
}
