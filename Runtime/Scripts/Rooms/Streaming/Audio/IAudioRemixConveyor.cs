using System;
using LiveKit.Internal;
using Livekit.Utils;
using UnityEngine;

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
                // Optimization: Skip expensive resampling if formats already match
                if (ownedAudioFrame.numChannels == numChannels && ownedAudioFrame.sampleRate == sampleRate)
                {
                    // Direct copy - no resampling needed
                    Utils.Error($"AudioRemixConveyor: Direct copy {ownedAudioFrame.numChannels}ch@{ownedAudioFrame.sampleRate}Hz -> {numChannels}ch@{sampleRate}Hz");
                    var data = ownedAudioFrame.AsSpan();
                    using var guard = outputBuffer.Lock();
                    guard.Value.Write(data);
                    
                    // Frame will be disposed when method exits
                }
                else
                {
                    // Resampling required - use FFI resampler
                    Utils.Error($"AudioRemixConveyor: Resampling {ownedAudioFrame.numChannels}ch@{ownedAudioFrame.sampleRate}Hz -> {numChannels}ch@{sampleRate}Hz");
                    using var uFrame = resampler.RemixAndResample(ownedAudioFrame, numChannels, sampleRate);
                    var data = uFrame.AsSpan();
                    using var guard = outputBuffer.Lock();
                    guard.Value.Write(data);
                }
            }

            public void Dispose()
            {
                resampler.Dispose();
            }
        }
    }
}