using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using PurrNet;

public static class DigestExchange
{
    private static readonly Dictionary<int, Dictionary<PlayerID, string>> _reports = new();

    /// <summary>
    /// Clients report their digest to the server; the server compares every report against
    /// its own digest and fails on any mismatch. Returns Ok on pure clients — the server
    /// result is authoritative for the run.
    /// </summary>
    public static async UniTask<ScenarioResult> Compare(ScenarioContext ctx, int channel, string localDigest, float timeoutSeconds)
    {
        if (ctx.role == NetworkRole.Client)
            Report(channel, localDigest);

        if (!ctx.isServer)
            return ScenarioResult.Ok(localDigest);

        int expected = ctx.externalClientCount;

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => _reports.TryGetValue(channel, out var r) && r.Count >= expected,
                timeoutSeconds,
                ctx.cancellationToken);
        }
        catch (System.TimeoutException)
        {
            _reports.TryGetValue(channel, out var partial);
            return ScenarioResult.Fail($"digest reports timeout: got {partial?.Count ?? 0}/{expected}");
        }

        var reports = _reports[channel];
        var failures = new List<string>();

        foreach (var (player, digest) in reports)
        {
            if (digest != localDigest)
                failures.Add($"player {player.id.value} diverged: '{digest}' != server '{localDigest}'");
        }

        _reports.Remove(channel);

        return failures.Count == 0
            ? ScenarioResult.Ok(localDigest)
            : ScenarioResult.Fail(string.Join(" | ", failures));
    }

    [ServerRpc(requireOwnership: false)]
    private static void Report(int channel, string digest, RPCInfo info = default)
    {
        if (!_reports.TryGetValue(channel, out var set))
        {
            set = new Dictionary<PlayerID, string>();
            _reports[channel] = set;
        }
        set[info.sender] = digest;
    }
}
