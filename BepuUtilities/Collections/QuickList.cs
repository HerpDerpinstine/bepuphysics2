﻿using System;
using System.Diagnostics;
using BepuUtilities.Memory;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace BepuUtilities.Collections
{
    /// <summary>
    /// Container supporting list-like behaviors built on top of pooled arrays.
    /// </summary>
    /// <remarks>
    /// Be very careful when using this type. It has sacrificed a lot upon the altar of performance; a few notable issues include:
    /// it is a value type and copying it around will break things without extreme care,
    /// it cannot be validly default-constructed,
    /// it exposes internal structures to user modification, 
    /// it rarely checks input for errors,
    /// the enumerator doesn't check for mid-enumeration modification,
    /// it allows unsafe addition that can break if the user doesn't manage the capacity,
    /// it works on top of an abstracted memory blob which might internally be a pointer that could be rugpulled, 
    /// it does not (and is incapable of) checking that provided memory gets returned to the same pool that it came from.
    /// </remarks>
    /// <typeparam name="T">Type of the elements in the list.</typeparam>
    /// <typeparam name="TSpan">Type of the memory span backing the list.</typeparam>
    public struct QuickList<T, TSpan> where TSpan : ISpan<T>
    {
        /// <summary>
        /// Backing memory containing the elements of the list.
        /// Indices from 0 to Count-1 hold actual data. All other data is undefined.
        /// </summary>
        public TSpan Span;

        /// <summary>
        /// Number of elements in the list.
        /// </summary>
        public int Count;

        /// <summary>
        /// Gets a reference to the element at the given index in the list.
        /// </summary>
        /// <param name="index">Index to grab an element from.</param>
        /// <returns>Element at the given index in the list.</returns>
        public ref T this[int index]
        {
            //TODO: check inlining in release
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                ValidateIndex(index);
                return ref Span[index];
            }
        }

        /// <summary>
        /// Creates a new list.
        /// </summary>
        /// <param name="initialSpan">Span to use as backing memory to begin with.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public QuickList(ref TSpan initialSpan)
        {
            Span = initialSpan;
            Count = 0;
        }

        /// <summary>
        /// Creates a new list.
        /// </summary>
        /// <param name="pool">Pool to pull a span from.</param>
        /// <param name="minimumInitialCount">The minimum size of the region to be pulled from the pool. Actual span may be larger.</param>
        /// <param name="list">Created list.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Create<TPool>(TPool pool, int minimumInitialCount, out QuickList<T, TSpan> list) where TPool : IMemoryPool<T, TSpan>
        {
            pool.Take(minimumInitialCount, out list.Span);
            list.Count = 0;
        }


        /// <summary>
        /// Swaps out the list's backing memory span for a new span.
        /// If the new span is smaller, the list's count is truncated and the extra elements are dropped. 
        /// The old span is not cleared or returned to any pool; if it needs to be pooled or cleared, the user must handle it.
        /// </summary>
        /// <param name="newSpan">New span to use.</param>
        /// <param name="oldSpan">Previous span used for elements.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Resize(ref TSpan newSpan, out TSpan oldSpan)
        {
            Validate();
            oldSpan = Span;
            Debug.Assert(oldSpan.Length != newSpan.Length, "Resizing without changing the size is pretty peculiar. Is something broken?");
            Span = newSpan;
            if (Count > Span.Length)
                Count = Span.Length;
            oldSpan.CopyTo(0, ref Span, 0, Count);

        }

        /// <summary>
        /// Resizes the list's backing array for the given size as a power of two.
        /// Any elements that do not fit in the resized span are dropped and the count is truncated.
        /// </summary>
        /// <typeparam name="TPool">Type of the span pool.</typeparam>
        /// <param name="newSizePower">Exponent of the size of the new memory block. New size will be 2^newSizePower.</param>
        /// <param name="pool">Pool to pull a new span from and return the old span to.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ResizeForPower<TPool>(int newSizePower, TPool pool) where TPool : IMemoryPool<T, TSpan>
        {
            var oldList = this;
            pool.TakeForPower(newSizePower, out var newSpan);
            Resize(ref newSpan, out var oldSpan);

            oldList.Dispose(pool);
        }

        /// <summary>
        /// Resizes the list's backing array for the given size.
        /// Any elements that do not fit in the resized span are dropped and the count is truncated.
        /// </summary>
        /// <typeparam name="TPool">Type of the span pool.</typeparam>
        /// <param name="newSize">Minimum number of elements required in the new backing array. Actual capacity of the created span may exceed this size.</param>
        /// <param name="pool">Pool to pull a new span from and return the old span to.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Resize<TPool>(int newSize, TPool pool) where TPool : IMemoryPool<T, TSpan>
        {
            ResizeForPower(SpanHelper.GetContainingPowerOf2(newSize), pool);
        }

        /// <summary>
        /// Returns the resources associated with the list to pools. Any managed references still contained within the list are cleared (and some unmanaged resources may also be cleared).
        /// </summary>
        /// <param name="pool">Pool used for element spans.</param>   
        /// <typeparam name="TPool">Type of the pool used for element spans.</typeparam>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Dispose<TPool>(TPool pool)
             where TPool : IMemoryPool<T, TSpan>
        {
            Span.ClearManagedReferences(0, Count);
            pool.Return(ref Span);
        }

        /// <summary>
        /// Ensures that the list has enough room to hold the specified number of elements. Can be used to initialize a list.
        /// </summary>
        /// <typeparam name="TPool">Type of the pool to pull from.</typeparam>
        /// <param name="count">Number of elements to hold.</param>
        /// <param name="pool">Pool used to obtain a new span if needed.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EnsureCapacity<TPool>(int count, TPool pool) where TPool : IMemoryPool<T, TSpan>
        {
            if (Span.Allocated)
            {
                if (count > Span.Length)
                {
                    Resize(count, pool);
                }
            }
            else
            {
                pool.Take(count, out Span);
            }
        }

        /// <summary>
        /// Compacts the internal buffer to the minimum size required for the number of elements in the list.
        /// </summary>
        public void Compact<TPool>(TPool pool) where TPool : IMemoryPool<T, TSpan>
        {
            Validate();
            var newPoolIndex = SpanHelper.GetContainingPowerOf2(Count);
            if ((1 << newPoolIndex) != Span.Length)
                ResizeForPower(newPoolIndex, pool);
        }

        /// <summary>
        /// Adds the elements of a list to the QuickList without checking capacity.
        /// </summary>
        /// <typeparam name="TSourceSpan">Type of the source span.</typeparam>
        /// <param name="span">Span of elements to add.</param>
        /// <param name="start">Start index of the added range.</param>
        /// <param name="count">Number of elements in the added range.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddRangeUnsafely<TSourceSpan>(ref TSourceSpan span, int start, int count) where TSourceSpan : ISpan<T>
        {
            Validate();
            ValidateUnsafeAdd();
            span.CopyTo(0, ref Span, Count, count);
            Count += count;
        }

        /// <summary>
        /// Adds the elements of a list to the QuickList.
        /// </summary>
        /// <typeparam name="TPool">Type of the pool to pull from.</typeparam>
        /// <typeparam name="TSourceSpan">Type of the source span.</typeparam>
        /// <param name="span">Span of elements to add.</param>
        /// <param name="start">Start index of the added range.</param>
        /// <param name="count">Number of elements in the added range.</param>
        /// <param name="pool">Pool used to obtain a new span if needed.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddRange<TPool, TSourceSpan>(ref TSourceSpan span, int start, int count, TPool pool) where TPool : IMemoryPool<T, TSpan> where TSourceSpan : ISpan<T>
        {
            EnsureCapacity(Count + count, pool);
            AddRangeUnsafely(ref span, start, count);
        }

        /// <summary>
        /// Appends space on the end of the list without checking capacity and returns a reference to it.
        /// </summary>
        /// <returns>Reference to the allocated space.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref T AllocateUnsafely()
        {
            ValidateUnsafeAdd();
            return ref Span[Count++];
        }

        /// <summary>
        /// Appends space on the end of the list and returns a reference to it.
        /// </summary>
        /// <returns>Reference to the allocated space.</returns>
        /// <typeparam name="TPool">Type of the pool to pull from.</typeparam>
        /// <param name="pool">Pool used to obtain a new span if needed.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref T Allocate<TPool>(TPool pool) where TPool : IMemoryPool<T, TSpan>
        {
            if (Count == Span.Length)
                Resize(Count * 2, pool);
            return ref AllocateUnsafely();
        }

        /// <summary>
        /// Adds the element to the list without checking the count against the capacity.
        /// </summary>
        /// <param name="element">Item to add.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddUnsafely(T element)
        {
            Validate();
            ValidateUnsafeAdd();
            AllocateUnsafely() = element;
        }

        /// <summary>
        /// Adds the element to the list.
        /// </summary>
        /// <typeparam name="TPool">Type of the pool to pull from.</typeparam>
        /// <param name="element">Item to add.</param>
        /// <param name="pool">Pool used to obtain a new span if needed.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add<TPool>(T element, TPool pool) where TPool : IMemoryPool<T, TSpan>
        {
            Validate();
            if (Count == Span.Length)
                Resize(Count * 2, pool);
            AddUnsafely(element);
        }

        /// <summary>
        /// Adds the element to the list without checking the count against the capacity.
        /// </summary>
        /// <param name="element">Element to add.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddUnsafely(ref T element)
        {
            Validate();
            ValidateUnsafeAdd();
            AllocateUnsafely() = element;
        }

        /// <summary>
        /// Adds the element to the list.
        /// </summary>
        /// <typeparam name="TPool">Type of the pool to pull from.</typeparam>
        /// <param name="element">Element to add.</param>
        /// <param name="pool">Pool used to obtain a new span if needed.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add<TPool>(ref T element, TPool pool) where TPool : IMemoryPool<T, TSpan>
        {
            if (Count == Span.Length)
                Resize(Count * 2, pool);
            AddUnsafely(ref element);
        }





        /// <summary>
        /// Gets the index of the element in the list using the default comparer, if present.
        /// </summary>
        /// <param name="element">Element to find.</param>
        /// <returns>Index of the element in the list if present, -1 otherwise.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int IndexOf(T element)
        {
            Validate();
            return Span.IndexOf(element, 0, Count);
        }


        /// <summary>
        /// Gets the index of the element in the list using the default comparer, if present.
        /// </summary>
        /// <param name="element">Element to find.</param>
        /// <returns>Index of the element in the list if present, -1 otherwise.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int IndexOf(ref T element)
        {
            Validate();
            return Span.IndexOf(ref element, 0, Count);
        }

        /// <summary>
        /// Gets the index of the first element in the list which matches a predicate, if any.
        /// </summary>
        /// <param name="predicate">Predicate to match.</param>
        /// <returns>Index of the first matching element in the list if present, -1 otherwise.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int IndexOf<TPredicate>(ref TPredicate predicate) where TPredicate : IPredicate<T>
        {
            Validate();
            return Span.IndexOf(ref predicate, 0, Count);
        }


        /// <summary>
        /// Removes an element from the list. Preserves the order of elements.
        /// </summary>
        /// <param name="element">Element to remove from the list.</param>
        /// <returns>True if the element was present and was removed, false otherwise.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Remove(T element)
        {
            Validate();
            var index = IndexOf(element);
            if (index >= 0)
            {
                RemoveAt(index);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Removes an element from the list. Preserves the order of elements.
        /// </summary>
        /// <param name="element">Element to remove from the list.</param>
        /// <returns>True if the element was present and was removed, false otherwise.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Remove(ref T element)
        {
            Validate();
            var index = IndexOf(element);
            if (index >= 0)
            {
                RemoveAt(index);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Removes the first element that matches a predicate from the list. Preserves the order of elements.
        /// </summary>
        /// <param name="predicate">Predicate to test elements with.</param>
        /// <returns>True if an element matched and was removed, false otherwise.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Remove<TPredicate>(ref TPredicate predicate) where TPredicate : IPredicate<T>
        {
            Validate();
            var index = Span.IndexOf(ref predicate, 0, Count);
            if (index >= 0)
            {
                RemoveAt(index);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Removes an element from the list. Comparisons use the default comparer for the type. Does not preserve the order of elements.
        /// </summary>
        /// <param name="element">Element to remove from the list.</param>
        /// <returns>True if the element was present and was removed, false otherwise.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool FastRemove(T element)
        {
            Validate();
            var index = IndexOf(element);
            if (index >= 0)
            {
                FastRemoveAt(index);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Removes an element from the list. Does not preserve the order of elements.
        /// </summary>
        /// <param name="element">Element to remove from the list.</param>
        /// <returns>True if the element was present and was removed, false otherwise.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool FastRemove(ref T element)
        {
            Validate();
            var index = IndexOf(element);
            if (index >= 0)
            {
                FastRemoveAt(index);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Removes the first element from the list that matches a predicate, moving from low to high indices. Does not preserve the order of elements.
        /// </summary>
        /// <param name="predicate">Predicate to test elements with.</param>
        /// <returns>True if an element matched and was removed, false otherwise.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool FastRemove<TPredicate>(ref TPredicate predicate) where TPredicate : IPredicate<T>
        {
            Validate();
            var index = Span.IndexOf(ref predicate, 0, Count);
            if (index >= 0)
            {
                FastRemoveAt(index);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Removes an element from the list at the given index. Preserves the order of elements.
        /// </summary>
        /// <param name="index">Index of the element to remove from the list.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void RemoveAt(int index)
        {
            Validate();
            ValidateIndex(index);
            --Count;
            if (index < Count)
            {
                //Copy everything from the removal point onward backward one slot.
                Span.CopyTo(index + 1, ref Span, index, Count - index);
            }
            //Clear out the former last slot.
            Span[Count] = default(T);
        }

        /// <summary>
        /// Removes an element from the list at the given index. Does not preserve the order of elements.
        /// </summary>
        /// <param name="index">Index of the element to remove from the list.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void FastRemoveAt(int index)
        {
            Validate();
            ValidateIndex(index);
            --Count;
            if (index < Count)
            {
                //Put the final element in the removed slot.
                Span[index] = Span[Count];
            }
            //Clear out the former last slot.
            Span[Count] = default(T);
        }

        /// <summary>
        /// Removes and outputs the last element in the list. Assumes positive count. User is responsible for guaranteeing correctness.
        /// </summary>
        /// <param name="element">Last element of the list.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Pop(out T element)
        {
            Validate();
            Debug.Assert(Count > 0, "It's up to the user to guarantee that the count is actually positive when using the unconditional pop.");
            Count--;
            element = Span[Count];
        }

        /// <summary>
        /// Removes and outputs the last element in the list if it exists.
        /// </summary>
        /// <param name="element">Last element of the list.</param>
        /// <returns>True if the element existed and was removed, false otherwise.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryPop(out T element)
        {
            Validate();
            if (Count > 0)
            {
                Count--;
                element = Span[Count];
                return true;
            }
            element = default(T);
            return false;
        }

        /// <summary>
        /// Determines whether the <see cref="T:System.Collections.Generic.ICollection`1"/> contains a specific value.
        /// </summary>
        /// <returns>
        /// true if <paramref name="element"/> is found in the <see cref="T:System.Collections.Generic.ICollection`1"/>; otherwise, false.
        /// </returns>
        /// <param name="element">The object to locate in the <see cref="T:System.Collections.Generic.ICollection`1"/>.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Contains(T element)
        {
            return IndexOf(element) >= 0;
        }

        /// <summary>
        /// Determines whether the collection contains a specific value.
        /// </summary>
        /// <returns>
        /// True if <paramref name="element"/> is found in the collection; otherwise, false.
        /// </returns>
        /// <param name="element">The object to locate in the collection.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Contains(ref T element)
        {
            return IndexOf(ref element) >= 0;
        }

        /// <summary>
        /// Determines whether the collection contains an element that matches a predicate.
        /// </summary>
        /// <returns>
        /// True if an element matching the predicate exists, otherwise false.
        /// </returns>
        /// <param name="predicate">The predicate to test against elements in the list.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Contains<TPredicate>(ref TPredicate predicate) where TPredicate : IPredicate<T>
        {
            return Span.IndexOf(ref predicate, 0, Count) >= 0;
        }


        /// <summary>
        /// Clears the list by setting the count to zero and explicitly setting all relevant indices in the backing array to default values.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Clear()
        {
            Validate();
            Span.Clear(0, Count);
            Count = 0;
        }


        public Enumerator GetEnumerator()
        {
            return new Enumerator(ref Span, Count);
        }


        public struct Enumerator : IEnumerator<T>
        {
            private readonly TSpan span;
            private readonly int count;
            private int index;

            public Enumerator(ref TSpan span, int count)
            {
                this.span = span;
                this.count = count;
                index = -1;
            }

            public T Current
            {
                get { return span[index]; }
            }

            public void Dispose()
            {
            }

            object System.Collections.IEnumerator.Current
            {
                get { return Current; }
            }

            public bool MoveNext()
            {
                return ++index < count;
            }

            public void Reset()
            {
                index = -1;
            }
        }


        [Conditional("DEBUG")]
        void ValidateIndex(int index)
        {
            Debug.Assert(index >= 0 && index < Count, "Index must be nonnegative and less than the number of elements in the list.");
        }

        [Conditional("DEBUG")]
        private void Validate()
        {
            Debug.Assert(Span.Length > 0, "Any QuickList in use should have a nonzero length Span. Was this instance default constructed without further initialization?");
        }

        [Conditional("DEBUG")]
        void ValidateUnsafeAdd()
        {
            Debug.Assert(Count < Span.Length, "Unsafe adders can only be used if the capacity is guaranteed to hold the new size.");
        }

    }
}
