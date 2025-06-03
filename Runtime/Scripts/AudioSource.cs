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
        private uint _configuredSampleRate;
        private uint _configuredChannels;
        private bool _disposed;

        internal FfiHandle Handle { get; private set; }

        public bool IsRunning => _isRunning;

        public RtcAudioSource(AudioSource audioSource, IAudioFilter audioFilter, uint? forceChannels = null, uint? forceSampleRate = null, AudioProcessingOptions? options = null)
        {
            if (audioSource == null)
            {
                Utils.Error("RtcAudioSource - AudioSource is null");
                throw new ArgumentException("AudioSource must be valid");
            }

            ConfigureAudioSource(audioSource, audioFilter, forceChannels, forceSampleRate, options);
        }

        /// <summary>
        /// Creates an RtcAudioSource optimized for voice chat (mono, 1 channel).
        /// Voice chat doesn't benefit from stereo and mono reduces bandwidth usage.
        /// </summary>
        public static RtcAudioSource CreateForVoiceChat(AudioSource audioSource, IAudioFilter audioFilter, uint sampleRate)
        {
            return new RtcAudioSource(audioSource, audioFilter, forceChannels: 1, forceSampleRate: sampleRate, options: AudioProcessingOptions.Default);
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
            
            Stop();
            
            if (_audioFilter?.IsValid != true || !_audioSource) 
            {
                Utils.Error("AudioFilter or AudioSource is null - cannot start audio capture");
                return;
            }

            _buffers[0].Reset();
            _buffers[1].Reset();
            _writeIndex = 0;
            _readIndex = 1;
            
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = new CancellationTokenSource();
            
            _backgroundTask?.Dispose();
            _backgroundTask = Task.Run(() => ProcessAudioDataAsync(_cancellationTokenSource.Token));

            _isRunning = true;
            _audioFilter.AudioRead += OnAudioRead;
            _audioSource.Play();
        }

        public void Stop()
        {
            if (_disposed) return;
            
            _isRunning = false;
            
            if (_audioFilter?.IsValid == true) _audioFilter.AudioRead -= OnAudioRead;
            if (_audioSource) _audioSource.Stop();

            // Stop background processing gracefully
            if (_cancellationTokenSource != null && !_cancellationTokenSource.Token.IsCancellationRequested)
            {
                _cancellationTokenSource.Cancel();
                
                if (_backgroundTask != null && !_backgroundTask.IsCompleted)
                {
                    try
                    {
                        _backgroundTask.Wait(1000); // Wait up to 1 second for clean shutdown
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

        /// <summary>
        /// Reconfigures the RtcAudioSource with new audio components and settings.
        /// Useful for switching microphones or changing audio configuration without creating a new instance.
        /// The audio source will be stopped and needs to be manually started again after reconfiguration.
        /// </summary>
        /// <param name="audioSource">New AudioSource component</param>
        /// <param name="audioFilter">New IAudioFilter component</param>
        /// <param name="forceChannels">Optional: Override channel count (null = auto-detect)</param>
        /// <param name="forceSampleRate">Optional: Override sample rate (null = auto-detect)</param>
        /// <param name="options">Optional: Audio processing options</param>
        public void Reconfigure(AudioSource audioSource, IAudioFilter audioFilter, uint? forceChannels = null, uint? forceSampleRate = null, AudioProcessingOptions? options = null)
        {
            if (_disposed) 
            {
                Utils.Error("Cannot reconfigure RtcAudioSource: object has been disposed");
                return;
            }

            if (audioSource == null)
            {
                Utils.Error("RtcAudioSource.Reconfigure - AudioSource is null");
                throw new ArgumentException("AudioSource must be valid");
            }

            Stop();
            Handle?.Dispose(); // Dispose old FFI handle

            ConfigureAudioSource(audioSource, audioFilter, forceChannels, forceSampleRate, options);
            
            Utils.Debug($"RtcAudioSource reconfigured: {_configuredSampleRate}Hz, {_configuredChannels}ch");
        }

        private void ConfigureAudioSource(AudioSource audioSource, IAudioFilter audioFilter, uint? forceChannels, uint? forceSampleRate, AudioProcessingOptions? options)
        {
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
            
            newAudioSource.EnableQueue = true;

            using var response = request.Send();
            FfiResponse res = response;
            Handle = IFfiHandleFactory.Default.NewFfiHandle(res.NewAudioSource.Source.Handle!.Id);
            
            _audioSource = audioSource;
            _audioFilter = audioFilter;
            _configuredSampleRate = actualSampleRate;
            _configuredChannels = actualChannels;
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
            }
        }

        ~RtcAudioSource()
        {
            Dispose(false);
        }

        private void OnAudioRead(float[] data, int channels, int sampleRate)
        {
            if (!_isRunning || data == null || data.Length == 0) 
            {
                if (data == null || data.Length == 0)
                    Utils.Debug($"OnAudioRead: Invalid data - length: {data?.Length ?? -1}");
                return;
            }

            Utils.Debug($"OnAudioRead: Received {data.Length} samples, {channels}ch, {sampleRate}Hz");

            // Audio thread - lock-free processing
            var writeBuffer = _buffers[_writeIndex];
            
            if (writeBuffer.Data == null || writeBuffer.Data.Length != data.Length)
            {
                writeBuffer.Data = new short[data.Length];
                Utils.Debug($"OnAudioRead: Allocated new buffer with {data.Length} samples");
            }
            
            ConvertFloatToShort(data, writeBuffer.Data);
            
            writeBuffer.Length = data.Length;
            writeBuffer.Channels = channels;
            writeBuffer.SampleRate = sampleRate;
            writeBuffer.HasData = true; // This must be set last for thread safety
            
            _buffers[_writeIndex] = writeBuffer;
            
            TrySwapBuffers();
        }

        private static void ConvertFloatToShort(float[] input, short[] output)
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
                    var floatVector = new Vector<float>(input, i);
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

        private static void ConvertFloatToShortScalar(float[] input, short[] output, int start, int end)
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
                            Utils.Debug("TrySwapBuffers: SemaphoreSlim disposed during release");
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
                Utils.Debug("Background audio processing started");
                
                while (!cancellationToken.IsCancellationRequested && !_disposed)
                {
                    try
                    {
                        // Wait for new audio data to be available
                        await _dataAvailable.WaitAsync(cancellationToken);
                        
                        if (cancellationToken.IsCancellationRequested || _disposed)
                            break;
                            
                        // Process available data
                        var readBuffer = _buffers[_readIndex];
                        if (readBuffer.HasData)
                        {
                            Utils.Debug($"ProcessAudioDataAsync: Processing buffer with {readBuffer.Length} samples, {readBuffer.Channels}ch, {readBuffer.SampleRate}Hz");
                            
                            try
                            {
                                // Validate that audio format matches this source's configuration
                                // Each source maintains its own consistent sample rate
                                if (readBuffer.SampleRate == _configuredSampleRate && 
                                    readBuffer.Channels == _configuredChannels)
                                {
                                    SendAudioData(readBuffer.Data, readBuffer.Channels, readBuffer.SampleRate);
                                    Utils.Debug($"ProcessAudioDataAsync: Successfully sent {readBuffer.Length} samples to FFI ({readBuffer.Channels}ch, {readBuffer.SampleRate}Hz)");
                                }
                                else
                                {
                                    Utils.Error($"ProcessAudioDataAsync: FRAME DROPPED - Audio format mismatch for this source: " +
                                              $"Expected {_configuredSampleRate}Hz/{_configuredChannels}ch, " +
                                              $"got {readBuffer.SampleRate}Hz/{readBuffer.Channels}ch");
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
                        captureFrame.SourceHandle = (ulong)Handle.DangerousGetHandle();
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
            catch (Exception e) 
            { 
                Utils.Error($"RtcAudioSource: Error sending audio data: {e.Message}");
            }
        }
    }
}

