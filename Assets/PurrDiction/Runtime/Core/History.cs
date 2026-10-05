using System;
using System.Runtime.CompilerServices;

namespace PurrNet.Prediction
{
    public sealed class History<T> where T : struct, IDisposable
    {
        ulong[] m_ticks;

        T[] m_values;

        int m_head;

        int m_count;

        readonly int m_maxCount;

        readonly int m_limitToCut;

        bool m_contiguous = true;

        int Phys(int index)
        {
            int phys = m_head + index;
            if (phys >= m_ticks.Length)
                phys -= m_ticks.Length;
            return phys;
        }

        /// <summary>
        /// Access values directly by index
        /// </summary>
        public T this[int index]
        {
            get
            {
                if (index < 0 || index >= m_count)
                {
                    throw new IndexOutOfRangeException($"Index {index} is out of range. Only from 0 to {m_count - 1} is valid.");
                }
                return m_values[Phys(index)];
            }
            set
            {
                if (index < 0 || index >= m_count)
                {
                    throw new IndexOutOfRangeException($"Index {index} is out of range. Only from 0 to {m_count - 1} is valid.");
                }
                m_values[Phys(index)] = value;
            }
        }

        /// <summary>
        /// Number of entries
        /// </summary>
        public int Count => m_count;

        public int Capacity => m_maxCount;

        /// <summary>
        /// Value of the most recent received tick
        /// </summary>
        public ulong MostRecentTick => m_count == 0 ? 0 : m_ticks[Phys(m_count - 1)];

        /// <summary>
        /// Oldest tick we have in the set (this will vary as older entries are purged)
        /// </summary>
        public ulong OldestTick => m_count == 0 ? 0 : m_ticks[m_head];

        /// <summary>
        /// Gets the tick value for the index in the internal collection
        /// </summary>
        /// <returns>Tick number</returns>
        public ulong GetEntryTick(int index)
        {
            if (index < 0 || index >= m_count)
            {
                throw new IndexOutOfRangeException($"Index {index} is out of range. Only from 0 to {m_count - 1} is valid.");
            }
            return m_ticks[Phys(index)];
        }

        /// <summary>
        /// Creates a new history collection
        /// </summary>
        /// <param name="maxEntries">Number of entries you can have at once reliably,
        /// anything past this number can be cleaned at any time without your input. -1 means no limit thus no cleaning.</param>
        public History(int maxEntries = -1)
        {
            m_maxCount = maxEntries;

            if (m_maxCount > 0)
            {
                m_limitToCut = Math.Max(m_maxCount + 10, m_maxCount + m_maxCount / 2);
                m_ticks = new ulong[m_limitToCut + 1];
                m_values = new T[m_limitToCut + 1];
            }
            else
            {
                m_ticks = new ulong[16];
                m_values = new T[16];
            }
        }

        void Grow()
        {
            int newCapacity = m_ticks.Length * 2;
            if (m_maxCount > 0)
                newCapacity = Math.Min(newCapacity, m_limitToCut + 1);
            ResizeBackingStorage(newCapacity);
        }

        // Compaction must preserve every baseline and value without disposing ownership.
        internal bool TryCompactBackingStorage(int maximumPayloadBytes)
        {
            if (m_maxCount <= 0)
                return false;
            int newCapacity = Math.Max(1, m_count);
            if (newCapacity >= m_ticks.Length ||
                (long)newCapacity * (Unsafe.SizeOf<T>() + sizeof(ulong)) > maximumPayloadBytes)
                return false;
            ResizeBackingStorage(newCapacity);
            return true;
        }

        // Keep the baseline's latest preceding entry as an anchor, even with a removal budget.
        internal int PruneBeforeBaseline(ulong baselineTick, int maximumEntriesToRemove = int.MaxValue)
        {
            if (m_count <= 1 || maximumEntriesToRemove <= 0)
                return 0;
            bool found = Find(baselineTick, out int index);
            int firstIndexToKeep = found ? index : Math.Max(0, index - 1);
            int removeCount = Math.Min(firstIndexToKeep, maximumEntriesToRemove);
            if (removeCount > 0)
                RemoveFront(removeCount);
            return removeCount;
        }

        // Restore capacity before Setup to avoid deferring growth into later simulation.
        internal long RestoreEagerBackingStorage()
        {
            if (m_maxCount <= 0 || m_ticks.Length >= m_limitToCut + 1)
                return 0;
            ResizeBackingStorage(m_limitToCut + 1);
            return BackingStoragePayloadBytes;
        }

