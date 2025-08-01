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

        private readonly CapacitiveTestAudioBuffer buffer = new(); //new(40);

        private bool disposed;

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
            Debug.Log($"Read Timestamp: {timestampMs} ms");

            if (disposed)
                return;

            data.Fill(0);

            int samplesPerChannel = data.Length / channels;

            {
                Option<AudioFrame> frameOption;
                lock (buffer)
                {
                    frameOption = buffer.Read(
                        (uint)sampleRate,
                        (uint)channels,
                        (uint)samplesPerChannel
                    );
                }

                if (frameOption.Has == false)
                {
                    return;
                }

                using AudioFrame frame = frameOption.Value;
                Span<PCMSample> span = frame.AsPCMSampleSpan();

                for (int i = 0; i < span.Length; i++)
                {
                    data[i] = S16ToFloat(span[i].data);
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

            // We need to pass the exact 10ms chunks, otherwise - crash
            // Example
            // #                                                                                             
            // # Fatal error in: ../common_audio/resampler/push_sinc_resampler.cc, line 52                   
            // # last system error: 1                                                                        
            // # Check failed: source_length == resampler_->request_frames() (1104 vs. 480)                  
            // #   

            using var frame = new OwnedAudioFrame(e.FrameReceived.Frame);
            // TODO test
            //using var rawFrame = new OwnedAudioFrame(e.FrameReceived.Frame);
            // if (rawFrame.SamplesPerChannel != 480)
            // {
            //     Debug.LogError($"Sample rate doesn't match to 480: {rawFrame.SampleRate}");
            //     return;
            // }
            //
            // if (currentChannels == 0 || currentSampleRate == 0)
            //     return;
            //
            // long timestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            // Debug.Log($"Event Timestamp: {timestampMs} ms");
            // using var frame = audioResampler.RemixAndResample(rawFrame, (uint)currentChannels, (uint)currentSampleRate);
            lock (buffer)
            {
                buffer.Write(frame.AsPCMSampleSpan(), frame.NumChannels, frame.SampleRate);
            }
        }
    }
}