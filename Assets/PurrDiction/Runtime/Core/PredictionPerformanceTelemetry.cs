using System;
using System.Diagnostics;
using UnityEngine;

namespace PurrNet.Prediction
{
    /// <summary>One bounded, opt-in measurement session for a single prediction world.</summary>
    /// <remarks>
    /// Durations use Stopwatch, not Unity's scaled time. Collection allocates only in Begin/End.
    /// Physics includes callbacks invoked by Unity's Simulate, but excludes the manager's
    /// before/after physics hooks. Percentiles are logarithmic histogram upper bounds, with
    /// at most approximately 2.2% bucket quantization; totals, means and maxima are exact.
    /// This diagnostic API is intended for controlled experiments, not adaptive scheduling.
    /// </remarks>
    public static class PredictionPerformanceTelemetry
    {
        private static Collector _collector;
        private static double _reconcileIntervalSeconds;

        /// <summary>
        /// Experimental minimum interval between client reconciliation batches; zero preserves
        /// normal behavior. Forward/input ticks continue and every queued frame is processed
        /// using the normal baseline and stale-frame rules when a batch starts. Applies only
        /// to pure clients. Increasing this delays corrections and verified callbacks.
        /// </summary>
        public static double reconcileIntervalSeconds
        {
            get => _reconcileIntervalSeconds;
            set
            {
                if (double.IsNaN(value) || double.IsInfinity(value) || value < 0)
                    throw new ArgumentOutOfRangeException(nameof(value));
                _reconcileIntervalSeconds = value;
            }
        }

        public static bool isCollecting => _collector != null;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset()
        {
            _collector = null;
            _reconcileIntervalSeconds = 0;
        }

        public static void Begin(PredictionManager world)
        {
            if (!world)
                throw new ArgumentNullException(nameof(world));
            if (_collector != null)
                throw new InvalidOperationException("A prediction performance session is already active.");
            _collector = new Collector(world);
        }

        public static PredictionPerformanceSnapshot End()
        {
            var collector = _collector;
            if (collector == null)
                throw new InvalidOperationException("No prediction performance session is active.");
            _collector = null;
            return collector.Snapshot();
        }

        private static Collector For(PredictionManager world)
        {
            var collector = _collector;
            return collector != null && ReferenceEquals(collector.world, world) ? collector : null;
        }

        internal static PassScope BeginPass(PredictionManager world, PredictionPassKind kind)
        {
            var collector = For(world);
            if (collector == null)
                return default;
            collector.ObserveFrame();
            return new PassScope(collector, kind);
        }

        internal static BatchScope BeginBatch(PredictionManager world, int pendingFrames)
        {
            var collector = For(world);
            if (collector == null)
                return default;
            collector.ObserveFrame();
            collector.pendingFrames.Add(pendingFrames);
            return new BatchScope(collector);
        }

        internal static void ObserveFrame(PredictionManager world)
        {
            For(world)?.ObserveFrame();
        }

        internal static void StateRestored(PredictionManager world)
        {
            var collector = For(world);
            if (collector != null)
                collector.physicsAfterRestore = true;
        }

        internal static void CadenceDeferred(PredictionManager world)
        {
            var collector = For(world);
            if (collector != null)
                collector.cadenceDeferredChecks++;
        }

        internal readonly struct PassScope : IDisposable
        {
            private readonly Collector _collector;
            private readonly PredictionPassKind _kind;
            private readonly long _started;

            internal PassScope(Collector collector, PredictionPassKind kind)
            {
                _collector = collector;
                _kind = kind;
                _started = Stopwatch.GetTimestamp();
            }

            internal long Timestamp() => _collector == null ? 0 : Stopwatch.GetTimestamp();

            internal void PrepareDone(long started)
            {
                if (_collector != null)
                    _collector.passes[(int)_kind].prepareMs.Add(ElapsedMs(started));
            }

            internal void SimulateDone(long started)
            {
                if (_collector != null)
                    _collector.passes[(int)_kind].simulateMs.Add(ElapsedMs(started));
            }

            internal void LateDone(long started)
            {
                if (_collector != null)
                    _collector.passes[(int)_kind].lateMs.Add(ElapsedMs(started));
            }

            internal void PhysicsDone(long started)
            {
                if (_collector == null)
                    return;
                double elapsed = ElapsedMs(started);
                _collector.passes[(int)_kind].physicsMs.Add(elapsed);
                if (_collector.physicsAfterRestore)
                    _collector.firstPhysicsAfterRestore.Add(elapsed);
                else
                    _collector.otherPhysics.Add(elapsed);
                _collector.physicsAfterRestore = false;
                _collector.currentFramePhysicsMs += elapsed;
            }

            public void Dispose()
            {
                if (_collector == null)
                    return;
                double elapsed = ElapsedMs(_started);
                _collector.passes[(int)_kind].totalMs.Add(elapsed);
                _collector.currentFramePasses++;
                // A reconciliation batch already includes its replay and verified passes.
                if (_kind == PredictionPassKind.Forward)
                    _collector.currentFrameWorkMs += elapsed;
            }
        }

