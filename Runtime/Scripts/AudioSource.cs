using System;
using UnityEngine;
using LiveKit.Proto;
using LiveKit.Internal;
using LiveKit.Internal.FFIClients.Requests;
using Livekit.Utils;
using System.Runtime.InteropServices;

namespace LiveKit
{
    public class RtcAudioSource
    {
        private const int DEFAULT_NUM_CHANNELS = 2;
        private const int DEFAULT_SAMPLE_RATE = 48000;
        private const float BUFFER_DURATION_S = 0.2f;
        private const float S16_MAX_VALUE = 32767f;
        private const float S16_MIN_VALUE = -32768f;
        private const float S16_SCALE_FACTOR = 32768f;

        private AudioSource audioSource;
        private AudioFilter audioFilter;
        private readonly Mutex<RingBuffer> buffer;
        private short[] tempBuffer;
        private AudioFrame frame;
        private uint channels;
        private uint sampleRate;
        private int currentBufferSize;
        private readonly object lockObject = new ();

        internal FfiHandle handle { get; }

        public RtcAudioSource(AudioSource audioSource, AudioFilter audioFilter)
        {
            buffer = new Mutex<RingBuffer>(new RingBuffer(0));
            currentBufferSize = 0;
            using var request = FFIBridge.Instance.NewRequest<NewAudioSourceRequest>();
            var newAudioSource = request.request;
            newAudioSource.Type = AudioSourceType.AudioSourceNative;
            newAudioSource.NumChannels = DEFAULT_NUM_CHANNELS;
            newAudioSource.SampleRate = DEFAULT_SAMPLE_RATE;

            using var response = request.Send();
            FfiResponse res = response;
            handle = IFfiHandleFactory.Default.NewFfiHandle(res.NewAudioSource.Source.Handle!.Id);
            this.audioSource = audioSource;
            this.audioFilter = audioFilter;
        }

        public void Start()
        {
            Stop();
            if (!audioFilter || !audioSource) 
            {
                Utils.Error("AudioFilter or AudioSource is null - cannot start audio capture");
                return;
            }

            audioFilter.AudioRead += OnAudioRead;
            audioSource.Play();
        }

        public void Stop()
        {
            if (audioFilter) audioFilter.AudioRead -= OnAudioRead;
            if (audioSource) audioSource.Stop();

            using var guard = buffer.Lock();
            guard.Value.Dispose();
            frame?.Dispose();
        }

        private void OnAudioRead(float[] data, int channels, int sampleRate)
        {
            lock (lockObject)
            {
                int newBufferSize = (int)(channels * sampleRate * BUFFER_DURATION_S) * sizeof(short);
                bool needsNewBuffer = newBufferSize != currentBufferSize;
                bool needsNewTempBuffer = channels != this.channels || sampleRate != this.sampleRate || data.Length != tempBuffer?.Length;

                if (needsNewBuffer || needsNewTempBuffer)
                {
                    if (needsNewBuffer)
                    {
                        using var guard = buffer.Lock();
                        guard.Value.Dispose();
                        guard.Value = new RingBuffer(newBufferSize);
                        currentBufferSize = newBufferSize;
                    }

                    if (needsNewTempBuffer)
                    {
                        tempBuffer = new short[data.Length];
                        this.channels = (uint)channels;
                        this.sampleRate = (uint)sampleRate;
                        frame?.Dispose();
                        frame = new AudioFrame(this.sampleRate, this.channels, (uint)(tempBuffer.Length / this.channels));
                    }
                }

                if (tempBuffer == null)
                {
                    Utils.Error("Temp buffer is null");
                    return;
                }

                // Convert float to S16
                for (int i = 0; i < data.Length; i++)
                {
                    float normalizedSample = data[i] * S16_SCALE_FACTOR;
                    normalizedSample = Math.Min(normalizedSample, S16_MAX_VALUE);
                    normalizedSample = Math.Max(normalizedSample, S16_MIN_VALUE);
                    tempBuffer[i] = (short)(normalizedSample + (Math.Sign(normalizedSample) * 0.5f));
                }

                var audioBytes = MemoryMarshal.Cast<short, byte>(tempBuffer.AsSpan());

                using (var guard = buffer.Lock())
                {
                    guard.Value.Write(audioBytes);
                    
                    // Only process frame if we have enough data for a complete frame
                    int frameSize = (int)(frame.SamplesPerChannel * frame.NumChannels * sizeof(short));
                    int availableData = guard.Value.AvailableRead();
                    
                    if (availableData >= frameSize)
                    {
                        ProcessAudioFrame();
                    }
                }
            }
        }

        private void ProcessAudioFrame()
        {
            try
            {
                if (frame == null)
                {
                    Utils.Error("AudioFrame is null in ProcessAudioFrame");
                    return;
                }

                int frameSize = (int)(frame.SamplesPerChannel * frame.NumChannels * sizeof(short));
                
                unsafe
                {
                    var frameSpan = new Span<byte>(frame.Data.ToPointer(), frameSize);
                    
                    using (var guard = buffer.Lock())
                    {
                        int bytesRead = guard.Value.Read(frameSpan);
                        
                        // Only send the frame if we have enough data
                        if (bytesRead < frameSize)
                        {
                            return; // Don't send incomplete frames
                        }
                    }
                }

                using var request = FFIBridge.Instance.NewRequest<CaptureAudioFrameRequest>();
                using var audioFrameBufferInfo = request.TempResource<AudioFrameBufferInfo>();

                var pushFrame = request.request;
                pushFrame.SourceHandle = (ulong)handle.DangerousGetHandle();

                pushFrame.Buffer = audioFrameBufferInfo;
                pushFrame.Buffer.DataPtr = (ulong)frame.Data;
                pushFrame.Buffer.NumChannels = frame.NumChannels;
                pushFrame.Buffer.SampleRate = frame.SampleRate;
                pushFrame.Buffer.SamplesPerChannel = frame.SamplesPerChannel;

                using var response = request.Send();

                pushFrame.Buffer.DataPtr = 0;
                pushFrame.Buffer.NumChannels = 0;
                pushFrame.Buffer.SampleRate = 0;
                pushFrame.Buffer.SamplesPerChannel = 0;
            }
            catch (Exception e) 
            { 
                Utils.Error("Audio Framedata error: " + e.Message + "\nStackTrace: " + e.StackTrace); 
            }
        }
    }
}