        internal bool HasEagerBackingStorage => m_maxCount > 0 && m_ticks.Length >= m_limitToCut + 1;

        internal long BackingStoragePayloadBytes =>
            (long)m_ticks.Length * (Unsafe.SizeOf<T>() + sizeof(ulong));

        private void ResizeBackingStorage(int newCapacity)
        {
            var newTicks = new ulong[newCapacity];
            var newValues = new T[newCapacity];

            int firstChunk = Math.Min(m_count, m_ticks.Length - m_head);
            Array.Copy(m_ticks, m_head, newTicks, 0, firstChunk);
            Array.Copy(m_values, m_head, newValues, 0, firstChunk);

            int remaining = m_count - firstChunk;
            if (remaining > 0)
            {
                Array.Copy(m_ticks, 0, newTicks, firstChunk, remaining);
                Array.Copy(m_values, 0, newValues, firstChunk, remaining);
            }

            m_ticks = newTicks;
            m_values = newValues;
            m_head = 0;
        }

        /// <summary>
        /// Writes history, adds an entry to the collection.
        /// </summary>
        /// <param name="tick">Which tick did this happen in.</param>
        /// <param name="data">What is the state/data of the tick.</param>
        public void Write(ulong tick, in T data)
        {
            if (m_count == 0 || tick > m_ticks[Phys(m_count - 1)])
            {
                if (m_count > 0 && tick != m_ticks[Phys(m_count - 1)] + 1)
                    m_contiguous = false;

                if (m_count == m_ticks.Length)
                    Grow();

                int phys = Phys(m_count);
                m_ticks[phys] = tick;
                m_values[phys] = data;
                m_count++;

                if (m_maxCount > 0)
                    TryToDownsize();
                return;
            }

            if (Find(tick, out var index))
            {
                int phys = Phys(index);
                m_values[phys].Dispose();
                m_values[phys] = data;
                return;
            }

            InsertAt(index, tick, data);
            m_contiguous = false;

            if (m_maxCount > 0)
                TryToDownsize();
        }

        void InsertAt(int index, ulong tick, in T data)
        {
            if (m_count == m_ticks.Length)
                Grow();

            for (int i = m_count; i > index; i--)
            {
                int dst = Phys(i);
                int src = Phys(i - 1);
                m_ticks[dst] = m_ticks[src];
                m_values[dst] = m_values[src];
            }

            int phys = Phys(index);
            m_ticks[phys] = tick;
            m_values[phys] = data;
            m_count++;
        }

        private void TryToDownsize()
        {
            if (m_count < m_limitToCut)
            {
                return;
            }

            RemoveFront(m_count - m_maxCount);
        }

        private void RemoveFront(int count)
        {
            for (int i = 0; i < count; i++)
            {
                int phys = Phys(i);
                m_values[phys].Dispose();
                m_values[phys] = default;
            }

            m_head = Phys(count);
            m_count -= count;

            if (m_count <= 1)
                m_contiguous = true;
        }

        private void RemoveBack(int count)
        {
            for (int i = m_count - count; i < m_count; i++)
            {
                int phys = Phys(i);
                m_values[phys].Dispose();
                m_values[phys] = default;
            }

            m_count -= count;

            if (m_count <= 1)
                m_contiguous = true;
        }

        /// <summary>
        /// Removes entries older than the configured history window while retaining the
        /// latest entry at or before the cutoff as an anchor for sparse history reads.
        /// </summary>
        public void PruneByTickWindow(ulong currentTick)
        {
            if (m_maxCount <= 0 || m_count <= 1)
                return;

            ulong window = (ulong)m_maxCount;
            if (currentTick <= window)
                return;

            PruneBeforeBaseline(currentTick - window);
        }

        /// <summary>
        /// Clear all the data before 'tick'
        /// </summary>
        /// <param name="tick">Clear everything before this</param>
        /// <param name="inclusive">Include the tick number in the clear</param>
        public void ClearPast(ulong tick, bool inclusive = false)
        {
            if (Find(tick, out var index) && inclusive)
            {
                index += 1;
            }

            RemoveFront(index);
        }

        /// <summary>
        /// Clear all the data after 'tick'
        /// </summary>
        /// <param name="tick">Clear everything after this</param>
        /// <param name="inclusive">Include the tick number in the clear</param>
        public void ClearFuture(ulong tick, bool inclusive = false)
        {
            if (Find(tick, out var index) && !inclusive)
            {
                index += 1;
            }

            RemoveBack(m_count - index);
        }

