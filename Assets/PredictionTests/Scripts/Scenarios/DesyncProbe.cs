using PurrNet.Prediction;

public class DesyncProbe : DeterministicIdentity<DesyncProbe.ProbeState>
{
    public struct ProbeState : IPredictedData<ProbeState>
    {
        public ulong count;

        public void Dispose() { }
    }

    public ulong corruptFromTick { get; set; }
    public ulong corruptionsApplied { get; private set; }
    public ulong verifiedCorruptionsApplied { get; private set; }
    public ulong firstCorruptionTick { get; private set; }
    public ulong lastCorruptionTick { get; private set; }

    protected override void Simulate(ref ProbeState state, sfloat delta)
    {
        ulong tick = predictionManager.localTickInContext;
        if (AdvanceWithFault(ref state, tick, corruptFromTick))
        {
            if (corruptionsApplied == 0)
                firstCorruptionTick = tick;
            lastCorruptionTick = tick;
            corruptionsApplied++;
            if (predictionManager.isVerified)
                verifiedCorruptionsApplied++;
        }
    }

    internal static bool AdvanceWithFault(ref ProbeState state, ulong tick, ulong faultStartsAt)
    {
        state.count += 1;
        // Keep the deliberate fault present until the real desync notification stops it.
        // A single count crossing can be erased by a later full authoritative state,
        // before any of the periodic unreliable hash reports observes that crossing.
        if (faultStartsAt == 0 || tick < faultStartsAt)
            return false;
        state.count += 9999;
        return true;
    }

    public string Digest()
    {
        return $"delta={(long)currentState.count - (long)predictionManager.time.tick}";
    }
}
