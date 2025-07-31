using System;
using LiveKit.Internal;
using LiveKit.Proto;
using LiveKit.Audio;
using RichTypes;
using UnityEngine;

namespace LiveKit.Rooms.Streaming.Audio
{
    public class AudioStream : IAudioStream
    {
        private readonly IAudioStreams audioStreams;
        private readonly FfiHandle handle;
        private readonly AudioResampler audioResampler = AudioResampler.New();

        private readonly AudioBuffer buffer = new(50);
        private short[] tempBuffer;

        private bool disposed;

        private long previousTimeStamp;

        public AudioStream(
            IAudioStreams audioStreams,
            OwnedAudioStream ownedAudioStream,
            IAudioRemixConveyor audioRemixConveyor
        )
        {
            this.audioStreams = audioStreams;
            handle = IFfiHandleFactory.Default.NewFfiHandle(ownedAudioStream.Handle!.Id);
            FfiClient.Instance.AudioStreamEventReceived += OnAudioStreamEvent;
        }

        public void Dispose()
        {
            if (disposed)
                return;

            disposed = true;

            handle.Dispose();
            buffer.Dispose();
            audioResampler.Dispose();

            FfiClient.Instance.AudioStreamEventReceived -= OnAudioStreamEvent;
            audioStreams.Release(this);
        }

        public void ReadAudio(Span<float> data, int channels, int sampleRate)
        {
            long timestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Debug.Log($"Timestamp diff: {timestampMs - previousTimeStamp} ms");
            previousTimeStamp = timestampMs;

            if (disposed)
                return;

            const int TO_MILLISECONDS = 1000;
            // We need to pass the exact 10ms chunks, otherwise - crash
            // Example
            // #                                                                                             
            // # Fatal error in: ../common_audio/resampler/push_sinc_resampler.cc, line 52                   
            // # last system error: 1                                                                        
            // # Check failed: source_length == resampler_->request_frames() (1104 vs. 480)                  
            // #   
            const int LIVEKIT_ACCEPTED_DURATION_MS = 10;

            int samplesPerChannel = data.Length / channels;
            int requiredDuration = (samplesPerChannel * TO_MILLISECONDS) / sampleRate;

            int remainingDuration = requiredDuration;

            int dataIndex = 0;

            while (remainingDuration > 0)
            {
                Option<AudioFrame> frameOption = buffer.ReadDuration(LIVEKIT_ACCEPTED_DURATION_MS);
                remainingDuration -= LIVEKIT_ACCEPTED_DURATION_MS;
                if (frameOption.Has == false)
                {
                    Utils.Debug("No more frames to process, fill the rest with silence");
                    for (; dataIndex < data.Length; dataIndex++) data[dataIndex] = 0;
                    buffer.ReadDuration((uint)remainingDuration);
                    return;
                }

                using AudioFrame rawFrame = frameOption.Value;
                using var frame = audioResampler.RemixAndResample(rawFrame, (uint)channels, (uint)sampleRate);
                Span<PCMSample> span = frame.AsPCMSampleSpan();

                for (int i = 0; i < span.Length; i++)
                {
                    data[dataIndex] = S16ToFloat(span[i].data);
                    dataIndex++;
                    if (dataIndex >= data.Length) return;
                }

                static float S16ToFloat(short v)
                {
                    return v / 32768f;
                }
            }
        }

        private void OnAudioStreamEvent(AudioStreamEvent e)
        {
            if (e.StreamHandle != (ulong)handle.DangerousGetHandle())
                return;

            if (e.MessageCase != AudioStreamEvent.MessageOneofCase.FrameReceived)
                return;

            using var frame = new OwnedAudioFrame(e.FrameReceived.Frame);
            buffer.Write(frame.AsPCMSampleSpan(), frame.NumChannels, frame.SampleRate);
        }
    }
}