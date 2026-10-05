using PurrNet;

public static class DigestGate
{
    private static readonly ScenarioStateLedger<ulong> _digestTicks = new();

    public static void Reset() => _digestTicks.Clear();

    internal static void RemoveCompleted() => _digestTicks.RemoveCompleted();

    public static bool TryGetDigestTick(int scenarioIndex, int channel, out ulong tick)
        => _digestTicks.TryGet(scenarioIndex, channel, out tick);

    internal static void RecordDigestTick(int scenarioIndex, int channel, ulong tick)
        => _digestTicks.TryAdd(scenarioIndex, channel, tick);

    [ObserversRpc(runLocally: true)]
    public static void BroadcastDigestTick(int scenarioIndex, int channel, ulong tick)
        => RecordDigestTick(scenarioIndex, channel, tick);
}