        internal readonly struct BatchScope : IDisposable
        {
            private readonly Collector _collector;
            private readonly long _started;
            private readonly long _verifiedBefore;
            private readonly long _replayedBefore;

            internal BatchScope(Collector collector)
            {
                _collector = collector;
                _started = Stopwatch.GetTimestamp();
                _verifiedBefore = collector.passes[(int)PredictionPassKind.Verified].totalMs.count;
                _replayedBefore = collector.passes[(int)PredictionPassKind.SpeculativeReplay].totalMs.count;
            }

            public void Dispose()
            {
                if (_collector == null)
                    return;
                double elapsed = ElapsedMs(_started);
                long verified = _collector.passes[(int)PredictionPassKind.Verified].totalMs.count - _verifiedBefore;
                long replayed = _collector.passes[(int)PredictionPassKind.SpeculativeReplay].totalMs.count - _replayedBefore;
                _collector.batchMs.Add(elapsed);
                _collector.verifiedFramesPerBatch.Add(verified);
                _collector.replayDepthTicks.Add(replayed);
                _collector.correctionBatches++;
                _collector.currentFrameWorkMs += elapsed;
                _collector.currentFrameBatches++;
            }
        }

        private static double ElapsedMs(long started) =>
            (Stopwatch.GetTimestamp() - started) * (1000d / Stopwatch.Frequency);

        internal sealed class Collector
        {
            internal readonly PredictionManager world;
            private readonly long _started = Stopwatch.GetTimestamp();
            private readonly double _intervalAtStart = reconcileIntervalSeconds;
            private readonly int _firstFrame = Time.frameCount;
            private int _frame = Time.frameCount;
            internal readonly PassAccumulator[] passes =
            {
                new PassAccumulator(), new PassAccumulator(), new PassAccumulator(), new PassAccumulator()
            };
            internal readonly MetricAccumulator firstPhysicsAfterRestore = new MetricAccumulator();
            internal readonly MetricAccumulator otherPhysics = new MetricAccumulator();
            internal readonly MetricAccumulator pendingFrames = new MetricAccumulator();
            internal readonly MetricAccumulator replayDepthTicks = new MetricAccumulator();
            internal readonly MetricAccumulator verifiedFramesPerBatch = new MetricAccumulator();
            internal readonly MetricAccumulator batchMs = new MetricAccumulator();
            private readonly MetricAccumulator _frameWorkMs = new MetricAccumulator();
            private readonly MetricAccumulator _framePhysicsMs = new MetricAccumulator();
            private readonly MetricAccumulator _framePasses = new MetricAccumulator();
            private readonly MetricAccumulator _frameBatches = new MetricAccumulator();
            private readonly MetricAccumulator _frameDeltaMs = new MetricAccumulator();
            internal bool physicsAfterRestore;
            internal double currentFrameWorkMs;
            internal double currentFramePhysicsMs;
            internal long currentFramePasses;
            internal long currentFrameBatches;
            internal long correctionBatches;
            internal long cadenceDeferredChecks;

            internal Collector(PredictionManager world) => this.world = world;

            internal void ObserveFrame()
            {
                int frame = Time.frameCount;
                if (_frame == frame)
                    return;
                // Begin/End can occur anywhere in a render frame. Exclude their partial
                // boundary frames from frame distributions, but retain every measured pass.
                if (_frame != _firstFrame)
                {
                    _frameWorkMs.Add(currentFrameWorkMs);
                    _framePhysicsMs.Add(currentFramePhysicsMs);
                    _framePasses.Add(currentFramePasses);
                    _frameBatches.Add(currentFrameBatches);
                    // Unity's current delta is the interval of the completed previous frame.
                    _frameDeltaMs.Add(Time.unscaledDeltaTime * 1000d);
                }
                _frame = frame;
                currentFrameWorkMs = 0;
                currentFramePhysicsMs = 0;
                currentFramePasses = 0;
                currentFrameBatches = 0;
            }

