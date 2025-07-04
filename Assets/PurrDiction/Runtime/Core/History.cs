using System;
using System.Collections.Generic;

namespace PurrNet.Prediction
{
    public sealed class History<T> where T : struct, IDisposable
    {
        struct Entry
        {
            public ulong Tick;

            public T Data;
        }

        readonly List<Entry> m_data;

        readonly int m_maxCount;

        readonly int m_limitToCut;

        /// <summary>
        /// Access values directly by index
        /// </summary>
        /// <value></value>
        public T this[int index]
        {
            get
            {
                if (index < 0 || index >= m_data.Count)
                {
                    throw new IndexOutOfRangeException($"Index {index} is out of range. Only from 0 to {m_data.Count - 1} is valid.");
                }
                return m_data[index].Data;
            }
            set
            {
                var v = m_data[index];
                v.Data = value;
                m_data[index] = v;
            }
        }

        /// <summary>
        /// Number of entries
        /// </summary>
        /// <value></value>
        public int Count => m_data.Count;

        public int Capacity => m_maxCount;

        /// <summary>
        /// Value of the most recent received tick
        /// </summary>
        /// <value></value>
        public ulong MostRecentTick => m_data.Count == 0 ? 0 : m_data[^1].Tick;

        /// <summary>
        /// Oldest tick we have in the set (this will vary as older entries are purged)
        /// </summary>
        /// <value></value>
        public ulong OldestTick => m_data.Count == 0 ? 0 : m_data[0].Tick;

        /// <summary>
        /// Gets the tick value for the index in the internal collection
        /// </summary>
        /// <param name="index"></param>
        /// <returns>Tick number</returns>
        public ulong GetEntryTick(int index)
        {
            return m_data[index].Tick;
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
                m_data = new List<Entry>(m_maxCount);
            }
            else
            {
                m_data = new List<Entry>();
            }
        }

        /// <summary>
        /// Writes history, adds an entry to the collection.
        /// </summary>
        /// <param name="tick">Which tick did this happen in.</param>
        /// <param name="data">What is the state/data of the tick.</param>
        public void Write(ulong tick, T data)
        {
            var entry = new Entry {
                Tick = tick,
                Data = data
            };

            if (Find(tick, out var index))
            {
                // Override existing data
                var old = m_data[index];
                old.Data.Dispose();
                m_data[index] = entry;
                return;
            }

            // Insert new data
            m_data.Insert(index, entry);

            if (m_maxCount > 0)
                TryToDownsize();
        }

        private void TryToDownsize()
        {
            if (Count < m_limitToCut)
            {
                // Not enough to worry about
                return;
            }

            // Lets trim to the desired max values
            int toRemove = m_data.Count - m_maxCount;

            for (int i = 0; i < toRemove; i++)
                m_data[i].Data.Dispose();

            m_data.RemoveRange(0, toRemove);
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

            for (int i = 0; i < index; i++)
                m_data[i].Data.Dispose();

            m_data.RemoveRange(0, index);
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

            for (int i = index; i < m_data.Count; i++)
                m_data[i].Data.Dispose();

            m_data.RemoveRange(index, m_data.Count - index);
        }

        public void Clear()
        {
            for (int i = 0; i < m_data.Count; i++)
                m_data[i].Data.Dispose();
            m_data.Clear();
        }

        /// <summary>
        /// Returns if possible the data at the specified tick number.
        /// </summary>
        /// <param name="tick">The tick you are searching for.</param>
        /// <param name="result">The data stored or default if not found.</param>
        /// <returns></returns>
        public bool Read(ulong tick, out T result)
        {
            result = default;

            bool found = Find(tick, out var index);

            if (found)
            {
                result = this[index];
            }

            return found;
        }

        public T ReadOrDefault(ulong tick)
        {
            T result = default;

            bool found = Find(tick, out var index);

            if (found)
                result = this[index];

            return result;
        }

        public bool TryGet(ulong tick, out T result)
        {
            return Read(tick, out result);
        }

        public bool TryGetClosest(ulong tick, out T result)
        {
            result = default;

            if (m_data.Count == 0)
                return false;

            if (Find(tick, out var index))
            {
                result = this[index];
                return true;
            }

            if (index == 0)
            {
                result = this[index];
                return true;
            }

            if (index == m_data.Count)
            {
                result = this[index - 1];
                return true;
            }

            var diff1 = tick - m_data[index - 1].Tick;
            var diff2 = m_data[index].Tick - tick;

            if (diff1 < diff2)
            {
                result = this[index - 1];
                return true;
            }

            result = this[index];
            return true;
        }

        public bool TryGetClosest(ulong tick, out T result, out ulong tickDifference)
        {
            result = default;

            if (m_data.Count == 0)
            {
                tickDifference = 0;
                return false;
            }

            if (Find(tick, out var index))
            {
                result = this[index];
                tickDifference = 0;
                return true;
            }

            if (index == 0)
            {
                result = this[index];
                var resultTick = m_data[index].Tick;
                if (resultTick > tick)
                     tickDifference = resultTick - tick;
                else tickDifference = tick - resultTick;
                return true;
            }

            if (index == m_data.Count)
            {
                result = this[index - 1];
                var resultTick = m_data[index - 1].Tick;
                if (resultTick > tick)
                     tickDifference = resultTick - tick;
                else tickDifference = tick - resultTick;
                return true;
            }

            var diff1 = tick - m_data[index - 1].Tick;
            var diff2 = m_data[index].Tick - tick;

            if (diff1 < diff2)
            {
                result = this[index - 1];
                var resultTick = m_data[index - 1].Tick;
                if (resultTick > tick)
                     tickDifference = resultTick - tick;
                else tickDifference = tick - resultTick;
                return true;
            }

            result = this[index];
            var resultTick2 = m_data[index].Tick;
            if (resultTick2 > tick)
                 tickDifference = resultTick2 - tick;
            else tickDifference = tick - resultTick2;
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
            int min = 0;
            int max = m_data.Count - 1;

            while (min <= max)
            {
                int mid = (min + max) / 2;

                if (tick == m_data[mid].Tick)
                {
                    result = mid;
                    return true;
                }

                if (tick < m_data[mid].Tick)
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
            m_data.TrimExcess();
        }

        public void Remove(ulong tick)
        {
            if (Find(tick, out var index))
            {
                m_data[index].Data.Dispose();
                m_data.RemoveAt(index);
            }
        }
    }
}
