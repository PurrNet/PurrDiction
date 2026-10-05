using System;
using System.Collections.Generic;

namespace PurrNet.Prediction
{
    internal sealed class PredictedViewBuffer<T> where T : struct, IDisposable
    {
        private struct Entry
        {
            public ulong tick;
            public T value;
        }

        private readonly List<Entry> _samples;
        private readonly LerpFunction<T> _lerp;
        private readonly int _capacity;

        private T _anchor;
        private ulong _anchorTick;

        public PredictedViewBuffer(LerpFunction<T> lerp, ulong tick, T initial, int capacity)
        {
            _lerp = lerp ?? throw new ArgumentNullException(nameof(lerp));
            _capacity = Math.Max(1, capacity);
            _samples = new List<Entry>(_capacity + 1);
            _anchor = initial;
            _anchorTick = tick;
        }

        internal int capacity => _capacity;

        /// <summary>
        /// Samples not yet reached by the presented tick.
        /// </summary>
        public int Count => _samples.Count;

        public ulong anchorTick => _anchorTick;

        /// <summary>
        /// Tick of the oldest pending sample, the one the anchor lerps toward. A gap larger than one
        /// tick means the ticks in between were never latched (skipped by a lead jump, or a replay
        /// re-created this object and only its head state was latched afterwards), so the view
        /// glides linearly across them.
        /// </summary>
        public bool TryGetNextTick(out ulong tick)
        {
            if (_samples.Count == 0)
            {
                tick = 0;
                return false;
            }

            tick = _samples[0].tick;
            return true;
        }

        /// <summary>
        /// Stores the state for <paramref name="tick"/>. A sample for the same tick as the anchor
        /// replaces it. A sample older than the anchor means the timeline moved backwards (a
        /// resync re-anchored the view ahead of the head), so the view restarts from that sample
        /// rather than freezing until the clock catches up. The oldest pending sample is dropped
        /// once the buffer exceeds its capacity.
        /// </summary>
        public void Add(ulong tick, T value)
        {
            if (tick == _anchorTick)
            {
                _anchor.Dispose();
                _anchor = value;
                return;
            }

            if (tick < _anchorTick)
            {
                Teleport(tick, value);
                return;
            }

            int index = _samples.Count;
            while (index > 0 && _samples[index - 1].tick > tick)
                index--;

            if (index > 0 && _samples[index - 1].tick == tick)
            {
                var existing = _samples[index - 1];
                existing.value.Dispose();
                existing.value = value;
                _samples[index - 1] = existing;
                return;
            }

            _samples.Insert(index, new Entry { tick = tick, value = value });

            while (_samples.Count > _capacity)
            {
                _samples[0].value.Dispose();
                _samples.RemoveAt(0);
            }
        }

        /// <summary>
        /// Drops every pending sample and restarts the view from <paramref name="value"/> at
        /// <paramref name="tick"/>. Ownership of the value transfers to the buffer.
        /// </summary>
        public void Teleport(ulong tick, T value)
        {
            for (int i = 0; i < _samples.Count; i++)
                _samples[i].value.Dispose();
            _samples.Clear();

            _anchor.Dispose();
            _anchor = value;
            _anchorTick = tick;
        }

        /// <summary>
        /// Returns the state to present for <paramref name="presentTick"/>: the lerp between the
        /// newest sample at or before it and the next one, holding on the anchor when no later
        /// sample exists yet. The result is transient and must not be disposed by the caller.
        /// </summary>
        public T Sample(double presentTick)
        {
            while (_samples.Count > 0 && _samples[0].tick <= presentTick)
            {
                _anchor.Dispose();
                _anchor = _samples[0].value;
                _anchorTick = _samples[0].tick;
                _samples.RemoveAt(0);
            }

            if (_samples.Count == 0 || presentTick <= _anchorTick)
                return _lerp(_anchor, _anchor, 1f);

            var next = _samples[0];
            double span = next.tick - (double)_anchorTick;
            float t = span > 0d ? (float)((presentTick - _anchorTick) / span) : 1f;
            if (t < 0f) t = 0f;
            else if (t > 1f) t = 1f;
            return _lerp(_anchor, next.value, t);
        }

        /// <summary>
        /// Samples that lie after <paramref name="presentTick"/> without committing anything.
        /// </summary>
        public int CountAhead(double presentTick)
        {
            int ahead = 0;
            for (int i = _samples.Count - 1; i >= 0 && _samples[i].tick > presentTick; i--)
                ahead++;
            return ahead;
        }
    }
}
