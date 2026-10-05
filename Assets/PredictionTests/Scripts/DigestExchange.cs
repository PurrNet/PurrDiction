using System.Collections.Generic;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using PurrNet;
using UnityEngine;

public static class DigestExchange
{
    private static readonly ScenarioStateLedger<Dictionary<PlayerID, string>> _reports = new();
    private static readonly ScenarioStateLedger<Task<ScenarioResult>> _inFlight = new();

    internal static void Reset()
    {
        _reports.Clear();
        _inFlight.Clear();
    }

    internal static void RemoveCompleted()
    {
        _reports.RemoveCompleted();
        _inFlight.RemoveCompleted();
    }

    /// <summary>
    /// Clients report their digest to the server; the server compares every report against
    /// its own digest. Reports and completed comparisons belong to one scenario, allowing
    /// both Host RunSplit halves to share a comparison without consuming each other's reports.
    /// </summary>
    public static async UniTask<ScenarioResult> Compare(ScenarioContext ctx, int channel, string localDigest, float timeoutSeconds)
    {
        ScenarioSynchronization.BeginScenario(ctx.scenarioIndex);
        if (ctx.role == NetworkRole.Client)
            Report(ctx.scenarioIndex, channel, localDigest);

        if (!ctx.isServer || ctx.externalClientCount == 0)
            return ScenarioResult.Ok(localDigest);

        if (!_inFlight.TryGet(ctx.scenarioIndex, channel, out var task))
        {
            task = CompareOnServer(ctx, channel, localDigest, timeoutSeconds).AsTask();
            _inFlight.TryAdd(ctx.scenarioIndex, channel, task);
        }
        return await task;
    }

    private static async UniTask<ScenarioResult> CompareOnServer(ScenarioContext ctx, int channel, string localDigest, float timeoutSeconds)
    {
        int expected = ctx.externalClientCount;
        var start = Time.realtimeSinceStartupAsDouble;

        while (true)
        {
            if (TryGetReports(ctx.scenarioIndex, channel, out var received) && received.Count >= expected)
                break;

            int connected = ConnectedExternalCount(ctx);
            if (connected < expected)
            {
                int got = received?.Count ?? 0;
                return ScenarioResult.Fail(
                    $"digest aborted: only {connected}/{expected} clients still connected (got {got} reports)");
            }

            if (Time.realtimeSinceStartupAsDouble - start > timeoutSeconds)
            {
                int got = received?.Count ?? 0;
                return ScenarioResult.Fail($"digest reports timeout: got {got}/{expected}");
            }

            ctx.cancellationToken.ThrowIfCancellationRequested();
            await UniTask.NextFrame();
        }

        TryGetReports(ctx.scenarioIndex, channel, out var reports);
        var failures = new List<string>();
        foreach (var (player, digest) in reports)
        {
            if (digest != localDigest)
                failures.Add($"player {player.id.value} diverged: '{digest}' != server '{localDigest}'");
        }

        return failures.Count == 0
            ? ScenarioResult.Ok(localDigest)
            : ScenarioResult.Fail(string.Join(" | ", failures));
    }

    private static int ConnectedExternalCount(ScenarioContext ctx)
    {
        var connected = ctx.networkManager.players;
        var localId = ctx.networkManager.localPlayer;
        bool isHost = ctx.role == NetworkRole.Host;

        int count = 0;
        for (int i = 0; i < connected.Count; i++)
        {
            if (isHost && connected[i] == localId)
                continue;
            count++;
        }
        return count;
    }

    internal static bool TryGetReports(int scenarioIndex, int channel, out Dictionary<PlayerID, string> reports)
        => _reports.TryGet(scenarioIndex, channel, out reports);

    internal static void RecordReport(int scenarioIndex, int channel, PlayerID sender, string digest)
    {
        if (!ScenarioSynchronization.IsOpen(scenarioIndex))
            return;
        if (!_reports.TryGet(scenarioIndex, channel, out var reports))
        {
            reports = new Dictionary<PlayerID, string>();
            _reports.TryAdd(scenarioIndex, channel, reports);
        }
        reports[sender] = digest;
    }

    [ServerRpc(requireOwnership: false)]
    private static void Report(int scenarioIndex, int channel, string digest, RPCInfo info = default)
        => RecordReport(scenarioIndex, channel, info.sender, digest);
}
