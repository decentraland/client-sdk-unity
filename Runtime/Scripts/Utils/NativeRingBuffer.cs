using System;
using System.Threading;
using RichTypes;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace LiveKit.Utils
{
    public struct NativeRingBuffer<T> : IDisposable where T : unmanaged
    {
        private readonly IntPtr ptr;
        private readonly LongPowerOf2 capacity;
        private long head;
        private long tail;

        public readonly bool IsEmpty => head == tail;
        public readonly bool IsFull => head - tail == capacity.value;
        public readonly long Count => head - tail;

        public NativeRingBuffer(LongPowerOf2 capacity)
        {
            this.capacity = capacity;
            head = 0;
            tail = 0;
            unsafe
            {
                void* nativePtr = UnsafeUtility.Malloc(
                    capacity.value * sizeof(T),
                    UnsafeUtility.AlignOf<T>(),
                    Allocator.Persistent
                )!;
                ptr = new IntPtr(nativePtr);
            }
        }

        public void Dispose()
        {
            unsafe
            {
                UnsafeUtility.Free(ptr.ToPointer()!, Allocator.Persistent);
            }
        }

        public bool TryEnqueue(T item)
        {
            if (IsFull)
                return false;

            unsafe
            {
                T* buffer = (T*)ptr.ToPointer();
                buffer += head & (capacity.value - 1);
                *buffer = item;
                head++;
            }

            return true;
        }

        public bool TryDequeue(out T item)
        {
            if (IsFull)
            {
                item = default!;
                return false;
            }

            unsafe
            {
                T* buffer = (T*)ptr.ToPointer();
                buffer += tail & (capacity.value - 1);
                item = *buffer;
                tail++;
            }

            return true;
        }

    }

    public readonly struct LongPowerOf2
    {
        public readonly long value;

        private LongPowerOf2(long value)
        {
            this.value = value;
        }

        public static Result<LongPowerOf2> New(long value)
        {
            if (value <= 0 || (value & (value - 1)) != 0)
                return Result<LongPowerOf2>.ErrorResult("Capacity must be power of 2.");
            return Result<LongPowerOf2>.SuccessResult(new LongPowerOf2(value));
        }
    }
}