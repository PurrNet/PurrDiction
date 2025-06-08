using System;
using PurrNet.Packing;

namespace PurrNet.Prediction
{
    internal struct FULL_STATE<T> : IDisposable, IPackedAuto
        where T : struct, IPredictedData<T>
    {
        public T state;
        public PredictedIdentityState prediction;

        public FULL_STATE<T> DeepCopy()
        {
#if UNITY_EDITOR
            var initialMemory = GC.GetTotalMemory(false);
#endif
            var result = new FULL_STATE<T>
            {
                state = Packer.Copy(state),
                prediction = prediction
            };

#if UNITY_EDITOR
            var finalMemory = GC.GetTotalMemory(false);
            if (finalMemory > initialMemory)
                UnityEngine.Debug.LogWarning($"DeepCopy of FULL_STATE<{typeof(T).Name}> increased memory usage by {finalMemory - initialMemory} bytes.");
#endif

            return result;
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
