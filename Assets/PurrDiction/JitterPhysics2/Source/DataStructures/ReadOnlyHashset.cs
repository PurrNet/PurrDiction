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
    /// Implements a read-only wrapper for <see cref="HashSet{T}"/>.
    /// </summary>
    public readonly struct ReadOnlyHashSet<T> : IReadOnlyCollection<T>
    {
        private readonly HashSet<T> _hashset;

        /// <summary>
        /// Implements a read-only wrapper for <see cref="HashSet{T}"/>.
        /// </summary>
        public ReadOnlyHashSet(HashSet<T> hashset)
        {
            _hashset = hashset;
        }

        public HashSet<T>.Enumerator GetEnumerator()
        {
            return _hashset.GetEnumerator();
        }

        IEnumerator<T> IEnumerable<T>.GetEnumerator()
        {
            return _hashset.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return _hashset.GetEnumerator();
        }

        public int Count => _hashset.Count;
    }
}