using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using LiveKit.Internal;
using LiveKit.Proto;
using LiveKit.Audio;
using LiveKit.Internal.FFIClients;
using LiveKit.Internal.FFIClients.Requests;
using LiveKit.Rooms.Tracks;
using Livekit.Types;
using RichTypes;

namespace LiveKit.Rooms.Streaming.Audio
{
    public class AudioStream : IDisposable
    {
        private readonly StreamKey streamKey;
        private readonly ulong trackHandle;
        
        private AudioStreamInternal currentInternal;
        private uint currentSampleRate;
        private uint currentChannels;

        public AudioStreamInfo AudioStreamInfo => currentInternal.audioStreamInfo;

        public WavTeeControl WavTeeControl => currentInternal.WavTeeControl;

        public AudioStream(StreamKey streamKey, ITrack track)
        {
            this.streamKey = streamKey;
            trackHandle = (ulong)track.Handle!.DangerousGetHandle();
            currentSampleRate = (uint)UnityEngine.AudioSettings.outputSampleRate;
            currentChannels = 2;
            currentInternal = NewInternal(streamKey, trackHandle, currentChannels, currentSampleRate);
        }

        private static AudioStreamInternal NewInternal(StreamKey streamKey, ulong trackHandle, uint channels, uint sampleRate)
        {
            using FfiRequestWrap<NewAudioStreamRequest> request = FFIBridge.Instance.NewRequest<NewAudioStreamRequest>();
            var newStream = request.request;
            newStream.TrackHandle = trackHandle;
            newStream.Type = AudioStreamType.AudioStreamNative;
            newStream.SampleRate = sampleRate;
            newStream.NumChannels = channels;
            AudioStreamInfo audioStreamInfo = new AudioStreamInfo(streamKey, newStream.NumChannels, newStream.SampleRate);

            using FfiResponseWrap response = request.Send();
            FfiResponse res = response;

            OwnedAudioStream streamInfo = res.NewAudioStream!.Stream!;

            return new AudioStreamInternal(streamInfo, audioStreamInfo);
        }

        /// <summary>
        /// Supposed to be called from Unity's audio thread.
        /// </summary>
        public void ReadAudio(Span<float> data, int channels, int sampleRate)
        {
            if (currentChannels != channels || currentSampleRate != sampleRate)
            {
                bool wasWavActive = currentInternal.WavTeeControl.IsWavActive;
                
                currentInternal.Dispose();
                currentChannels = (uint)channels;
                currentSampleRate = (uint)sampleRate;
                currentInternal = NewInternal(streamKey, trackHandle, currentChannels, currentSampleRate);

                if (wasWavActive)
                {
                    currentInternal.WavTeeControl.StartWavTeeToDisk();
                }
            }
            
            currentInternal.ReadAudio(data, channels, sampleRate);
        }

        public void Dispose()
        {
            currentInternal.Dispose();
        }
    }
    
    public class AudioStreamInternal : IDisposable
    {
        private static readonly ResampleQueue Queue = new();

        private readonly FfiHandle handle;

        /// <summary>
        /// Keep under single lock for the use case, avoid unneeded multiple mutex locking
        /// </summary>
        private readonly Mutex<NativeAudioBufferResampleTee> buffer =
            new(
                new NativeAudioBufferResampleTee(
                    new NativeAudioBuffer(200),
                    default,
                    default
                )
            );

        private int targetChannels;
        private int targetSampleRate;

        private bool disposed;

        public readonly AudioStreamInfo audioStreamInfo;

        public WavTeeControl WavTeeControl
        {
            get
            {
                string networkFilePath = 
                    StreamKeyUtils.NewPersistentFilePathByStreamKey(audioStreamInfo.streamKey, "network");
                string resampleFilePath =
                    StreamKeyUtils.NewPersistentFilePathByStreamKey(audioStreamInfo.streamKey, "resample");
                return new WavTeeControl(buffer, beforeWavFilePath: networkFilePath, afterWavFilePath: resampleFilePath);
            }
        }

        public AudioStreamInternal(
            OwnedAudioStream ownedAudioStream,
            AudioStreamInfo audioStreamInfo
        )
        {
            this.audioStreamInfo = audioStreamInfo;

            handle = IFfiHandleFactory.Default.NewFfiHandle(ownedAudioStream.Handle!.Id);
            FfiClient.Instance.AudioStreamEventReceived += OnAudioStreamEvent;
            Queue.Register(this);
        }

