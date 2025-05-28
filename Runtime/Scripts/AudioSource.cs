using System;
using UnityEngine;
using LiveKit.Proto;
using LiveKit.Internal;
using LiveKit.Internal.FFIClients.Requests;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace LiveKit
{
    public class RtcAudioSource : IDisposable
    {
        private const float S16_MAX_VALUE = 32767f;
        private const float S16_MIN_VALUE = -32768f;
        private const float S16_SCALE_FACTOR = 32768f;
        private const int FRAME_DURATION_MS = 10;
        private const int QUEUE_BUFFER_SIZE = 10;
        
        private const int BATCH_SIZE = 3;
        private const int MAX_BATCH_DELAY_MS = 40;

        private AudioSource audioSource;
        private IAudioFilter audioFilter;
        private short[] tempBuffer;
        private short[] frameBuffer;
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
        private readonly List<short[]> batchBuffer = new();
        private DateTime lastBatchSent = DateTime.UtcNow;

        private long totalFramesSent;
        private long totalBlankFramesSent;
        private long totalDroppedFrames;
        private long totalBatchesSent;
        private DateTime lastStatsLog = DateTime.UtcNow;
        
        internal FfiHandle handle { get; }

        public bool IsRunning => isRunning;
        public int QueueSize => frameQueue.Count;
        public long TotalFramesSent => totalFramesSent;
        public long TotalBlankFramesSent => totalBlankFramesSent;
        public long TotalDroppedFrames => totalDroppedFrames;
        public long TotalBatchesSent => totalBatchesSent;
        public uint CurrentSampleRate => sampleRate;
        public uint CurrentChannels => channels;

        public RtcAudioSource(AudioSource audioSource, IAudioFilter audioFilter)
        {
            frameBufferLength = 0;
            
            var actualSampleRate = (uint)AudioSettings.outputSampleRate;
            var actualChannels = (uint)(AudioSettings.speakerMode switch
            {
                AudioSpeakerMode.Mono => 1,
                AudioSpeakerMode.Stereo => 2,
                AudioSpeakerMode.Quad => 4,
                AudioSpeakerMode.Surround => 5,
                AudioSpeakerMode.Mode5point1 => 6,
                AudioSpeakerMode.Mode7point1 => 8,
                _ => 2
            });
                       
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
            if (disposed) 
            {
                Utils.Error("Cannot start RtcAudioSource: object has been disposed");
                return;
            }
            
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
            if (disposed) return;
            
            if (audioFilter?.IsValid == true) audioFilter.AudioRead -= OnAudioRead;
            if (audioSource) audioSource.Stop();

            isRunning = false;
            
            lock (batchBuffer)
            {
                if (batchBuffer.Count > 0)
                {
                    SendBatchedFrames();
                }
            }
            
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
                            consecutiveErrors = 0;
                        }
                        else if (samplesPerFrame > 0 && blankFrame != null)
                        {
                            SendQueuedFrame(blankFrame);
                            consecutiveErrors = 0;
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
                        
                        await Task.Delay(Math.Min(100, FRAME_DURATION_MS * consecutiveErrors), cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation is requested
            }
            catch (Exception ex)
            {
                Utils.Error($"RtcAudioSource: Fatal error in queue processing: {ex.Message}");
            }
        }

        private void SendQueuedFrame(short[] frameData)
        {
            if (!frame.IsValid) return;

            lock (batchBuffer)
            {
                batchBuffer.Add(frameData);
                
                var timeSinceLastBatch = DateTime.UtcNow - lastBatchSent;
                bool shouldSendBatch = batchBuffer.Count >= BATCH_SIZE || 
                                     timeSinceLastBatch.TotalMilliseconds >= MAX_BATCH_DELAY_MS;
                
                if (shouldSendBatch && batchBuffer.Count > 0)
                {
                    SendBatchedFrames();
                }
            }
        }

        private void SendBatchedFrames()
        {
            if (batchBuffer.Count == 0) return;

            try
            {
                foreach (var frameData in batchBuffer)
                {
                    SendSingleFrame(frameData);
                    
                    if (frameData == blankFrame)
                    {
                        Interlocked.Increment(ref totalBlankFramesSent);
                    }
                    else
                    {
                        Interlocked.Increment(ref totalFramesSent);
                    }
                }
                
                Interlocked.Increment(ref totalBatchesSent);
                lastBatchSent = DateTime.UtcNow;
                
                batchBuffer.Clear();
                
                var now = DateTime.UtcNow;
                if ((now - lastStatsLog).TotalSeconds >= 30)
                {
                    lastStatsLog = now;
                    var queueSize = frameQueue.Count;
                    var totalFrames = totalFramesSent + totalBlankFramesSent;
                    var dropRate = totalFrames > 0 ? (totalDroppedFrames * 100.0 / totalFrames) : 0;
                    var queueUtilization = (queueSize * 100.0 / QUEUE_BUFFER_SIZE);
                    var avgBatchSize = totalBatchesSent > 0 ? (totalFrames / (double)totalBatchesSent) : 0;
                    
                }
            }
            catch (Exception ex)
            {
                Utils.Error($"RtcAudioSource: Error sending batched frames: {ex.Message}");
            }
        }

        private void SendSingleFrame(short[] frameData)
        {
            unsafe
            {
                var frameSpan = new Span<byte>(frame.Data.ToPointer(), cachedFrameSize);
                var audioBytes = MemoryMarshal.Cast<short, byte>(frameData.AsSpan());
                
                audioBytes.CopyTo(frameSpan);
            }

            SendAudioFrame();
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
                    if (frameBufferLength > 0)
                    {
                        FlushRemainingFrames();
                    }

                    tempBuffer = new short[data.Length];
                    this.channels = (uint)channels;
                    this.sampleRate = (uint)sampleRate;
                    
                    samplesPerFrame = (int)(sampleRate * channels * FRAME_DURATION_MS / 1000);
                    frameBuffer = new short[samplesPerFrame];
                    blankFrame = new short[samplesPerFrame];
                    frameBufferLength = 0;
                    
                    if (frame.IsValid) frame.Dispose();
                    frame = new AudioFrame(this.sampleRate, this.channels, (uint)(samplesPerFrame / this.channels));
                    
                    cachedFrameSize = frame.Length;
                }

                if (tempBuffer == null)
                {
                    Utils.Error("RtcAudioSource: Temp buffer is null");
                    return;
                }

                var tempSpan = tempBuffer.AsSpan();
                var dataSpan = data.AsSpan();
                
                for (int i = 0; i < dataSpan.Length; i++)
                {
                    float sample = dataSpan[i] * S16_SCALE_FACTOR;
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
            
            var currentQueueSize = frameQueue.Count;
            
            if (currentQueueSize < QUEUE_BUFFER_SIZE)
            {
                frameQueue.Enqueue(frameCopy);
                
            }
            else
            {
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
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private bool disposed = false;

        protected virtual void Dispose(bool disposing)
        {
            if (!disposed)
            {
                if (disposing)
                {
                    Stop();
                }
                
                if (handle != null)
                {
                    handle.Dispose();
                }
                
                disposed = true;
            }
        }

        ~RtcAudioSource()
        {
            Dispose(false);
        }
    }
}
