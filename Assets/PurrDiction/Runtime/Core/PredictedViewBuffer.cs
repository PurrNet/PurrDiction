using System;
using Unity.IL2CPP.CompilerServices;

namespace PurrNet.Prediction
{
    // Ring indices are always wrapped into range; see History for why bounds checks are off.
    [Il2CppSetOption(Option.ArrayBoundsChecks, false)]
    internal sealed class PredictedViewBuffer<T> where T : struct, IDisposable
    {
        private struct Entry
        {
            public ulong tick;
            public T value;
        }

        // Pending samples, oldest first, in a ring addressed by reference so reads copy no states.
        private Entry[] _samples;
        private int _length;
        private int _head;
        private int _count;
        private readonly LerpFunction<T> _lerp;
        private readonly int _capacity;

        private T _anchor;
        private ulong _anchorTick;

        public PredictedViewBuffer(LerpFunction<T> lerp, ulong tick, T initial, int capacity)
        {
            _lerp = lerp ?? throw new ArgumentNullException(nameof(lerp));
            _capacity = Math.Max(1, capacity);
            _samples = new Entry[_capacity + 1];
            _length = _samples.Length;
            _anchor = initial;
            _anchorTick = tick;
        }

        internal int capacity => _capacity;

        /// <summary>
        /// Samples not yet reached by the presented tick.
        /// </summary>
        public int Count => _count;

        public ulong anchorTick => _anchorTick;

        private ref Entry At(int index)
        {
            int slot = _head + index;
            if (slot >= _length)
                slot -= _length;
            return ref _samples[slot];
        }

        private void DropOldest()
        {
            At(0) = default;
            _head = _head + 1 == _length ? 0 : _head + 1;
            _count--;
        }

        /// <summary>
        /// Tick of the oldest pending sample, the one the anchor lerps toward. A gap larger than one
        /// tick means the ticks in between were never latched (skipped by a lead jump, or a replay
        /// re-created this object and only its head state was latched afterwards), so the view
        /// glides linearly across them.
        /// </summary>
        public bool TryGetNextTick(out ulong tick)
        {
            if (_count == 0)
            {
                tick = 0;
                return false;
            }

            tick = At(0).tick;
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
                DisposeHeld(_anchor);
                _anchor = value;
                return;
            }

            if (tick < _anchorTick)
            {
                Teleport(tick, value);
                return;
            }

            int index = _count;
            while (index > 0 && At(index - 1).tick > tick)
                index--;

            if (index > 0 && At(index - 1).tick == tick)
            {
                ref var existing = ref At(index - 1);
                DisposeHeld(existing.value);
                existing.value = value;
                return;
            }

            if (_count == _length)
                Grow();
            for (int i = _count; i > index; i--)
                At(i) = At(i - 1);
            At(index) = new Entry { tick = tick, value = value };
            _count++;

            while (_count > _capacity)
            {
                DisposeHeld(At(0).value);
                DropOldest();
            }
        }

        private void Grow()
        {
            var grown = new Entry[_samples.Length * 2];
            for (int i = 0; i < _count; i++)
                grown[i] = At(i);
            _samples = grown;
            _length = grown.Length;
            _head = 0;
        }

        // A slot still holding default(T) (never filled, or teleported to default on release) owns nothing.
        static void DisposeHeld(T value)
        {
            if (!StateLifetime.IsUnset(ref value))
                value.Dispose();
        }

        /// <summary>
        /// Drops every pending sample and restarts the view from <paramref name="value"/> at
        /// <paramref name="tick"/>. Ownership of the value transfers to the buffer.
        /// </summary>
        public void Teleport(ulong tick, T value)
        {
            while (_count > 0)
            {
                DisposeHeld(At(0).value);
                DropOldest();
            }
            _head = 0;

            DisposeHeld(_anchor);
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
            while (_count > 0 && At(0).tick <= presentTick)
            {
                ref var reached = ref At(0);
                DisposeHeld(_anchor);
                _anchor = reached.value;
                _anchorTick = reached.tick;
                DropOldest();
            }

            if (_count == 0 || presentTick <= _anchorTick)
                return _lerp(_anchor, _anchor, 1f);

            ref var next = ref At(0);
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
            for (int i = _count - 1; i >= 0 && At(i).tick > presentTick; i--)
                ahead++;
            return ahead;
        }
    }
}
