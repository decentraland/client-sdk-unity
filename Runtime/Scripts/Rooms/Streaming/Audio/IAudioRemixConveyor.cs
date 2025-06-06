﻿using System;
using LiveKit.Internal;
using Livekit.Utils;

namespace LiveKit.Rooms.Streaming.Audio
{
    public interface IAudioRemixConveyor : IDisposable
    {
        void Process(OwnedAudioFrame ownedAudioFrame, Mutex<RingBuffer> outputBuffer, uint numChannels,
            uint sampleRate);

        class SameThreadAudioRemixConveyor : IAudioRemixConveyor
        {
            private readonly AudioResampler.ThreadSafe resampler = new();

            public void Process(
                OwnedAudioFrame ownedAudioFrame,
                Mutex<RingBuffer> outputBuffer,
                uint numChannels,
                uint sampleRate
            )
            {
                using var uFrame = resampler.RemixAndResample(ownedAudioFrame, numChannels, sampleRate);
                var data = uFrame.AsSpan();
                using var guard = outputBuffer.Lock();
                guard.Value.Write(data);
            }

            public void Dispose()
            {
                resampler.Dispose();
            }
        }
    }
}