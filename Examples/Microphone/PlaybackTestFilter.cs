using System;
using LiveKit.Audio;
using LiveKit.Internal;
using LiveKit.Scripts.Audio;
using Livekit.Types;
using UnityEngine;

namespace Livekit.Examples.Microphone
{
    public class PlaybackTestFilter : MonoBehaviour
    {
        private readonly Mutex<NativeAudioBuffer> buffer =
            new(new NativeAudioBuffer(bufferDurationMs: 200));

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

            using var remix = audioResampler.RemixAndResample(frame, targetChannels, outputSampleRate);
            guard.Value.Write(remix);
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