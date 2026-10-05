using System;
using System.Collections.Generic;
using System.Text;
using Cysharp.Threading.Tasks;
using PurrNet.Prediction;
using UnityEngine;

public static class PredictionTestUtils
{
    /// <summary>
    /// Aligns all peers onto a single digest tick that sits inside a deterministic quiet
    /// window. Peers digest their own predicted timelines, which sit at different wall-clock
    /// and tick positions (input lead, scenario pacing under loss); a digest sampled on
    /// different sides of a deterministic spawn sees different instance sets and reports a
    /// false divergence. The barrier bounds wall-clock skew, the server then waits until the
    /// timed spawner is at least six seconds from firing (or exhausted) and broadcasts a
    /// digest tick one second ahead; every peer digests only after its own timeline passes
    /// that tick, so all samples land in the same inter-spawn gap.
    /// </summary>
    public static async UniTask AlignDigestTick(ScenarioContext ctx, int channel, float timeoutSeconds)
    {
        await ScenarioBarrier.Wait(ctx, 9000 + channel, timeoutSeconds);

        var pm = ctx.predictionManager;

        if (ctx.isServer)
        {
            var spawner = UnityEngine.Object.FindFirstObjectByType<DeterministicTimedSpawner>();
            if (spawner)
            {
                await UniTaskUtils.WaitWithTimeout(
                    () => spawner.spawnedCount >= spawner.totalSpawns || spawner.secondsUntilNextSpawn > 6f,
                    timeoutSeconds,
                    ctx.cancellationToken);
            }

            DigestGate.BroadcastDigestTick(ctx.scenarioIndex, channel, pm.time.tick + (ulong)pm.tickRate);
        }

        await UniTaskUtils.WaitWithTimeout(
            () => DigestGate.TryGetDigestTick(ctx.scenarioIndex, channel, out var digestTick) && pm.time.tick >= digestTick,
            timeoutSeconds,
            ctx.cancellationToken);
    }

    public static void RegisterPrefab(ScenarioContext ctx, GameObject prefab, bool pooled = false, int warmupCount = 0)
    {
        ctx.predictionManager.predictedPrefabs.prefabs.Add(new PredictedPrefab
        {
            prefab = prefab,
            pooled = pooled,
            warmupCount = warmupCount
        });

        if (pooled)
            ctx.predictionManager.predictedPrefabs = ctx.predictionManager.predictedPrefabs;
    }

    public static GameObject CreatePrefab<T>(string name) where T : PredictedIdentity
    {
        var go = new GameObject(name);
        go.SetActive(false);
        go.AddComponent<T>();
        UnityEngine.Object.DontDestroyOnLoad(go);
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
            sb.Append($"[{(int)details.prefabId}:{details.pieceIndex.value}:{details.instanceId.instanceId.value}:{owner}");

            if (details.instanceId.TryGetComponent<PawnIdentity>(pm, out var pawn))
                sb.Append($":sum={pawn.currentState.sum}:proj={pawn.currentState.projectiles}");

            sb.Append(']');
        }

        bool any = false;
        foreach (var details in entries)
        {
            if (!details.instanceId.TryGetComponent<PredictedParent>(pm, out var carrier))
                continue;

            if (!any)
            {
                sb.Append("|parents=");
                any = true;
            }

            var link = carrier.currentState.parent;
            sb.Append('[').Append(details.instanceId.instanceId.value).Append("->");
            sb.Append(link.HasValue ? link.Value.objectId.instanceId.value.ToString() : "root");
            sb.Append(']');
        }

        return sb.ToString();
    }

    public static void AppendIdentities<T>(PredictionManager pm, int prefabId, StringBuilder sb, Func<T, string> digest)
        where T : Component
    {
        ref var state = ref pm.hierarchy.currentState;
        var entries = new List<InstanceDetails>();
        for (var i = 0; i < state.spawnedPrefabs.Count; i++)
        {
            var details = state.spawnedPrefabs[i];
            if (details.prefabId == prefabId)
                entries.Add(details);
        }

        entries.Sort((a, b) => a.instanceId.instanceId.value.CompareTo(b.instanceId.instanceId.value));

        sb.Append('|').Append(typeof(T).Name).Append('=');
        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i].instanceId.TryGetComponent<T>(pm, out var instance))
                sb.Append('[').Append(digest(instance)).Append(']');
            else
                sb.Append("[missing:").Append(entries[i].instanceId.instanceId.value).Append(']');
        }
    }
}
