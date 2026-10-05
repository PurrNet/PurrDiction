using PurrNet.Packing;
using Unity.Profiling;

namespace PurrNet.Prediction
{
    internal struct FULL_STATE<T> : IAuthoritativeState<FULL_STATE<T>>, IPackedAuto
        where T : struct, IPredictedData<T>
    {
        public T state;
        public PredictedIdentityState prediction;

        static readonly ProfilerMarker SimulateMarker = new("DeepCopy." + typeof(T).FullName);

        public FULL_STATE<T> DeepCopy()
        {
            using (SimulateMarker.Auto())
            {
                return new FULL_STATE<T>
                {
                    state = PurrCopy<T>.Copy(state),
                    prediction = prediction
                };
            }
        }

        public bool HasSameContents(ref FULL_STATE<T> other)
        {
            return Packer.AreEqualRef(ref prediction, ref other.prediction) &&
                   Packer.AreEqualRef(ref state, ref other.state);
        }

        public void Dispose()
        {
            state.Dispose();
            prediction.Dispose();
        }

        public override string ToString()
        {
            return $"{{state: {state}, prediction: {prediction}}}";
        }
    }
}
