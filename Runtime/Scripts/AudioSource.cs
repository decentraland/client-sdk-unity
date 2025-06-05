using System;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using LiveKit.Proto;
using LiveKit.Internal;
using LiveKit.Internal.FFIClients.Requests;

namespace LiveKit
{
    public class RtcAudioSource : IDisposable
    {
        private const float S16MaxValue = 32767f;
        private const float S16MinValue = -32768f;
        private const float S16ScaleFactor = 32768f;

        // PITCH SHIFT TRACE: Static timing tracking
        private static DateTime _lastAudioFrameTime = DateTime.MinValue;
        private static int _frameCount = 0;

        private struct AudioBuffer
        {
            public short[] Data;
            public int Length;
            public int Channels;
            public int SampleRate;
            public volatile bool HasData;
            
            public void Reset()
            {
                HasData = false;
                Length = 0;
                Channels = 0;
                SampleRate = 0;
            }
        }

        private AudioSource _audioSource;
        private IAudioFilter _audioFilter;
        private AudioBuffer[] _buffers = new AudioBuffer[2];
        private volatile int _writeIndex = 0;
        private volatile int _readIndex = 1;
        private readonly object _swapLock = new();
        private readonly SemaphoreSlim _dataAvailable = new(0, 1);
        private CancellationTokenSource? _cancellationTokenSource;
        private Task _backgroundTask;
        private bool _isRunning;
        private readonly uint _configuredSampleRate;
        private readonly uint _configuredChannels;
        private bool _disposed;

        internal FfiHandle Handle { get; private set; }

        public bool IsRunning => _isRunning;
        public uint ConfiguredSampleRate => _configuredSampleRate;
        public uint ConfiguredChannels => _configuredChannels;

        public RtcAudioSource(AudioSource audioSource, IAudioFilter audioFilter, uint? forceChannels = null, uint? forceSampleRate = null, AudioProcessingOptions? options = null)
        {
            if (audioSource == null)
            {
                Utils.Error("RtcAudioSource - AudioSource is null");
                throw new ArgumentException("AudioSource must be valid");
            }

            var audioOptions = options ?? AudioProcessingOptions.Default;

            uint actualSampleRate;
            if (forceSampleRate.HasValue)
            {
                actualSampleRate = forceSampleRate.Value;
            }
            else
            {
                actualSampleRate = (uint)AudioSettings.outputSampleRate;
            }
            
            uint actualChannels;
            if (forceChannels.HasValue)
            {
                actualChannels = forceChannels.Value;
            }
            else
            {
                actualChannels = (uint)(AudioSettings.speakerMode switch
                {
                    AudioSpeakerMode.Mono => 1,
                    AudioSpeakerMode.Stereo => 2,
                    AudioSpeakerMode.Quad => 4,
                    AudioSpeakerMode.Surround => 5,
                    AudioSpeakerMode.Mode5point1 => 6,
                    AudioSpeakerMode.Mode7point1 => 8,
                    _ => 1
                });
            }
            
            using var request = FFIBridge.Instance.NewRequest<NewAudioSourceRequest>();
            var newAudioSource = request.request;
            newAudioSource.Type = AudioSourceType.AudioSourceNative;
            newAudioSource.NumChannels = actualChannels;
            newAudioSource.SampleRate = actualSampleRate;
            
            newAudioSource.Options = new AudioSourceOptions
            {
                EchoCancellation = audioOptions.EchoCancellation,
                NoiseSuppression = audioOptions.NoiseSuppression,
                AutoGainControl = audioOptions.AutoGainControl
            };
            
            newAudioSource.EnableQueue = audioOptions.EnableQueue;

            using var response = request.Send();
            FfiResponse res = response;
            Handle = IFfiHandleFactory.Default.NewFfiHandle(res.NewAudioSource.Source.Handle!.Id);
            
            _audioSource = audioSource;
            _audioFilter = audioFilter;
            _configuredSampleRate = actualSampleRate;
            _configuredChannels = actualChannels;
        }

        /// <summary>
        /// Creates an RtcAudioSource optimized for voice chat (mono, 1 channel).
        /// Voice chat doesn't benefit from stereo and mono reduces bandwidth usage.
        /// </summary>
        public static RtcAudioSource CreateForVoiceChat(AudioSource audioSource, IAudioFilter audioFilter, uint sampleRate)
        {
            return new RtcAudioSource(audioSource, audioFilter, forceChannels: 1, forceSampleRate: sampleRate, options: AudioProcessingOptions.LowLatency);
        }

