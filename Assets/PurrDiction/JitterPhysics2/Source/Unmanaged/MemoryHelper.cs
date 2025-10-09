/*
 * Jitter2 Physics Library
 * (c) Thorben Linneweber and contributors
 * SPDX-License-Identifier: MIT
 */

using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Real = PurrNet.Prediction.FP64;

namespace Jitter2.Unmanaged
{
    public static unsafe class MemoryHelper
    {
        /// <summary>
        /// A memory block with a size equivalent to six instances of the <see cref="Real"/> type.
        /// </summary>
        /// <remarks>
        /// The struct uses sequential layout and a fixed size to ensure consistent memory alignment and layout.
        /// </remarks>
        [StructLayout(LayoutKind.Sequential, Size = 6 * Real.SIZE_OF)]
        public struct MemBlock6Real { }

        /// <summary>
        /// A memory block with a size equivalent to nine instances of the <see cref="Real"/> type.
        /// </summary>
        /// <remarks>
        /// The struct uses sequential layout and a fixed size to ensure consistent memory alignment and layout.
        /// </remarks>
        [StructLayout(LayoutKind.Sequential, Size = 9 * Real.SIZE_OF)]
        public struct MemBlock9Real { }

        /// <summary>
        /// A memory block with a size equivalent to twelve instances of the <see cref="Real"/> type.
        /// </summary>
        /// <remarks>
        /// The struct uses sequential layout and a fixed size to ensure consistent memory alignment and layout.
        /// </remarks>
        [StructLayout(LayoutKind.Sequential, Size = 12 * Real.SIZE_OF)]
        public struct MemBlock12Real { }

        /// <summary>
        /// A memory block with a size equivalent to sixteen instances of the <see cref="Real"/> type.
        /// </summary>
        /// <remarks>
        /// The struct uses sequential layout and a fixed size to ensure consistent memory alignment and layout.
        /// </remarks>
        [StructLayout(LayoutKind.Sequential, Size = 16 * Real.SIZE_OF)]
        public struct MemBlock16Real { }

        /// <summary>
        /// Allocates a block of unmanaged memory for an array of the specified type.
        /// </summary>
        /// <typeparam name="T">The unmanaged type of the elements to allocate memory for.</typeparam>
        /// <param name="num">The number of elements to allocate memory for.</param>
        /// <returns>A pointer to the allocated memory block.</returns>
        public static T* AllocateHeap<T>(int num) where T : unmanaged
        {
            return (T*)AllocateHeap(num * sizeof(T));
        }

        /// <summary>
        /// Allocates a block of aligned unmanaged memory for an array of the specified type.
        /// </summary>
        /// <typeparam name="T">The unmanaged type of the elements to allocate memory for.</typeparam>
        /// <param name="num">The number of elements to allocate memory for.</param>
        /// <param name="alignment"></param>
        /// <returns>A pointer to the allocated memory block.</returns>
        public static T* AlignedAllocateHeap<T>(int num, int alignment) where T : unmanaged
        {
            return (T*)AlignedAllocateHeap(num * sizeof(T), alignment);
        }

        /// <summary>
        /// Frees a block of unmanaged memory previously allocated for an array of the specified type.
        /// </summary>
        /// <typeparam name="T">The unmanaged type of the elements in the memory block.</typeparam>
        /// <param name="ptr">A pointer to the memory block to free.</param>
        public static void Free<T>(T* ptr) where T : unmanaged
        {
            Free((void*)ptr);
        }

        /// <summary>
        /// Allocates a block of unmanaged memory of the specified length in bytes.
        /// </summary>
        /// <param name="len">The length of the memory block to allocate, in bytes.</param>
        /// <returns>A pointer to the allocated memory block.</returns>
        public static void* AllocateHeap(int len)
        {
            return UnsafeUtility.Malloc(
                len,
                16,
                Allocator.Persistent
            );
        }

        /// <summary>
        /// Allocates a block of aligned unmanaged memory of the specified length in bytes.
        /// </summary>
        /// <param name="len">The length of the memory block to allocate, in bytes.</param>
        /// <param name="alignment"></param>
        /// <returns>A pointer to the allocated memory block.</returns>
        public static void* AlignedAllocateHeap(int len, int alignment)
        {
            return UnsafeUtility.Malloc(
                len,
                alignment,
                Allocator.Persistent
            );
        }

        /// <summary>
        /// Frees a block of unmanaged memory previously allocated.
        /// </summary>
        /// <param name="ptr">A pointer to the memory block to free.</param>
        public static void Free(void* ptr)
        {
            UnsafeUtility.Free(ptr, Allocator.Persistent);
        }

        /// <summary>
        /// Frees a block of aligned unmanaged memory previously allocated.
        /// </summary>
        /// <param name="ptr">A pointer to the aligned memory block to free.</param>
        public static void AlignedFree(void* ptr)
        {
            UnsafeUtility.Free(ptr, Allocator.Persistent);
        }

        /// <summary>
        /// Zeros out unmanaged memory.
        /// </summary>
        /// <param name="buffer">A pointer to the memory block to zero out.</param>
        /// <param name="len">The length of the memory block to zero out, in bytes.</param>
        public static void MemSet(void* buffer, int len)
        {
            for (int i = 0; i < len; i++)
            {
                *((byte*)buffer + i) = 0;
            }
        }
    }
}
