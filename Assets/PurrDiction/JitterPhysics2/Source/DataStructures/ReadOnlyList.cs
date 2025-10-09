/*
 * Jitter2 Physics Library
 * (c) Thorben Linneweber and contributors
 * SPDX-License-Identifier: MIT
 */

using System.Collections;
using System.Collections.Generic;

namespace Jitter2.DataStructures
{
    /// <summary>
    /// Implements a read-only wrapper for <see cref="List{T}"/>.
    /// </summary>
    public readonly struct ReadOnlyList<T> : IReadOnlyCollection<T>
    {
        private readonly List<T> _list;

        /// <summary>
        /// Implements a read-only wrapper for <see cref="List{T}"/>.
        /// </summary>
        public ReadOnlyList(List<T> list)
        {
            _list = list;
        }

        public T this[int i] => _list[i];

        public List<T>.Enumerator GetEnumerator()
        {
            return _list.GetEnumerator();
        }

        IEnumerator<T> IEnumerable<T>.GetEnumerator()
        {
            return _list.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return _list.GetEnumerator();
        }

        public int Count => _list.Count;
    }
}