        public void Clear()
        {
            RemoveFront(m_count);
            m_head = 0;
            m_contiguous = true;
        }

        /// <summary>
        /// Returns if possible the data at the specified tick number.
        /// </summary>
        /// <param name="tick">The tick you are searching for.</param>
        /// <param name="result">The data stored or default if not found.</param>
        public bool Read(ulong tick, out T result)
        {
            if (Find(tick, out var index))
            {
                result = m_values[Phys(index)];
                return true;
            }

            result = default;
            return false;
        }

        public T ReadOrDefault(ulong tick)
        {
            return Find(tick, out var index) ? m_values[Phys(index)] : default;
        }

        public bool TryGet(ulong tick, out T result)
        {
            return Read(tick, out result);
        }

        /// <summary>
        /// Returns the data at the specified tick, or if no exact match exists,
        /// returns the closest previous entry (the most recent entry before the tick).
        /// </summary>
        /// <param name="tick">The tick you are searching for.</param>
        /// <param name="result">The data stored or default if not found.</param>
        /// <returns>True if an exact or previous entry was found.</returns>
        public bool ReadOrPrevious(ulong tick, out T result)
        {
            if (Find(tick, out var index))
            {
                result = m_values[Phys(index)];
                return true;
            }

            // index is the insertion point; index-1 is the closest previous entry
            if (index > 0)
            {
                result = m_values[Phys(index - 1)];
                return true;
            }

            result = default;
            return false;
        }

        public bool TryGetClosest(ulong tick, out T result)
        {
            return TryGetClosest(tick, out result, out _);
        }

        public bool TryGetClosest(ulong tick, out T result, out ulong tickDifference)
        {
            if (m_count == 0)
            {
                result = default;
                tickDifference = 0;
                return false;
            }

            if (Find(tick, out var index))
            {
                result = m_values[Phys(index)];
                tickDifference = 0;
                return true;
            }

            if (index == m_count || (index > 0 && tick - m_ticks[Phys(index - 1)] < m_ticks[Phys(index)] - tick))
                index -= 1;

            result = m_values[Phys(index)];
            var resultTick = m_ticks[Phys(index)];
            tickDifference = resultTick > tick ? resultTick - tick : tick - resultTick;
            return true;
        }

        /// <summary>
        /// Does a binary search on the internal entries.
        /// </summary>
        /// <param name="tick">The tick you are looking for.</param>
        /// <param name="result">The index that is either your result or the closest index, this can change when Writing new data.</param>
        /// <returns>Returns if a match was found or not.</returns>
        public bool Find(ulong tick, out int result)
        {
            if (m_count == 0)
            {
                result = 0;
                return false;
            }

            ulong first = m_ticks[m_head];

            if (tick < first)
            {
                result = 0;
                return false;
            }

            if (m_contiguous)
            {
                ulong offset = tick - first;

                if (offset < (ulong)m_count)
                {
                    result = (int)offset;
                    return true;
                }

                result = m_count;
                return false;
            }

            int min = 0;
            int max = m_count - 1;

            while (min <= max)
            {
                int mid = (min + max) / 2;
                ulong midTick = m_ticks[Phys(mid)];

                if (tick == midTick)
                {
                    result = mid;
                    return true;
                }

                if (tick < midTick)
                {
                    max = mid - 1;
                }
                else
                {
                    min = mid + 1;
                }
            }

            result = min;
            return false;
        }

        /// <summary>
        /// Throw any cached data at the garbage collector
        /// </summary>
        public void TrimExcessMemory()
        {
            if (m_maxCount > 0 || m_count > m_ticks.Length / 2 || m_ticks.Length <= 16)
                return;

            ResizeBackingStorage(Math.Max(16, m_count));
        }

        public void Remove(ulong tick)
        {
            if (!Find(tick, out var index))
                return;

            int phys = Phys(index);
            m_values[phys].Dispose();

            for (int i = index; i < m_count - 1; i++)
            {
                int dst = Phys(i);
                int src = Phys(i + 1);
                m_ticks[dst] = m_ticks[src];
                m_values[dst] = m_values[src];
            }

            m_values[Phys(m_count - 1)] = default;
            m_count--;

            if (index != 0 && index != m_count)
                m_contiguous = false;

            if (m_count <= 1)
                m_contiguous = true;
        }
    }
}