        /// <summary>
        /// Creates an RtcAudioSource for high-quality audio (stereo, 2 channels).
        /// Suitable for music, screen share audio, or other high-fidelity audio content.
        /// </summary>
        public static RtcAudioSource CreateForHighQualityAudio(AudioSource audioSource, IAudioFilter audioFilter, uint sampleRate)
        {
            return new RtcAudioSource(audioSource, audioFilter, forceChannels: 2, forceSampleRate: sampleRate, options: AudioProcessingOptions.HighQuality);
        }

        /// <summary>
        /// Creates an RtcAudioSource optimized for ultra-low latency real-time communication.
        /// Disables internal buffering and queue mode for minimum possible delay.
        /// May cause audio glitches on slower devices or poor network conditions.
        /// </summary>
        public static RtcAudioSource CreateForLowLatency(AudioSource audioSource, IAudioFilter audioFilter, uint sampleRate, uint channels = 1)
        {
            return new RtcAudioSource(audioSource, audioFilter, forceChannels: channels, forceSampleRate: sampleRate, options: AudioProcessingOptions.LowLatency);
        }

        /// <summary>
        /// Creates an RtcAudioSource with full custom configuration.
        /// </summary>
        public static RtcAudioSource CreateCustom(AudioSource audioSource, IAudioFilter audioFilter, uint sampleRate, uint channels, AudioProcessingOptions? options = null)
        {
            return new RtcAudioSource(audioSource, audioFilter, forceChannels: channels, forceSampleRate: sampleRate, options: options ?? AudioProcessingOptions.Default);
        }

        public void Start()
        {
            if (_disposed) 
            {
                Utils.Error("Cannot start RtcAudioSource: object has been disposed");
                return;
            }
            
            if (_audioFilter?.IsValid != true || !_audioSource) 
            {
                Utils.Error("AudioFilter or AudioSource is null - cannot start audio capture");
                return;
            }

            // If already running, stop first to ensure clean state
            if (_isRunning)
            {
                Debug.LogError("RtcAudioSource.Start() called while running - stopping first");
                Stop();
            }

            // Always start fresh - clean up any existing background processing
            StopBackgroundProcessing();

            // Reset all buffer state
            _buffers[0].Reset();
            _buffers[1].Reset();
            _writeIndex = 0;
            _readIndex = 1;
            
            // Create fresh background processing
            _cancellationTokenSource = new CancellationTokenSource();
            _backgroundTask = Task.Run(() => ProcessAudioDataAsync(_cancellationTokenSource.Token));

            // Start capturing audio
            _isRunning = true;
            _audioFilter.AudioRead += OnAudioRead;
            
            Debug.LogError("RtcAudioSource.Start() - BUFFER SIZE DEBUGGING: Started with fresh state");
        }

        public void Stop()
        {
            if (_disposed) return;
            
            _isRunning = false;
            
            // Unsubscribe from audio events
            if (_audioFilter?.IsValid == true) 
                _audioFilter.AudioRead -= OnAudioRead;

            // Stop background processing
            StopBackgroundProcessing();
            
            Utils.Debug("RtcAudioSource.Stop() - Stopped cleanly");
        }

        private void StopBackgroundProcessing()
        {
            if (_cancellationTokenSource != null && !_cancellationTokenSource.Token.IsCancellationRequested)
            {
                _cancellationTokenSource.Cancel();
                
                if (_backgroundTask != null && !_backgroundTask.IsCompleted)
                {
                    try
                    {
                        _backgroundTask.Wait(1000);
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected when cancelling
                    }
                    catch (AggregateException ex) when (ex.InnerException is OperationCanceledException)
                    {
                        // Also expected when cancelling
                    }
                    catch (Exception ex)
                    {
                        Utils.Error($"Error stopping background audio processing: {ex.Message}");
                    }
                }
                
                _backgroundTask?.Dispose();
                _backgroundTask = null;
                
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    Stop();
                    
                    _audioSource = null;
                    _audioFilter = null;
                    
                    _buffers[0].Reset();
                    _buffers[1].Reset();
                    _dataAvailable?.Dispose();
                }
                
                Handle?.Dispose();
                _disposed = true;
                
                Utils.Debug($"RtcAudioSource disposed: {_configuredSampleRate}Hz, {_configuredChannels}ch");
            }
        }

        ~RtcAudioSource()
        {
            Dispose(false);
        }

