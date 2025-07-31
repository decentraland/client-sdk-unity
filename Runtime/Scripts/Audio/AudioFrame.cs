using System;
using LiveKit.Proto;
using LiveKit.Internal;
using LiveKit.Internal.FFIClients.Requests;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using System.Runtime.InteropServices;

namespace LiveKit.Audio
{
    public interface IAudioFrame
    {
        public uint NumChannels { get; }
        public uint SampleRate { get; }
        public uint SamplesPerChannel { get; }
        public IntPtr Data { get; }

        public bool Disposed { get; }
    }


    public static class AudioFrameExtensions
    {
        public static Span<byte> AsSpan<TAudioFrame>(this TAudioFrame frame) where TAudioFrame : IAudioFrame
        {
            if (frame.Disposed)
            {
                Utils.Error("Attempted to access disposed AudioFrame");
                return Span<byte>.Empty;
            }

            unsafe
            {
                return new Span<byte>(frame.Data.ToPointer(), frame.Length());
            }
        }

        public static Span<PCMSample> AsPCMSampleSpan<TAudioFrame>(this TAudioFrame frame) where TAudioFrame : IAudioFrame
        {
            return MemoryMarshal.Cast<byte, PCMSample>(frame.AsSpan());
        }

        public static int Length<TAudioFrame>(this TAudioFrame frame) where TAudioFrame : IAudioFrame
        {
            return (int) (frame.SamplesPerChannel * frame.NumChannels * sizeof(short));
        }
    }


    public struct AudioFrame : IAudioFrame, IDisposable
    {
        public uint NumChannels { get; }
        public uint SampleRate { get; }
        public uint SamplesPerChannel { get; }

        private readonly NativeArray<byte> _data;
        private readonly IntPtr _dataPtr;
        private bool _disposed;

        public IntPtr Data => _dataPtr;
        public int Length => this.Length();
        public bool IsValid => _data.IsCreated && !_disposed;
        public bool Disposed => _disposed;

        internal AudioFrame(uint sampleRate, uint numChannels, uint samplesPerChannel)
        {
            SampleRate = sampleRate;
            NumChannels = numChannels;
            SamplesPerChannel = samplesPerChannel;
            _disposed = false;

            unsafe
            {
                _data = new NativeArray<byte>((int) (samplesPerChannel * numChannels * sizeof(short)), Allocator.Persistent);
                _dataPtr = (IntPtr) NativeArrayUnsafeUtility.GetUnsafePtr(_data);
            }
        }

        public void Dispose()
        {
            if (!_disposed && _data.IsCreated)
            {
                _data.Dispose();
                _disposed = true;
            }
        }
    }
}