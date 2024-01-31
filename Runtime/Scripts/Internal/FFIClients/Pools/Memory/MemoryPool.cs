using System;
using System.Collections.Generic;

namespace LiveKit.Internal.FFIClients.Pools.Memory
{
    [Obsolete("Use ArrayMemoryPool instead", true)]
    public class MemoryPool : IMemoryPool
    {
        private readonly Dictionary<int, Queue<byte[]>> buffers = new();

        public MemoryWrap Memory(int byteSize)
        {
            return AvailableBuffers(byteSize).TryDequeue(out var buffer)
                ? new MemoryWrap(buffer!, byteSize, this)
                : new MemoryWrap(new byte[ToPowerOfTwo(byteSize)], byteSize, this);
        }

        public void Release(byte[] buffer)
        {
            var availableBuffers = AvailableBuffers(buffer.Length);
            availableBuffers.Enqueue(buffer);
        }

        private Queue<byte[]> AvailableBuffers(int size)
        {
            if (size == 0)
            {
                throw new ArgumentException("Buffer size cannot be 0");
            }

            var power = ToPowerOfTwo(size);
            if (buffers.TryGetValue(power, out var list) == false)
            {
                list = new Queue<byte[]>();
                buffers.Add(power, list);
            }

            return list!;
        }

        private static int ToPowerOfTwo(int size)
        {
            var power = 1;
            while (power < size)
            {
                power *= 2;
            }

            return power;
        }
    }
}