        private void OnAudioRead(Span<float> data, int channels, int sampleRate)
        {
            if (!_isRunning || data == null || data.Length == 0) 
            {
                if (data == null || data.Length == 0)
                    Debug.LogWarning($"OnAudioRead: Invalid data - length: {data.Length}");
                return;
            }

            // Check for format mismatch - this indicates the need for a new RtcAudioSource
            if (sampleRate != _configuredSampleRate || channels != _configuredChannels)
            {
                Utils.Error($"OnAudioRead: Audio format mismatch! Expected {_configuredSampleRate}Hz/{_configuredChannels}ch, " +
                          $"got {sampleRate}Hz/{channels}ch. This RtcAudioSource needs to be disposed and recreated.");
                return;
            }

            // PITCH SHIFT TRACE: Calculate timing metrics (thread-safe)
            var currentTime = DateTime.UtcNow;
            var expectedDurationMs = (float)data.Length / channels / sampleRate * 1000f;
            
            Debug.LogError($"OnAudioRead: PITCH TRACE - Unity frame: {data.Length} samples, {channels}ch, {sampleRate}Hz, " +
                          $"expected duration: {expectedDurationMs:F2}ms, timestamp: {currentTime:mm:ss.fff}");

            var writeBuffer = _buffers[_writeIndex];
            
            if (writeBuffer.Data == null || writeBuffer.Data.Length != data.Length)
            {
                writeBuffer.Data = new short[data.Length];
                Debug.LogError($"OnAudioRead: Allocated new buffer with {data.Length} samples");
            }
            
            ConvertFloatToShort(data, writeBuffer.Data);
            
            writeBuffer.Length = data.Length;
            writeBuffer.Channels = channels;
            writeBuffer.SampleRate = sampleRate;
            writeBuffer.HasData = true;
            
            _buffers[_writeIndex] = writeBuffer;
            
            TrySwapBuffers();
        }

        private static void ConvertFloatToShort(Span<float> input, short[] output)
        {
            if (Vector.IsHardwareAccelerated && input.Length >= Vector<float>.Count)
            {
                var scaleVector = new Vector<float>(S16ScaleFactor);
                var minVector = new Vector<float>(S16MinValue);
                var maxVector = new Vector<float>(S16MaxValue);
                var halfVector = new Vector<float>(0.5f);
                
                int vectorLength = Vector<float>.Count;
                int vectorizedLength = input.Length - (input.Length % vectorLength);
                
                for (int i = 0; i < vectorizedLength; i += vectorLength)
                {
                    var floatVector = new Vector<float>(input.Slice(i, vectorLength));
                    var scaledVector = floatVector * scaleVector;
                    var roundingVector = Vector.ConditionalSelect(
                        Vector.GreaterThanOrEqual(scaledVector, Vector<float>.Zero),
                        halfVector,
                        -halfVector);
                    var roundedVector = scaledVector + roundingVector;
                    var clampedVector = Vector.Max(Vector.Min(roundedVector, maxVector), minVector);
                    
                    for (int j = 0; j < vectorLength; j++)
                    {
                        output[i + j] = (short)clampedVector[j];
                    }
                }
                
                // Handle remaining samples using scalar conversion
                ConvertFloatToShortScalar(input, output, vectorizedLength, input.Length);
            }
            else
            {
                // Fallback to scalar processing for entire array
                ConvertFloatToShortScalar(input, output, 0, input.Length);
            }
        }

        private static void ConvertFloatToShortScalar(Span<float> input, short[] output, int start, int end)
        {
            for (int i = start; i < end; i++)
            {
                float sample = input[i] * S16ScaleFactor;
                output[i] = (short)Math.Clamp(sample + (sample >= 0 ? 0.5f : -0.5f), S16MinValue, S16MaxValue);
            }
        }

        private void TrySwapBuffers()
        {
            // Non-blocking attempt to swap buffers
            if (Monitor.TryEnter(_swapLock, 0))
            {
                try
                {
                    if (_isRunning && !_disposed)
                    {
                        var oldWrite = _writeIndex;
                        var oldRead = _readIndex;
                        _writeIndex = oldRead;
                        _readIndex = oldWrite;
                        
                        Utils.Debug($"TrySwapBuffers: Successfully swapped buffers (write: {oldWrite}->{_writeIndex}, read: {oldRead}->{_readIndex})");
                        
                        // Signal background thread that new data is available
                        try
                        {
                            _dataAvailable?.Release();
                            Utils.Debug("TrySwapBuffers: Signaled background thread - new data available");
                        }
                        catch (ObjectDisposedException)
                        {
                            // Can happen during shutdown, ignore
                        }
                        catch (SemaphoreFullException)
                        {
                            // Background thread hasn't processed previous data yet, skip this frame
                            // This is better than blocking the audio thread
                            Utils.Debug("TrySwapBuffers: FRAME DROPPED - Background thread busy, semaphore full");
                        }
                    }
                }
                finally
                {
                    Monitor.Exit(_swapLock);
                }
            }
            else
            {
                // If can't get lock immediately, skip this frame (better than blocking audio thread)
                Utils.Debug("TrySwapBuffers: FRAME DROPPED - Could not acquire lock immediately");
            }
        }

