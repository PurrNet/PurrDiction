using System;
using PurrNet.Packing;
using Unity.Profiling;

namespace PurrNet.Prediction
{
    internal struct FULL_STATE<T> : IDisposable, IPackedAuto
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
                    state = Packer.Copy(state),
                    prediction = prediction
                };
            }
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