        /// <summary>
        /// Supposed to be disposed ONLY by AudioStreams
        /// </summary>
        public void Dispose()
        {
            if (disposed)
                return;

            disposed = true;

            handle.Dispose();
            using (var guard = buffer.Lock())
            {
                guard.Value.Dispose();
            }

            FfiClient.Instance.AudioStreamEventReceived -= OnAudioStreamEvent;
            Queue.UnRegister(this);
        }

        /// <summary>
        /// Supposed to be called from Unity's audio thread.
        /// </summary>
        public void ReadAudio(Span<float> data, int channels, int sampleRate)
        {
            targetChannels = channels;
            targetSampleRate = sampleRate;

            if (disposed)
                return;

            data.Fill(0);

            int samplesPerChannel = data.Length / channels;

            {
                Option<AudioFrame> frameOption;
                using (var guard = buffer.Lock())
                {
                    frameOption = guard.Value.Read(
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
                    data[i] = span[i].ToFloat();
                }
            }
        }

        private void OnAudioStreamEvent(AudioStreamEvent e)
        {
            if (e.StreamHandle != (ulong)handle.DangerousGetHandle())
                return;

            if (e.MessageCase != AudioStreamEvent.MessageOneofCase.FrameReceived)
                return;


            Queue.Enqueue(this, e.FrameReceived.Frame);

            // TODO
            // SIMD integration
            // MOVE UNITY sampling to buffer already, don't do it on audio thread
        }

        private class ResampleQueue
        {
            private readonly BlockingCollection<(AudioStreamInternal author, OwnedAudioFrameBuffer buffer)> bufferQueue = new();
            private readonly AudioResampler audioResampler = AudioResampler.New();
            private readonly HashSet<AudioStreamInternal> registeredStreams = new();

            private CancellationTokenSource? cancellationTokenSource;

            public void Register(AudioStreamInternal audioStream)
            {
                lock (registeredStreams)
                {
                    registeredStreams.Add(audioStream);
                    if (cancellationTokenSource == null)
                    {
                        StartThread();
                    }
                }
            }

            public void UnRegister(AudioStreamInternal audioStream)
            {
                lock (registeredStreams)
                {
                    registeredStreams.Remove(audioStream);
                    if (registeredStreams.Count == 0)
                    {
                        cancellationTokenSource?.Cancel();
                        cancellationTokenSource = null;
                    }
                }
            }

            public void Enqueue(AudioStreamInternal stream, OwnedAudioFrameBuffer buffer)
            {
                bufferQueue.Add((stream, buffer));
            }

            private void ProcessCandidate(AudioStreamInternal stream, OwnedAudioFrameBuffer buffer)
            {
                // We need to pass the exact 10ms chunks, otherwise - crash
                // Example
                // #                                                                                             
                // # Fatal error in: ../common_audio/resampler/push_sinc_resampler.cc, line 52                   
                // # last system error: 1                                                                        
                // # Check failed: source_length == resampler_->request_frames() (1104 vs. 480)                  
                // #   
                using var rawFrame = new OwnedAudioFrame(buffer);

                if (stream.targetChannels == 0 || stream.targetSampleRate == 0) return;
                using var frame = audioResampler.RemixAndResample(rawFrame, (uint)stream.targetChannels,
                    (uint)stream.targetSampleRate);
                using var guard = stream.buffer.Lock();
                guard.Value.Write(frame);
                guard.Value.TryWriteWavTee(rawFrame, frame);
            }

            private void StartThread()
            {
                var token = cancellationTokenSource = new CancellationTokenSource();
                new Thread(() =>
                    {
                        try
                        {
                            foreach (var (author, ownedAudioFrameBuffer)
                                     in bufferQueue.GetConsumingEnumerable(token.Token)!)
                                ProcessCandidate(author, ownedAudioFrameBuffer);
                        }
                        catch (OperationCanceledException)
                        {
                            // Expected
                        }
                        catch (Exception e)
                        {
                            Utils.Error($"Caught an unexpected exception in ResampleQueue worker thread: {e.Message}");
                        }
                    }
                ).Start();
            }
        }
    }

    public readonly struct AudioStreamInfo
    {
        public readonly StreamKey streamKey;
        public readonly uint numChannels;
        public readonly uint sampleRate;

        public AudioStreamInfo(StreamKey streamKey, uint numChannels, uint sampleRate)
        {
            this.streamKey = streamKey;
            this.numChannels = numChannels;
            this.sampleRate = sampleRate;
        }
    }
}