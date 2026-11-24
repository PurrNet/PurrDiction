using System;

namespace PurrNet.Prediction
{
    /// <summary>
    /// The clock relayed identities are presented on, on the server's timeline. It runs in real time, apart from
    /// the prediction view clock, and is steered toward the newest verified state less a cushion: the lateness
    /// that <see cref="coverage"/> of the recent frames landed within, as a jitter buffer sizes its delay. A frame
    /// later than that holds the view until it lands; the clock then plays the backlog through up to
    /// <see cref="maxCatchUp"/> faster rather than jumping it, and settles back as the late frames leave the
    /// window. Rare bursts leave the cushion where it was; frequent ones raise it until they are ridden out.
    /// </summary>
    internal sealed class RelayViewClock
    {
        internal const double DefaultCoverage = 0.95d;
        internal const double DefaultMaxCatchUp = 0.25d;

        /// <summary>
        /// Seconds of frames the lateness is measured over.
        /// </summary>
        internal const double WindowSeconds = 8d;

        /// <summary>
        /// Largest share by which relayed objects run slower to build a larger cushion.
        /// </summary>
        internal const double MaxSlowDown = 0.1d;

        /// <summary>
        /// Speed change per tick off the target, so the clock eases in rather than stopping at it.
        /// </summary>
        internal const double GainPerTick = 0.1d;

        /// <summary>
        /// Share of the spread between the mean and the covered lateness the target keeps on top of the
        /// covered lateness, so on a jittery link a frame landing just past the covered edge does not hold
        /// the view. A steady link has no spread and keeps none. The mean, unlike the median, moves
        /// smoothly when lateness comes in whole render frames, so the margin does not hop.
        /// </summary>
        internal const double MarginShareOfSpread = 0.25d;

        /// <summary>
        /// Most ticks of that margin.
        /// </summary>
        internal const double MaxMarginTicks = 0.5d;

        /// <summary>
        /// Ticks inside the rewind cap the clock aims for when the cap binds, so the small lags of easing
        /// toward the target do not take it over the cap.
        /// </summary>
        internal const double CapHeadroomTicks = 1d;

        private readonly double[] _lateness;
        private readonly double[] _sorted;
        private int _count;
        private int _cursor;
        private int _arrivedSinceAdvance;
        private bool _latenessDirty;
        private double _coveredLateness;
        private double _margin;

        private double _now;
        private double _presented;
        private bool _started;

        private double _coverage = DefaultCoverage;

        public RelayViewClock(int tickRate)
        {
            int window = Math.Max(16, (int)Math.Ceiling(WindowSeconds * Math.Max(1, tickRate)));
            _lateness = new double[window];
            _sorted = new double[window];
        }

        /// <summary>
        /// Share of recent frames the cushion lets land before relayed objects need them, clamped to 0.5–1.
        /// </summary>
        public double coverage
        {
            get => _coverage;
            set
            {
                value = double.IsNaN(value) ? DefaultCoverage : Math.Max(0.5d, Math.Min(1d, value));
                if (value != _coverage)
                    _latenessDirty = true;
                _coverage = value;
            }
        }

        /// <summary>
        /// Largest share by which relayed objects run faster to bring the delay down, clamped to 0–1.
        /// </summary>
        public double maxCatchUp { get; set; } = DefaultMaxCatchUp;

        /// <summary>
        /// Tick relayed identities are presented at, including the fraction.
        /// </summary>
        public double presentedTick => _presented;

        /// <summary>
        /// Speed the last <see cref="Advance"/> ran relayed objects at, 1 being real time.
        /// </summary>
        public double speed { get; private set; } = 1d;

        /// <summary>
        /// A verified frame was applied, leaving relayed identities in the state for
        /// <paramref name="stateTick"/>. Frames are applied before the render frame's
        /// <see cref="Advance"/>, whose view is the first to show them, so their lateness is completed
        /// there, against the time that view presents.
        /// </summary>
        public void RecordArrival(ulong stateTick)
        {
            _lateness[_cursor] = _now - stateTick;
            _cursor = _cursor + 1 == _lateness.Length ? 0 : _cursor + 1;
            if (_count < _lateness.Length)
                _count++;
            _arrivedSinceAdvance++;
            _latenessDirty = true;
        }

