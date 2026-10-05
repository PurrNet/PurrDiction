using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("PurrNet.Prediction.EditorTests")]

/// <summary>Synchronization epochs follow the bootstrap's scenario indices, including reconnects.</summary>
public static class ScenarioSynchronization
{
    private static int _completedThrough = -1;

    public static bool IsOpen(int scenarioIndex) => scenarioIndex > _completedThrough;

    public static void Reset()
    {
        _completedThrough = -1;
        ScenarioBarrier.Reset();
        DigestExchange.Reset();
        DigestGate.Reset();
    }

    public static void BeginScenario(int scenarioIndex)
    {
        if (!IsOpen(scenarioIndex))
            throw new InvalidOperationException($"Scenario {scenarioIndex} has already completed.");
        // A faster peer may already have sent this scenario's messages. Keep them.
    }

    public static void EndScenario(int scenarioIndex)
    {
        _completedThrough = Math.Max(_completedThrough, scenarioIndex);
        ScenarioBarrier.RemoveCompleted();
        DigestExchange.RemoveCompleted();
        DigestGate.RemoveCompleted();
    }
}

/// <summary>Exact scenario/channel keys; completed epochs reject even late RPC deliveries.</summary>
internal sealed class ScenarioStateLedger<T>
{
    private readonly Dictionary<(int scenario, int channel), T> _values = new();

    public bool TryGet(int scenario, int channel, out T value)
    {
        if (ScenarioSynchronization.IsOpen(scenario))
            return _values.TryGetValue((scenario, channel), out value);
        value = default;
        return false;
    }

    public bool TryAdd(int scenario, int channel, T value)
        => ScenarioSynchronization.IsOpen(scenario) && _values.TryAdd((scenario, channel), value);

    public void Clear() => _values.Clear();

    public void RemoveCompleted()
    {
        var expired = new List<(int scenario, int channel)>();
        foreach (var key in _values.Keys)
            if (!ScenarioSynchronization.IsOpen(key.scenario))
                expired.Add(key);
        foreach (var key in expired)
            _values.Remove(key);
    }
}
