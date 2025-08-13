using System;
using PurrNet.Pooling;
using PurrNet.Prediction.Profiler;

namespace PurrNet.Prediction.Editor
{
    public struct DeltaTickSample : IDisposable
    {
        public DisposableList<PackingInfo> wroteStates;
        public DisposableList<PackingInfo> readStates;
        public DisposableList<PackingInfo> wroteInputs;
        public DisposableList<PackingInfo> readInputs;

        public static DeltaTickSample CollectFromProfiler()
        {
            var sample = new DeltaTickSample();

            sample.wroteStates = DisposableList<PackingInfo>.Create(256);
            sample.readStates = DisposableList<PackingInfo>.Create(256);
            sample.wroteInputs = DisposableList<PackingInfo>.Create(256);
            sample.readInputs = DisposableList<PackingInfo>.Create(256);

            sample.wroteStates.AddRange(TickBandwidthProfiler.wroteStates);
            sample.readStates.AddRange(TickBandwidthProfiler.readStates);
            sample.wroteInputs.AddRange(TickBandwidthProfiler.wroteInputs);
            sample.readInputs.AddRange(TickBandwidthProfiler.readInputs);

            return sample;
        }

        public void Dispose()
        {
            wroteStates.Dispose();
            readStates.Dispose();
            wroteInputs.Dispose();
            readInputs.Dispose();
        }
    }
}
