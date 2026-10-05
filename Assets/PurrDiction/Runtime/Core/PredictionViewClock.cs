using System;

namespace PurrNet.Prediction
{
    internal sealed class PredictionViewClock
    {
        private struct Sample : IDisposable
        {
            public double tick;

            public void Dispose() { }
        }

        private readonly InterpolatedWithDispose<Sample> _buffer;
        private double? _pendingLatch;

        public int tickRate { get; }

        /// <summary>
        /// Prediction tick currently being presented, including the fraction between two ticks.
        /// </summary>
        public double viewTick { get; private set; }

        public bool hasPendingLatch => _pendingLatch.HasValue;

        public int bufferSize => _buffer.bufferSize;

        /// <summary>
        /// The last <see cref="Advance"/> had to trim buffered samples to stay within the cap.
        /// </summary>
        public bool lastAdvanceTrimmed { get; private set; }

        /// <summary>
        /// The last <see cref="Advance"/> ran out of samples and held the newest one.
        /// </summary>
        public bool lastAdvanceStarved { get; private set; }

        public PredictionViewClock(int tickRate, ulong initialTick)
        {
            this.tickRate = tickRate;
            viewTick = initialTick;
            _buffer = new InterpolatedWithDispose<Sample>(
                Lerp,
                1f / tickRate,
                new Sample { tick = initialTick },
                PredictionManager.GetViewInterpolationMaxBufferSize(tickRate));
        }

        private static Sample Lerp(Sample from, Sample to, float t)
            => new Sample { tick = from.tick + (to.tick - from.tick) * t };

        /// <summary>
        /// Records that the state for <paramref name="tick"/> is the newest sample the view can show.
        /// Mirrors the identity latch: a pending latch is replaced, never queued.
        /// </summary>
        public void Latch(ulong tick, bool refreshOnly)
        {
            if (!PredictionManager.ShouldReplaceViewLatch(refreshOnly, _pendingLatch.HasValue))
                return;
            _pendingLatch = tick;
        }

        /// <summary>
        /// Consumes the pending latch and advances the clock by one render frame.
        /// </summary>
        public double Advance(float deltaTime)
        {
            lastAdvanceTrimmed = false;
            if (_pendingLatch.HasValue)
            {
                int depthBeforeAdd = _buffer.bufferSize;
                _buffer.Add(new Sample { tick = _pendingLatch.Value });
                lastAdvanceTrimmed = _buffer.bufferSize <= depthBeforeAdd;
                _pendingLatch = null;
            }

            viewTick = _buffer.Advance(deltaTime).tick;
            lastAdvanceStarved = _buffer.bufferSize == 0;
            return viewTick;
        }
    }
}
