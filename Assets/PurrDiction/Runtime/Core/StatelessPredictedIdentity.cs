using PurrNet.Modules;
using PurrNet.Packing;

namespace PurrNet.Prediction
{
    public static class StatelessHeSaidPacker
    {
        [UsedByIL]
        public static void Write(BitPacker stream, StatelessHeSaid value) { }

        [UsedByIL]
        public static void Read(BitPacker stream, ref StatelessHeSaid value) { }

        [UsedByIL]
        public static bool WriteDelta(BitPacker stream, StatelessHeSaid oldValue, StatelessHeSaid value)
        {
            Packer<bool>.Write(stream, false);
            return false;
        }

        [UsedByIL]
        public static void ReadDelta(BitPacker stream, StatelessHeSaid oldValue, ref StatelessHeSaid value)
        {
            stream.AdvanceBits(1);
        }
    }

    public struct StatelessHeSaid : IPredictedData<StatelessHeSaid>
    {
        public void Dispose() { }
    }

    public abstract class StatelessPredictedIdentity : PredictedIdentity<StatelessHeSaid>
    {
        protected override void Simulate(ref StatelessHeSaid state, float delta)
        {
            Simulate(delta);
        }

        protected override StatelessHeSaid Interpolate(StatelessHeSaid from, StatelessHeSaid to, float t) => to;

        protected virtual void Simulate(float delta) {}
    }
}
