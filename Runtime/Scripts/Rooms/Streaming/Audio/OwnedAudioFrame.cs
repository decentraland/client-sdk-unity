using System;
using LiveKit.Audio;
using LiveKit.Internal;
using LiveKit.Proto;

namespace LiveKit.Rooms.Streaming.Audio
{
    public readonly struct OwnedAudioFrame : IAudioFrame, IDisposable
    {
        private readonly AudioFrameBufferInfo info;
        private readonly FfiHandle handle;

        public uint NumChannels { get; }
        public uint SampleRate { get; }
        public uint SamplesPerChannel { get; }
        public IntPtr Data { get; }

        public int Length => (int) (SamplesPerChannel * NumChannels * sizeof(short));

        public OwnedAudioFrame(OwnedAudioFrameBuffer ownedAudioFrameBuffer)
        {
            handle = IFfiHandleFactory.Default.NewFfiHandle(ownedAudioFrameBuffer.Handle.Id);
            info = ownedAudioFrameBuffer.Info;
            SampleRate = info.SampleRate;
            NumChannels = info.NumChannels;
            SamplesPerChannel = info.SamplesPerChannel;
            Data = (IntPtr) info.DataPtr;
        }

        public void Dispose()
        {
            handle.Dispose();
        }

        public Span<byte> AsSpan()
        {
            unsafe
            {
                return new Span<byte>(Data.ToPointer(), Length);
            }
        }
    }
}