        /// <summary>
        /// Moves the clock on by <paramref name="elapsedTicks"/> of real time. It never passes
        /// <paramref name="newestTick"/>, the newest state received, never aims further than
        /// <paramref name="maxOffsetTicks"/> behind <paramref name="viewTick"/> (how far the server rewinds relayed
        /// objects for this client's queries), and never trails the newest state further than view buffers hold.
        /// </summary>
        public void Advance(double elapsedTicks, ulong newestTick, double viewTick, double maxOffsetTicks)
        {
            if (!(elapsedTicks > 0d))
                elapsedTicks = 0d;
            _now += elapsedTicks;
            CompleteArrivals(elapsedTicks);
            if (newestTick == 0 || _count == 0)
                return;

            if (_latenessDirty)
            {
                MeasureLateness();
                _latenessDirty = false;
            }

            // The sample a view at tick T lerps toward is the one for ceil(T): it must have landed.
            double target = _now - _coveredLateness - 1d - _margin;
            if (maxOffsetTicks > 0d && !double.IsPositiveInfinity(maxOffsetTicks))
                target = Math.Max(target, viewTick - Math.Max(0d, maxOffsetTicks - CapHeadroomTicks));

            double next;
            if (!_started)
            {
                _started = true;
                speed = 1d;
                next = target;
            }
            else
            {
                // Against where real time alone would carry the clock: the target has already moved on by
                // this frame's time, and an error that includes it would settle a frame ahead of the target.
                speed = Speed(target - (_presented + elapsedTicks));
                next = _presented + elapsedTicks * speed;
            }

            next = Math.Min(next, newestTick);
            _presented = Math.Max(next, (double)newestTick - PredictionManager.RelayViewMaxExcessTicks);
        }

        /// <summary>
        /// Forgets every frame and the presented tick, for a new session.
        /// </summary>
        public void Reset()
        {
            Array.Clear(_lateness, 0, _lateness.Length);
            _count = 0;
            _cursor = 0;
            _arrivedSinceAdvance = 0;
            _latenessDirty = false;
            _coveredLateness = 0d;
            _margin = 0d;
            _now = 0d;
            _presented = 0d;
            _started = false;
            speed = 1d;
        }

        // Behind the target (error > 0) the clock runs faster, ahead of it slower, in proportion to the gap.
        private double Speed(double error)
        {
            if (error > 0d)
            {
                double catchUp = double.IsNaN(maxCatchUp) ? DefaultMaxCatchUp : Math.Max(0d, Math.Min(1d, maxCatchUp));
                return 1d + Math.Min(catchUp, GainPerTick * error);
            }

            return 1d - Math.Min(MaxSlowDown, GainPerTick * -error);
        }

        // The frames recorded since the last Advance were measured against its time; they are first
        // shown at this one's.
        private void CompleteArrivals(double elapsedTicks)
        {
            int arrived = Math.Min(_arrivedSinceAdvance, _count);
            _arrivedSinceAdvance = 0;
            for (int i = 0, slot = _cursor; i < arrived; i++)
            {
                slot = slot == 0 ? _lateness.Length - 1 : slot - 1;
                _lateness[slot] += elapsedTicks;
            }
        }

        private void MeasureLateness()
        {
            Array.Copy(_lateness, _sorted, _count);
            Array.Sort(_sorted, 0, _count);
            _coveredLateness = SortedAt(_coverage);
            double sum = 0d;
            for (int i = 0; i < _count; i++)
                sum += _sorted[i];
            _margin = Math.Min(MaxMarginTicks, MarginShareOfSpread * Math.Max(0d, _coveredLateness - sum / _count));
        }

        private double SortedAt(double share)
        {
            int index = (int)Math.Ceiling(share * _count) - 1;
            return _sorted[Math.Max(0, Math.Min(_count - 1, index))];
        }
    }
}
