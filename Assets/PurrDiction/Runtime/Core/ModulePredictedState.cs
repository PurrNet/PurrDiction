using PurrNet.Packing;
using Unity.Profiling;

namespace PurrNet.Prediction
{
    internal struct ModulePredictedState : IPredictedData<ModulePredictedState>
    {
        public bool wasOnSimulationStartCalled;

        public void Dispose() { }
    }

    internal struct MODULE_STATE<T> : IAuthoritativeState<MODULE_STATE<T>>, IPackedAuto
        where T : struct, IPredictedData<T>
    {
        public T state;
        public ModulePredictedState prediction;

        private static readonly ProfilerMarker _deepCopyMarker = new("DeepCopy.Module." + typeof(T).FullName);

        public MODULE_STATE<T> DeepCopy()
        {
            using (_deepCopyMarker.Auto())
            {
                return new MODULE_STATE<T>
                {
                    state = PurrCopy<T>.Copy(state),
                    prediction = prediction
                };
            }
        }

        public bool HasSameContents(ref MODULE_STATE<T> other)
        {
            return Packer.AreEqualRef(ref prediction, ref other.prediction) &&
                   Packer.AreEqualRef(ref state, ref other.state);
        }

        public void Dispose()
        {
            state.Dispose();
            prediction.Dispose();
        }
    }
}