            internal PredictionPerformanceSnapshot Snapshot()
            {
                ObserveFrame();
                return new PredictionPerformanceSnapshot
                {
                    durationSeconds = ElapsedMs(_started) / 1000d,
                    reconcileIntervalSeconds = _intervalAtStart,
                    forward = passes[0].Snapshot(),
                    verified = passes[1].Snapshot(),
                    gapCatchup = passes[2].Snapshot(),
                    speculativeReplay = passes[3].Snapshot(),
                    firstPhysicsAfterRestore = firstPhysicsAfterRestore.Snapshot(),
                    otherPhysics = otherPhysics.Snapshot(),
                    frameWorkMs = _frameWorkMs.Snapshot(),
                    framePhysicsMs = _framePhysicsMs.Snapshot(),
                    framePasses = _framePasses.Snapshot(),
                    frameBatches = _frameBatches.Snapshot(),
                    frameDeltaMs = _frameDeltaMs.Snapshot(),
                    replayDepthTicks = replayDepthTicks.Snapshot(),
                    pendingFrames = pendingFrames.Snapshot(),
                    verifiedFramesPerBatch = verifiedFramesPerBatch.Snapshot(),
                    batchMs = batchMs.Snapshot(),
                    correctionBatches = correctionBatches,
                    appliedVerifiedFrames = passes[1].totalMs.count,
                    cadenceDeferredChecks = cadenceDeferredChecks
                };
            }
        }

        internal sealed class PassAccumulator
        {
            internal readonly MetricAccumulator totalMs = new MetricAccumulator();
            internal readonly MetricAccumulator prepareMs = new MetricAccumulator();
            internal readonly MetricAccumulator simulateMs = new MetricAccumulator();
            internal readonly MetricAccumulator physicsMs = new MetricAccumulator();
            internal readonly MetricAccumulator lateMs = new MetricAccumulator();

            internal PredictionPassMetrics Snapshot() => new PredictionPassMetrics
            {
                count = totalMs.count,
                totalMs = totalMs.Snapshot(),
                prepareMs = prepareMs.Snapshot(),
                simulateMs = simulateMs.Snapshot(),
                physicsMs = physicsMs.Snapshot(),
                lateMs = lateMs.Snapshot()
            };
        }

        internal sealed class MetricAccumulator
        {
            private const int Offset = 1024;
            private const int Subdivisions = 32;
            private readonly long[] _histogram = new long[2049];
            internal long count;
            private double _total;
            private double _min = double.PositiveInfinity;
            private double _max;

            internal void Add(double value)
            {
                count++;
                _total += value;
                _min = Math.Min(_min, value);
                _max = Math.Max(_max, value);
                int bucket = value <= 0 ? 0 : Math.Max(1, Math.Min(_histogram.Length - 1,
                    (int)Math.Ceiling(Math.Log(value, 2d) * Subdivisions) + Offset));
                _histogram[bucket]++;
            }

            private double Percentile(double fraction)
            {
                if (count == 0)
                    return 0;
                long target = (long)Math.Ceiling(count * fraction);
                long cumulative = 0;
                for (int i = 0; i < _histogram.Length; i++)
                {
                    cumulative += _histogram[i];
                    if (cumulative >= target)
                        return i == 0 ? 0 : Math.Min(_max, Math.Pow(2d, (i - Offset) / (double)Subdivisions));
                }
                return _max;
            }

            internal PredictionMetricSummary Snapshot() => new PredictionMetricSummary
            {
                count = count,
                total = _total,
                mean = count == 0 ? 0 : _total / count,
                min = count == 0 ? 0 : _min,
                max = _max,
                p50 = Percentile(0.5),
                p95 = Percentile(0.95),
                p99 = Percentile(0.99)
            };
        }
    }

    internal enum PredictionPassKind
    {
        Forward,
        Verified,
        GapCatchup,
        SpeculativeReplay
    }

    [Serializable]
    public sealed class PredictionPerformanceSnapshot
    {
        public double durationSeconds;
        public double reconcileIntervalSeconds;
        public bool frameSamplesExcludeBoundaryFrames = true;
        public string percentileMethod = "Logarithmic histogram upper bounds, 32 subdivisions per power of two (~2.2% quantization).";
        public PredictionPassMetrics forward, verified, gapCatchup, speculativeReplay;
        public PredictionMetricSummary firstPhysicsAfterRestore, otherPhysics;
        public PredictionMetricSummary frameWorkMs, framePhysicsMs, framePasses, frameBatches, frameDeltaMs;
        public PredictionMetricSummary replayDepthTicks, pendingFrames, verifiedFramesPerBatch, batchMs;
        public long correctionBatches, appliedVerifiedFrames, cadenceDeferredChecks;
    }

    [Serializable]
    public sealed class PredictionPassMetrics
    {
        public long count;
        public PredictionMetricSummary totalMs, prepareMs, simulateMs, physicsMs, lateMs;
    }

    [Serializable]
    public sealed class PredictionMetricSummary
    {
        public long count;
        public double total, mean, min, max, p50, p95, p99;
    }
}
