using System.Collections.Generic;
using System.Text;
using PurrNet.Prediction;
using UnityEngine;

public static class PredictionTestUtils
{
    public static void RegisterPrefab(ScenarioContext ctx, GameObject prefab)
    {
        ctx.predictionManager.predictedPrefabs.prefabs.Add(new PredictedPrefab
        {
            prefab = prefab,
            pooled = false,
            warmupCount = 0
        });
    }

    public static GameObject CreatePrefab<T>(string name) where T : PredictedIdentity
    {
        var go = new GameObject(name);
        go.AddComponent<T>();
        Object.DontDestroyOnLoad(go);
        return go;
    }

    public static int CountInstances(PredictionManager pm, int prefabId)
    {
        ref var state = ref pm.hierarchy.currentState;
        int count = 0;
        for (var i = 0; i < state.spawnedPrefabs.Count; i++)
        {
            if (state.spawnedPrefabs[i].prefabId == prefabId)
                count++;
        }
        return count;
    }

    public static long CounterDelta(DeterministicTickCounter counter, PredictionManager pm)
    {
        return (long)counter.currentState.count - (long)pm.time.tick;
    }

    /// <summary>
    /// Stable digest of the predicted world: deterministic counter alignment, hierarchy
    /// instance list, nextInstanceId and per-pawn state. Equal across peers once the
    /// simulation is quiesced; any one-tick deterministic skew or instance-id drift
    /// shows up as a mismatch.
    /// </summary>
    public static string WorldDigest(ScenarioContext ctx, DeterministicTickCounter counter)
    {
        var pm = ctx.predictionManager;
        var sb = new StringBuilder();

        if (counter)
            sb.Append($"counterDelta={CounterDelta(counter, pm)};");

        ref var state = ref pm.hierarchy.currentState;
        sb.Append($"next={state.nextInstanceId};count={state.spawnedPrefabs.Count};");

        var entries = new List<InstanceDetails>();
        for (var i = 0; i < state.spawnedPrefabs.Count; i++)
            entries.Add(state.spawnedPrefabs[i]);
        entries.Sort((a, b) => a.instanceId.instanceId.value.CompareTo(b.instanceId.instanceId.value));

        foreach (var details in entries)
        {
            ulong owner = details.owner.HasValue ? details.owner.Value.id.value : 0;
            sb.Append($"[{(int)details.prefabId}:{details.instanceId.instanceId.value}:{owner}");

            if (details.instanceId.TryGetComponent<PawnIdentity>(pm, out var pawn))
                sb.Append($":sum={pawn.currentState.sum}:proj={pawn.currentState.projectiles}");

            sb.Append(']');
        }

        return sb.ToString();
    }
}
