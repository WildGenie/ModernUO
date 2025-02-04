// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Server.Buffers;

namespace Server.Collections;

// A simple Queue of generic objects.  Internally it is implemented as a
// circular buffer, so Enqueue can be O(n).  Dequeue is O(1).
[DebuggerDisplay("Count = {Count}")]
public ref struct PooledRefQueue<T>
{
    private T[] _array;
    private int _head; // The index from which to dequeue if the queue isn't empty.
    private int _tail; // The index at which to enqueue if the queue isn't full.
    private int _size; // Number of elements.
    private bool _mt;
    private int _version;

#pragma warning disable CA1825 // avoid the extra generic instantiation for Array.Empty<T>()
    private static readonly T[] s_emptyArray = new T[0];
#pragma warning restore CA1825

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static PooledRefQueue<T> Create(int capacity = 32, bool mt = false) => new(capacity, mt);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static PooledRefQueue<T> CreateMT(int capacity = 32) => new(capacity, true);

    // Creates a queue with room for capacity objects. The default grow factor
    // is used.
    public PooledRefQueue(int capacity, bool mt = false)
    {
        _mt = mt;
        _array = capacity switch
        {
            < 0 => throw new ArgumentOutOfRangeException(nameof(capacity), capacity, CollectionThrowStrings.ArgumentOutOfRange_NeedNonNegNum),
            0   => s_emptyArray,
            _   => (mt ? ArrayPool<T>.Shared : STArrayPool<T>.Shared).Rent(capacity)
        };

        _head = 0;
        _tail = 0;
        _size = 0;
        _version = 0;
    }

    public int Count => _size;

    // Removes all Objects from the queue.
    public void Clear()
    {
        if (_size != 0)
        {
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            {
                if (_head < _tail)
                {
                    Array.Clear(_array, _head, _size);
                }
                else
                {
                    Array.Clear(_array, _head, _array.Length - _head);
                    Array.Clear(_array, 0, _tail);
                }
            }

            _size = 0;
        }

        _head = 0;
        _tail = 0;
        _version++;
    }

    // CopyTo copies a collection into an Array, starting at a particular
    // index into the array.
    public void CopyTo(T[] array, int arrayIndex)
    {
        if (array == null)
        {
            throw new ArgumentNullException(nameof(array));
        }

        if (arrayIndex < 0 || arrayIndex > array.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(arrayIndex), arrayIndex, CollectionThrowStrings.ArgumentOutOfRange_Index);
        }

        if (array.Length - arrayIndex < _size)
        {
            throw new ArgumentException(CollectionThrowStrings.Argument_InvalidOffLen);
        }

        int numToCopy = _size;
        if (numToCopy == 0)
        {
            return;
        }

        int firstPart = Math.Min(_array.Length - _head, numToCopy);
        Array.Copy(_array, _head, array, arrayIndex, firstPart);
        numToCopy -= firstPart;
        if (numToCopy > 0)
        {
            Array.Copy(_array, 0, array, arrayIndex + _array.Length - _head, numToCopy);
        }
    }

    // Adds item to the tail of the queue.
    public void Enqueue(T item)
    {
        if (_size == _array.Length)
        {
            Grow(_size + 1);
        }

        _array[_tail] = item;
        MoveNext(ref _tail);
        _size++;
        _version++;
    }

    // GetEnumerator returns an IEnumerator over this Queue.  This
    // Enumerator will support removing.
    public Enumerator GetEnumerator() => new(this);

    // Removes the object at the head of the queue and returns it. If the queue
    // is empty, this method throws an
    // InvalidOperationException.
    public T Dequeue()
    {
        int head = _head;
        T[] array = _array;

        if (_size == 0)
        {
            ThrowForEmptyQueue();
        }

        T removed = array[head];
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            array[head] = default!;
        }
        MoveNext(ref _head);
        _size--;
        _version++;
        return removed;
    }

    public bool TryDequeue([MaybeNullWhen(false)] out T result)
    {
        int head = _head;
        T[] array = _array;

        if (_size == 0)
        {
            result = default!;
            return false;
        }

        result = array[head];
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            array[head] = default!;
        }
        MoveNext(ref _head);
        _size--;
        _version++;
        return true;
    }

    // Returns the object at the head of the queue. The object remains in the
    // queue. If the queue is empty, this method throws an
    // InvalidOperationException.
    public T Peek()
    {
        if (_size == 0)
        {
            ThrowForEmptyQueue();
        }

        return _array[_head];
    }

    public T PeekRandom()
    {
        if (_size == 0)
        {
            ThrowForEmptyQueue();
        }

        var index = _head + Utility.Random(_size);
        if (index >= _array.Length)
        {
            index -= _array.Length;
        }

        return _array[index];
    }

    public bool TryPeek([MaybeNullWhen(false)] out T result)
    {
        if (_size == 0)
        {
            result = default!;
            return false;
        }

        result = _array[_head];
        return true;
    }

    // Returns true if the queue contains at least one object equal to item.
    // Equality is determined using EqualityComparer<T>.Default.Equals().
    public bool Contains(T item)
    {
        if (_size == 0)
        {
            return false;
        }

        if (_head < _tail)
        {
            return Array.IndexOf(_array, item, _head, _size) >= 0;
        }

        // We've wrapped around. Check both partitions, the least recently enqueued first.
        return
            Array.IndexOf(_array, item, _head, _array.Length - _head) >= 0 ||
            Array.IndexOf(_array, item, 0, _tail) >= 0;
    }

    // Iterates over the objects in the queue, returning an array of the
    // objects in the Queue, or an empty array if the queue is empty.
    // The order of elements in the array is first in to last in, the same
    // order produced by successive calls to Dequeue.
    public T[] ToArray()
    {
        if (_size == 0)
        {
            return s_emptyArray;
        }

        T[] arr = new T[_size];

        if (_head < _tail)
        {
            Array.Copy(_array, _head, arr, 0, _size);
        }
        else
        {
            Array.Copy(_array, _head, arr, 0, _array.Length - _head);
            Array.Copy(_array, 0, arr, _array.Length - _head, _tail);
        }

        return arr;
    }

    public T[] ToPooledArray(bool mt = false)
    {
        if (_size == 0)
        {
            return s_emptyArray;
        }

        T[] arr = (mt ? ArrayPool<T>.Shared : STArrayPool<T>.Shared).Rent(_size);

        if (_head < _tail)
        {
            Array.Copy(_array, _head, arr, 0, _size);
        }
        else
        {
            Array.Copy(_array, _head, arr, 0, _array.Length - _head);
            Array.Copy(_array, 0, arr, _array.Length - _head, _tail);
        }

        return arr;
    }

    // PRIVATE Grows or shrinks the buffer to hold capacity objects. Capacity
    // must be >= _size.
    private void SetCapacity(int capacity)
    {
        T[] newarray = (_mt ? ArrayPool<T>.Shared : STArrayPool<T>.Shared).Rent(capacity);
        if (_size > 0)
        {
            if (_head < _tail)
            {
                Array.Copy(_array, _head, newarray, 0, _size);
            }
            else
            {
                Array.Copy(_array, _head, newarray, 0, _array.Length - _head);
                Array.Copy(_array, 0, newarray, _array.Length - _head, _tail);
            }
        }

        if (_array.Length > 0)
        {
            Clear();
            (_mt ? ArrayPool<T>.Shared : STArrayPool<T>.Shared).Return(_array);
        }

        _array = newarray;
        _head = 0;
        _tail = _size == capacity ? 0 : _size;
        _version++;
    }

    // Increments the index wrapping it if necessary.
