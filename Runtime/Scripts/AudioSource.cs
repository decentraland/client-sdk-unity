using System;
using UnityEngine;
using LiveKit.Proto;
using LiveKit.Internal;
using LiveKit.Internal.FFIClients.Requests;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace LiveKit
{
    public class RtcAudioSource : IDisposable
    {
        private const int DEFAULT_NUM_CHANNELS = 2;
        private const int DEFAULT_SAMPLE_RATE = 48000;
        private const float S16_MAX_VALUE = 32767f;
        private const float S16_MIN_VALUE = -32768f;
        private const float S16_SCALE_FACTOR = 32768f;
        private const int FRAME_DURATION_MS = 10; // 10ms frames for consistent processing
        private const int QUEUE_BUFFER_SIZE = 5; // 50ms buffer (5 frames of 10ms each)

        private AudioSource audioSource;
        private IAudioFilter audioFilter;
        private short[] tempBuffer;
        private short[] frameBuffer; // Buffer for 10ms frames
        private AudioFrame frame;
        private uint channels;
        private uint sampleRate;
        private int frameBufferLength;
        private int samplesPerFrame;
        private readonly object lockObject = new ();
        
        private int cachedFrameSize;
        
        private short[] blankFrame;
        
        private readonly ConcurrentQueue<short[]> frameQueue = new();
        private CancellationTokenSource cancellationTokenSource;
        private Task queueProcessorTask;
        private bool isRunning;

        // Performance monitoring
        private long totalFramesSent;
        private long totalBlankFramesSent;
        private long totalDroppedFrames;
        private DateTime lastStatsLog = DateTime.UtcNow;

        internal FfiHandle handle { get; }

        // Public properties for monitoring
        public bool IsRunning => isRunning;
        public int QueueSize => frameQueue.Count;
        public long TotalFramesSent => totalFramesSent;
        public long TotalBlankFramesSent => totalBlankFramesSent;
        public long TotalDroppedFrames => totalDroppedFrames;
        public uint CurrentSampleRate => sampleRate;
        public uint CurrentChannels => channels;

        public RtcAudioSource(AudioSource audioSource, IAudioFilter audioFilter)
        {
            frameBufferLength = 0;
            
            // Get actual audio settings from Unity AudioSource
            var actualSampleRate = (uint)AudioSettings.outputSampleRate;
            var actualChannels = (uint)AudioSettings.speakerMode switch
            {
                AudioSpeakerMode.Mono => 1,
                AudioSpeakerMode.Stereo => 2,
                AudioSpeakerMode.Quad => 4,
                AudioSpeakerMode.Surround => 5,
                AudioSpeakerMode.Mode5point1 => 6,
                AudioSpeakerMode.Mode7point1 => 8,
                _ => DEFAULT_NUM_CHANNELS
            };
            
            Debug.Log($"RtcAudioSource: Using sample rate {actualSampleRate}Hz, {actualChannels} channels");
            
            using var request = FFIBridge.Instance.NewRequest<NewAudioSourceRequest>();
            var newAudioSource = request.request;
            newAudioSource.Type = AudioSourceType.AudioSourceNative;
            newAudioSource.NumChannels = actualChannels;
            newAudioSource.SampleRate = actualSampleRate;
            
            newAudioSource.Options = new AudioSourceOptions
            {
                EchoCancellation = true,
                NoiseSuppression = true,
                AutoGainControl = true
            };
            
            newAudioSource.EnableQueue = true;

            using var response = request.Send();
            FfiResponse res = response;
            handle = IFfiHandleFactory.Default.NewFfiHandle(res.NewAudioSource.Source.Handle!.Id);
            this.audioSource = audioSource;
            this.audioFilter = audioFilter;
        }

        public void Start()
        {
            Stop();
            if (audioFilter?.IsValid != true || !audioSource) 
            {
                Utils.Error("AudioFilter or AudioSource is null - cannot start audio capture");
                return;
            }

            isRunning = true;
            cancellationTokenSource = new CancellationTokenSource();
            
            queueProcessorTask = Task.Run(async () => await ProcessQueueAsync(cancellationTokenSource.Token));
            
            audioFilter.AudioRead += OnAudioRead;
            audioSource.Play();
        }

        public void Stop()
        {
            if (audioFilter?.IsValid == true) audioFilter.AudioRead -= OnAudioRead;
            if (audioSource) audioSource.Stop();

            isRunning = false;
            
            cancellationTokenSource?.Cancel();
            queueProcessorTask?.Wait(1000);
            
            FlushRemainingFrames();

            if (frame.IsValid) frame.Dispose();
            
            cancellationTokenSource?.Dispose();
        }

        private async Task ProcessQueueAsync(CancellationToken cancellationToken)
        {
            const int maxConsecutiveErrors = 5;
            int consecutiveErrors = 0;
            
            try
            {
                while (!cancellationToken.IsCancellationRequested && isRunning)
                {
                    try
                    {
                        if (frameQueue.TryDequeue(out var frameData))
                        {
                            SendQueuedFrame(frameData);
                            consecutiveErrors = 0; // Reset error count on success
                        }
                        else if (samplesPerFrame > 0 && blankFrame != null)
                        {
                            SendQueuedFrame(blankFrame);
                            consecutiveErrors = 0; // Reset error count on success
                        }
                        
                        await Task.Delay(FRAME_DURATION_MS, cancellationToken);
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException))
                    {
                        consecutiveErrors++;
                        Utils.Error($"RtcAudioSource: Error in queue processing (attempt {consecutiveErrors}): {ex.Message}");
                        
                        if (consecutiveErrors >= maxConsecutiveErrors)
                        {
                            Utils.Error("RtcAudioSource: Too many consecutive errors, stopping queue processing");
                            break;
                        }
                        
                        // Brief delay before retry
                        await Task.Delay(Math.Min(100, FRAME_DURATION_MS * consecutiveErrors), cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation is requested
                Debug.Log("RtcAudioSource: Queue processing cancelled");
            }
            catch (Exception ex)
            {
                Utils.Error($"RtcAudioSource: Fatal error in queue processing: {ex.Message}");
            }
        }

        private void SendQueuedFrame(short[] frameData)
        {
            if (!frame.IsValid) return;

            unsafe
            {
                var frameSpan = new Span<byte>(frame.Data.ToPointer(), cachedFrameSize);
                var audioBytes = MemoryMarshal.Cast<short, byte>(frameData.AsSpan());
                
                audioBytes.CopyTo(frameSpan);
            }

            SendAudioFrame();
            
            // Track metrics
            if (frameData == blankFrame)
            {
                Interlocked.Increment(ref totalBlankFramesSent);
            }
            else
            {
                Interlocked.Increment(ref totalFramesSent);
            }
            
            // Log stats periodically (every 30 seconds)
            var now = DateTime.UtcNow;
            if ((now - lastStatsLog).TotalSeconds >= 30)
            {
                lastStatsLog = now;
                var queueSize = frameQueue.Count;
                Debug.Log($"RtcAudioSource Stats: Frames={totalFramesSent}, Blank={totalBlankFramesSent}, Dropped={totalDroppedFrames}, QueueSize={queueSize}");
            }
        }

        private void OnAudioRead(float[] data, int channels, int sampleRate)
        {
            bool needsReconfiguration = channels != this.channels || 
                                      sampleRate != this.sampleRate || 
                                      data.Length != tempBuffer?.Length;

            lock (lockObject)
            {
                if (needsReconfiguration)
                {
                    if (this.channels != 0 || this.sampleRate != 0)
                    {
                        Debug.LogWarning($"RtcAudioSource: Audio format changed from {this.sampleRate}Hz/{this.channels}ch to {sampleRate}Hz/{channels}ch");
                    }
                    
                    if (frameBufferLength > 0)
                    {
                        FlushRemainingFrames();
                    }

                    tempBuffer = new short[data.Length];
                    this.channels = (uint)channels;
                    this.sampleRate = (uint)sampleRate;
                    
                    samplesPerFrame = (int)(sampleRate * channels * FRAME_DURATION_MS / 1000);
                    frameBuffer = new short[samplesPerFrame];
                    blankFrame = new short[samplesPerFrame]; // Pre-allocate blank frame
                    frameBufferLength = 0;
                    
                    if (frame.IsValid) frame.Dispose();
                    frame = new AudioFrame(this.sampleRate, this.channels, (uint)(samplesPerFrame / this.channels));
                    
                    cachedFrameSize = frame.Length;
                    
                    Debug.Log($"RtcAudioSource: Reconfigured for {sampleRate}Hz, {channels} channels, {samplesPerFrame} samples per frame");
                }

                if (tempBuffer == null)
                {
                    Debug.LogError("RtcAudioSource: Temp buffer is null");
                    return;
                }

                // Convert float samples to short with optimized conversion
                var tempSpan = tempBuffer.AsSpan();
                var dataSpan = data.AsSpan();
                
                // Vectorized conversion for better performance
                for (int i = 0; i < dataSpan.Length; i++)
                {
                    float sample = dataSpan[i] * S16_SCALE_FACTOR;
                    // Clamp and round in one operation
                    tempSpan[i] = (short)Math.Clamp(sample + (sample >= 0 ? 0.5f : -0.5f), S16_MIN_VALUE, S16_MAX_VALUE);
                }

                ProcessAudioChunks(tempBuffer);
            }
        }

        private void ProcessAudioChunks(short[] audioData)
        {
            int samplesProcessed = 0;
            
            while (samplesProcessed < audioData.Length)
            {
                int remainingSamples = audioData.Length - samplesProcessed;
                
                if (frameBufferLength > 0 || remainingSamples < samplesPerFrame)
                {
                    int missingLength = samplesPerFrame - frameBufferLength;
                    int samplesToAdd = Math.Min(missingLength, remainingSamples);
                    
                    Array.Copy(audioData, samplesProcessed, frameBuffer, frameBufferLength, samplesToAdd);
                    frameBufferLength += samplesToAdd;
                    samplesProcessed += samplesToAdd;
                    
                    if (frameBufferLength == samplesPerFrame)
                    {
                        EnqueueFrame(frameBuffer);
                        frameBufferLength = 0;
                    }
                }
                else
                {
                    var frameData = new short[samplesPerFrame];
                    Array.Copy(audioData, samplesProcessed, frameData, 0, samplesPerFrame);
                    EnqueueFrame(frameData);
                    samplesProcessed += samplesPerFrame;
                }
            }
        }

        private void EnqueueFrame(short[] frameData)
        {
            if (!isRunning) return;
            
            var frameCopy = new short[frameData.Length];
            Array.Copy(frameData, frameCopy, frameData.Length);
            
            if (frameQueue.Count < QUEUE_BUFFER_SIZE)
            {
                frameQueue.Enqueue(frameCopy);
            }
            else
            {
                // Queue is full, drop the oldest frame and add the new one
                frameQueue.TryDequeue(out _);
                frameQueue.Enqueue(frameCopy);
                Interlocked.Increment(ref totalDroppedFrames);
            }
        }

        private void FlushRemainingFrames()
        {
            lock (lockObject)
            {
                if (frameBufferLength > 0 && frame.IsValid)
                {
                    for (int i = frameBufferLength; i < samplesPerFrame; i++)
                    {
                        frameBuffer[i] = 0;
                    }
                    
                    EnqueueFrame(frameBuffer);
                    frameBufferLength = 0;
                }
            }
        }

        private void SendAudioFrame()
        {
            try
            {
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

        public void Dispose()
        {
            Stop();
        }
    }
}