        private async Task ProcessAudioDataAsync(CancellationToken cancellationToken)
        {
            try
            {                
                while (!cancellationToken.IsCancellationRequested && !_disposed)
                {
                    try
                    {
                        await _dataAvailable.WaitAsync(cancellationToken);
                        
                        if (cancellationToken.IsCancellationRequested || _disposed)
                            break;
                            
                        var readBuffer = _buffers[_readIndex];
                        if (readBuffer.HasData)
                        {
                            // PITCH SHIFT TRACE: Track frame info
                            var expectedDurationMs = (float)readBuffer.Length / readBuffer.Channels / readBuffer.SampleRate * 1000f;
                            
                            Debug.LogError($"ProcessAudioDataAsync: PITCH TRACE - Processing {readBuffer.Length} samples, {readBuffer.Channels}ch, {readBuffer.SampleRate}Hz, " +
                                          $"expected duration: {expectedDurationMs:F2}ms");
                            
                            try
                            {
                                if (Handle != null && 
                                    readBuffer.SampleRate == _configuredSampleRate && 
                                    readBuffer.Channels == _configuredChannels)
                                {
                                    SendAudioData(readBuffer.Data, readBuffer.Channels, readBuffer.SampleRate);
                                    
                                    Debug.LogError($"ProcessAudioDataAsync: PITCH TRACE - SENT TO FFI - {readBuffer.Length} samples " +
                                                  $"({readBuffer.Channels}ch, {readBuffer.SampleRate}Hz)");
                                }
                                else
                                {
                                    if (Handle == null)
                                    {
                                        Utils.Debug("ProcessAudioDataAsync: FRAME DROPPED - No valid handle");
                                    }
                                    else
                                    {
                                        Utils.Error($"ProcessAudioDataAsync: FRAME DROPPED - Audio format mismatch: " +
                                                  $"Expected {_configuredSampleRate}Hz/{_configuredChannels}ch, " +
                                                  $"got {readBuffer.SampleRate}Hz/{readBuffer.Channels}ch. " +
                                                  $"RtcAudioSource needs to be disposed and recreated.");
                                    }
                                }                              
                            }
                            catch (Exception ex)
                            {
                                Utils.Error($"ProcessAudioDataAsync: Error processing audio data: {ex.Message}");
                            }
                            finally
                            {
                                // Mark buffer as processed
                                lock (_swapLock)
                                {
                                    if (_readIndex < _buffers.Length && _buffers != null)
                                    {
                                        _buffers[_readIndex].HasData = false;
                                        Utils.Debug($"ProcessAudioDataAsync: Buffer {_readIndex} marked as processed");
                                    }
                                }
                            }
                        }
                        else
                        {
                            Utils.Debug("ProcessAudioDataAsync: Signaled but no data available in read buffer");
                        }
                    }
                    catch (ObjectDisposedException)
                    {
                        // SemaphoreSlim was disposed, time to exit
                        break;
                    }
                    catch (OperationCanceledException)
                    {
                        // Cancellation requested, exit gracefully
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Utils.Error($"Unexpected error in background audio processing: {ex.Message}");
            }
            finally
            {
                Utils.Debug("Background audio processing stopped");
            }
        }

        private void SendAudioData(short[] audioData, int channels, int sampleRate)
        {
            var currentHandle = Handle;
            if (currentHandle == null)
            {
                Utils.Debug("SendAudioData: Handle is null, skipping frame");
                return;
            }

            try
            {
                uint samplesPerChannel = (uint)(audioData.Length / channels);

                unsafe
                {
                    fixed (short* dataPtr = audioData)
                    {
                        using var request = FFIBridge.Instance.NewRequest<CaptureAudioFrameRequest>();
                        using var audioFrameBufferInfo = request.TempResource<AudioFrameBufferInfo>();

                        var captureFrame = request.request;
                        captureFrame.SourceHandle = (ulong)currentHandle.DangerousGetHandle();
                        captureFrame.Buffer = audioFrameBufferInfo;
                        captureFrame.Buffer.DataPtr = (ulong)dataPtr;
                        captureFrame.Buffer.NumChannels = (uint)channels;
                        captureFrame.Buffer.SampleRate = (uint)sampleRate;
                        captureFrame.Buffer.SamplesPerChannel = samplesPerChannel;

                        using var response = request.Send();

                        captureFrame.Buffer.DataPtr = 0;
                        captureFrame.Buffer.NumChannels = 0;
                        captureFrame.Buffer.SampleRate = 0;
                        captureFrame.Buffer.SamplesPerChannel = 0;
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                Utils.Debug("SendAudioData: Handle was disposed during send");
            }
            catch (Exception e) 
            { 
                Utils.Error($"RtcAudioSource: Error sending audio data: {e.Message}");
            }
        }
    }
}