#if NET7_SDK
    private void MoveNext(scoped ref int index)
#else
    private void MoveNext(ref int index)
#endif
    {
        // It is tempting to use the remainder operator here but it is actually much slower
        // than a simple comparison and a rarely taken branch.
        // JIT produces better code than with ternary operator ?:
        int tmp = index + 1;
        if (tmp == _array.Length)
        {
            tmp = 0;
        }
        index = tmp;
    }

    private void ThrowForEmptyQueue()
    {
        Debug.Assert(_size == 0);
        throw new InvalidOperationException(CollectionThrowStrings.InvalidOperation_EmptyQueue);
    }

    /// <summary>
    /// Ensures that the capacity of this Queue is at least the specified <paramref name="capacity"/>.
    /// </summary>
    /// <param name="capacity">The minimum capacity to ensure.</param>
    public int EnsureCapacity(int capacity)
    {
        if (capacity < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, CollectionThrowStrings.ArgumentOutOfRange_NeedNonNegNum);
        }

        if (_array.Length < capacity)
        {
            Grow(capacity);
        }

        return _array.Length;
    }

    private void Grow(int capacity)
    {
        const int GrowFactor = 2;
        const int MinimumGrow = 4;

        int newcapacity = GrowFactor * _array.Length;

        // Allow the list to grow to maximum possible capacity (~2G elements) before encountering overflow.
        // Note that this check works even when _items.Length overflowed thanks to the (uint) cast
        if ((uint)newcapacity > int.MaxValue)
        {
            newcapacity = int.MaxValue;
        }

        // Ensure minimum growth is respected.
        newcapacity = Math.Max(newcapacity, _array.Length + MinimumGrow);

        // If the computed capacity is still less than specified, set to the original argument.
        // Capacities exceeding Array.MaxLength will be surfaced as OutOfMemoryException by Array.Resize.
        if (newcapacity < capacity)
        {
            newcapacity = capacity;
        }

        SetCapacity(newcapacity);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Dispose()
    {
        var array = _array;
        if (array.Length > 0)
        {
            Clear();
            (_mt ? ArrayPool<T>.Shared : STArrayPool<T>.Shared).Return(array);
        }

        this = default;
    }

    // Implements an enumerator for a Queue.  The enumerator uses the
    // internal version number of the list to ensure that no modifications are
    // made to the list while an enumeration is in progress.
    public ref struct Enumerator
    {
        private readonly PooledRefQueue<T> _q;
        private readonly int _version;
        private int _index; // -1 = not started, -2 = ended/disposed
        private T? _currentElement;

        internal Enumerator(PooledRefQueue<T> q)
        {
            _q = q;
            _version = q._version;
            _index = -1;
            _currentElement = default;
        }

        public void Dispose()
        {
            _index = -2;
            _currentElement = default;
        }

        public bool MoveNext()
        {
            if (_version != _q._version)
            {
                throw new InvalidOperationException(CollectionThrowStrings.InvalidOperation_EnumFailedVersion);
            }

            if (_index == -2)
            {
                return false;
            }

            _index++;

            if (_index == _q._size)
            {
                // We've run past the last element
                _index = -2;
                _currentElement = default;
                return false;
            }

            // Cache some fields in locals to decrease code size
            T[] array = _q._array;
            int capacity = array.Length;

            // _index represents the 0-based index into the queue, however the queue
            // doesn't have to start from 0 and it may not even be stored contiguously in memory.

            int arrayIndex = _q._head + _index; // this is the actual index into the queue's backing array
            if (arrayIndex >= capacity)
            {
                // NOTE: Originally we were using the modulo operator here, however
                // on Intel processors it has a very high instruction latency which
                // was slowing down the loop quite a bit.
                // Replacing it with simple comparison/subtraction operations sped up
                // the average foreach loop by 2x.

                arrayIndex -= capacity; // wrap around if needed
            }

            _currentElement = array[arrayIndex];
            return true;
        }

        public T Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                if (_index < 0)
                {
                    ThrowEnumerationNotStartedOrEnded();
                }

                return _currentElement!;
            }
        }

        private void ThrowEnumerationNotStartedOrEnded()
        {
            Debug.Assert(_index is -1 or -2);
            throw new InvalidOperationException(_index == -1 ? CollectionThrowStrings.InvalidOperation_EnumNotStarted : CollectionThrowStrings.InvalidOperation_EnumEnded);
        }

        public void Reset()
        {
            if (_version != _q._version)
            {
                throw new InvalidOperationException(CollectionThrowStrings.InvalidOperation_EnumFailedVersion);
            }

            _index = -1;
            _currentElement = default;
        }
    }
}
