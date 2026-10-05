using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using PurrNet;
using UnityEngine;

public static class ScenarioBarrier
{
    private static readonly ScenarioStateLedger<HashSet<PlayerID>> _arrived = new();
    private static readonly ScenarioStateLedger<Task> _inFlight = new();
    private static readonly ScenarioStateLedger<ScenarioResult> _results = new();

    internal static void Reset()
    {
        _arrived.Clear();
        _inFlight.Clear();
        _results.Clear();
    }

    internal static void RemoveCompleted()
    {
        _arrived.RemoveCompleted();
        _inFlight.RemoveCompleted();
        _results.RemoveCompleted();
    }

    public static async UniTask Wait(ScenarioContext ctx, int barrierId, float timeoutSeconds)
    {
        ScenarioSynchronization.BeginScenario(ctx.scenarioIndex);
        if (!_inFlight.TryGet(ctx.scenarioIndex, barrierId, out var task))
        {
            task = WaitImpl(ctx, barrierId, timeoutSeconds).AsTask();
            _inFlight.TryAdd(ctx.scenarioIndex, barrierId, task);
        }
        // Keep completed tasks until the epoch closes, so both Host RunSplit halves
        // share the same result even when the first half completes synchronously.
        await task;
    }

    private static async UniTask WaitImpl(ScenarioContext ctx, int barrierId, float timeoutSeconds)
    {
        if (ctx.isClient)
            ReportArrived(ctx.scenarioIndex, barrierId);

        if (ctx.isServer)
        {
            try
            {
                await UniTaskUtils.WaitWithTimeout(
                    () => _arrived.TryGet(ctx.scenarioIndex, barrierId, out var clients) &&
                          clients.Count >= ctx.expectedConnections,
                    timeoutSeconds,
                    ctx.cancellationToken);
            }
            catch (TimeoutException)
            {
                _arrived.TryGet(ctx.scenarioIndex, barrierId, out var arrived);
                var message = $"barrier timeout scenario={ctx.scenarioIndex} barrier={barrierId} " +
                              $"arrived={arrived?.Count ?? 0}/{ctx.expectedConnections} role={ctx.role}";
                BroadcastResult(ctx.scenarioIndex, barrierId, false, message);
                Debug.LogError($"[ScenarioBarrier] {message}");
                throw new TimeoutException(message);
            }

            BroadcastResult(ctx.scenarioIndex, barrierId, true, null);
        }

        if (ctx.isClient)
        {
            await UniTaskUtils.WaitWithTimeout(
                () => TryGetResult(ctx.scenarioIndex, barrierId, out _),
                timeoutSeconds,
                ctx.cancellationToken);
            TryGetResult(ctx.scenarioIndex, barrierId, out var result);
            if (!result.success)
                throw new TimeoutException(result.message);
        }
    }

    internal static bool TryGetResult(int scenarioIndex, int barrierId, out ScenarioResult result)
        => _results.TryGet(scenarioIndex, barrierId, out result);

    internal static void RecordArrival(int scenarioIndex, int barrierId, PlayerID sender)
    {
        if (!ScenarioSynchronization.IsOpen(scenarioIndex))
            return;
        if (!_arrived.TryGet(scenarioIndex, barrierId, out var clients))
        {
            clients = new HashSet<PlayerID>();
            _arrived.TryAdd(scenarioIndex, barrierId, clients);
        }
        clients.Add(sender);
    }

    internal static void RecordResult(int scenarioIndex, int barrierId, bool success, string message)
        => _results.TryAdd(scenarioIndex, barrierId,
            success ? ScenarioResult.Ok() : ScenarioResult.Fail(message));

    [ServerRpc(requireOwnership: false)]
    private static void ReportArrived(int scenarioIndex, int barrierId, RPCInfo info = default)
        => RecordArrival(scenarioIndex, barrierId, info.sender);

    [ObserversRpc(runLocally: true)]
    private static void BroadcastResult(int scenarioIndex, int barrierId, bool success, string message)
        => RecordResult(scenarioIndex, barrierId, success, message);
}
