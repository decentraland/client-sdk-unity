using System;
using LiveKit.Audio;
using LiveKit.Internal;
using LiveKit.Scripts.Audio;
using Livekit.Types;
using RichTypes;
using UnityEngine;

namespace Livekit.Examples.Microphone
{
    public class PlaybackTestFilter : MonoBehaviour
    {
        private readonly Mutex<NativeAudioBuffer> buffer =
            new(new NativeAudioBuffer(bufferDurationMs: 200));
        private readonly Mutex<NativeAudioBuffer> microphoneBuffer = new(new NativeAudioBuffer(30));

        private readonly AudioResampler audioResampler = AudioResampler.New();
        private MicrophoneAudioFilter? microphoneAudioFilter;

        private uint outputSampleRate;
        private uint targetChannels;

        public void Construct(MicrophoneAudioFilter newMicrophoneAudioFilter)
        {
            microphoneAudioFilter = newMicrophoneAudioFilter;
            microphoneAudioFilter.AudioRead += MicrophoneAudioFilterOnAudioRead;
            outputSampleRate = (uint)AudioSettings.outputSampleRate;
        }

        void MicrophoneAudioFilterOnAudioRead(Span<float> data, int channels, int sampleRate)
        {
            using var guard = buffer.Lock();
            using var frame = new AudioFrame((uint)sampleRate, (uint)channels, (uint)(data.Length / channels));
            var span = frame.AsPCMSampleSpan();

            for (int i = 0; i < data.Length; i++)
            {
                span[i] = PCMSample.FromUnitySample(data[i]);
            }

            using var microphoneBufferGuard = microphoneBuffer.Lock();
            microphoneBufferGuard.Value.Write(span, (uint)channels, (uint)sampleRate);

            while (true)
            {
                uint sample10MS = (uint)(sampleRate * 10 / 1000);
                Option<AudioFrame> bufferedFrame =
                    microphoneBufferGuard.Value.Read((uint)sampleRate, (uint)channels, sample10MS);
                if (bufferedFrame.Has)
                {
                    using var b = bufferedFrame.Value;
                    using var remix = audioResampler.RemixAndResample(b, targetChannels, outputSampleRate);
                    guard.Value.Write(remix);
                }
                else
                {
                    break;
                }
            }
        }

        private void OnAudioFilterRead(float[] data, int channels)
        {
            targetChannels = (uint)channels;

            int samplesPerChannel = data.Length / channels;
            using var guard = buffer.Lock();
            var frame = guard.Value.Read(outputSampleRate, (uint)channels, (uint)samplesPerChannel);
            if (frame.Has)
            {
                using var f = frame.Value;
                var span = f.AsPCMSampleSpan();
                for (int i = 0; i < span.Length; i++)
                {
                    data[i] = span[i].ToFloat();
                }
            }
        }

        private void OnDestroy()
        {
            if (microphoneAudioFilter != null)
            {
                microphoneAudioFilter.AudioRead -= MicrophoneAudioFilterOnAudioRead;
                microphoneAudioFilter.Dispose();
            }

            audioResampler.Dispose();

            using var guard = buffer.Lock();
            guard.Value.Dispose();
        }
    }
}