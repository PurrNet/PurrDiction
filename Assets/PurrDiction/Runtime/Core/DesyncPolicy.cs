using PurrNet.Packing;
using PurrNet.Transports;

namespace PurrNet.Prediction
{
    /// <summary>
    /// How the server responds when a client's deterministic state diverges from its own.
    /// Detection compares tick-salted hashes of settled deterministic state that clients
    /// report to the server; Ignore skips hashing entirely and has zero overhead.
    /// </summary>
    public enum DesyncPolicy : byte
    {
        /// <summary>No hashing, no detection. Zero bandwidth and CPU overhead.</summary>
        Ignore = 0,
        /// <summary>Detect divergence and raise events on both server and client. No automatic recovery.</summary>
        Report = 1,
        /// <summary>Detect divergence and re-baseline the client's entire timeline with a full frame.</summary>
        Resync = 2,
        /// <summary>Detect divergence and heal the diverged identity by sending its authoritative state.</summary>
        Correct = 3
    }

    /// <summary>
    /// Per-identity override of the global <see cref="DesyncPolicy"/> configured on the PredictionManager.
    /// </summary>
    public enum DesyncPolicyOverride : byte
    {
        /// <summary>Use the PredictionManager's global desync policy.</summary>
        Inherit = 0,
        Ignore = 1,
        Report = 2,
        Resync = 3,
        Correct = 4
    }

    internal static class DesyncPolicyResolution
    {
        public static DesyncPolicy Resolve(DesyncPolicyOverride overridePolicy, DesyncPolicy globalPolicy)
        {
            return overridePolicy switch
            {
                DesyncPolicyOverride.Ignore => DesyncPolicy.Ignore,
                DesyncPolicyOverride.Report => DesyncPolicy.Report,
                DesyncPolicyOverride.Resync => DesyncPolicy.Resync,
                DesyncPolicyOverride.Correct => DesyncPolicy.Correct,
                _ => globalPolicy
            };
        }
    }

    internal static class DeterministicStateHash
    {
        const uint FnvOffset = 2166136261u;
        const uint FnvPrime = 16777619u;

        public static ushort Compute(ulong tick, BitPacker packer)
        {
            uint h = FnvOffset;
            for (int shift = 0; shift < 64; shift += 8)
                h = (h ^ (byte)(tick >> shift)) * FnvPrime;

            ByteData data = packer.ToByteData();
            int bits = packer.positionInBits;
            int fullBytes = bits >> 3;
            var span = data.span;

            for (int i = 0; i < fullBytes; i++)
                h = (h ^ span[i]) * FnvPrime;

            int rem = bits & 7;
            if (rem != 0)
                h = (h ^ (uint)(span[fullBytes] & ((1 << rem) - 1))) * FnvPrime;

            return (ushort)(h ^ (h >> 16));
        }
    }
}
