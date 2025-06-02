using System;
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

        private AudioSource _audioSource;
        private IAudioFilter _audioFilter;
        private short[] _audioBuffer;
        private readonly object _lockObject = new();
        private bool _isRunning;
        private readonly uint _configuredSampleRate;
        private readonly uint _configuredChannels;
        private bool _disposed;

        internal FfiHandle Handle { get; }

        public bool IsRunning => _isRunning;

        public RtcAudioSource(AudioSource audioSource, IAudioFilter audioFilter, uint? forceChannels = null)
        {
            if (audioSource == null)
            {
                Utils.Error("RtcAudioSource - AudioSource is null");
                throw new ArgumentException("AudioSource must be valid");
            }

            var actualSampleRate = (uint)AudioSettings.outputSampleRate;
            
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
                    _ => 1  // Default to mono for voice chat instead of stereo
                });
            }
            
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
            Handle = IFfiHandleFactory.Default.NewFfiHandle(res.NewAudioSource.Source.Handle!.Id);
            _audioSource = audioSource;
            _audioFilter = audioFilter;

            // Cache Unity's audio settings to avoid main thread access from audio thread
            _configuredSampleRate = actualSampleRate;
            _configuredChannels = actualChannels;
        }

        /// <summary>
        /// Creates an RtcAudioSource optimized for voice chat (mono, 1 channel).
        /// Voice chat doesn't benefit from stereo and mono reduces bandwidth usage.
        /// </summary>
        public static RtcAudioSource CreateForVoiceChat(AudioSource audioSource, IAudioFilter audioFilter)
        {
            return new RtcAudioSource(audioSource, audioFilter, forceChannels: 1);
        }

        /// <summary>
        /// Creates an RtcAudioSource for high-quality audio (stereo, 2 channels).
        /// Suitable for music, screen share audio, or other high-fidelity audio content.
        /// </summary>
        public static RtcAudioSource CreateForHighQualityAudio(AudioSource audioSource, IAudioFilter audioFilter)
        {
            return new RtcAudioSource(audioSource, audioFilter, forceChannels: 2);
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

            _isRunning = true;
            _audioFilter.AudioRead += OnAudioRead;
            _audioSource.Play();
        }

        public void Stop()
        {
            if (_disposed) return;
            
            if (_audioFilter?.IsValid == true) _audioFilter.AudioRead -= OnAudioRead;
            if (_audioSource) _audioSource.Stop();

            _isRunning = false;
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
                    _audioBuffer = null;
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
            if (!_isRunning || data == null || data.Length == 0) return;

            lock (_lockObject)
            {
                // Recreate buffer only when size changes
                if (_audioBuffer == null || _audioBuffer.Length != data.Length)
                {
                    _audioBuffer = new short[data.Length];
                }

                // Convert float samples to 16-bit PCM
                for (int i = 0; i < data.Length; i++)
                {
                    float sample = data[i] * S16ScaleFactor;
                    _audioBuffer[i] = (short)Math.Clamp(sample + (sample >= 0 ? 0.5f : -0.5f), S16MinValue, S16MaxValue);
                }

                SendAudioData(_audioBuffer, channels, sampleRate);
            }
        }

        private void SendAudioData(short[] audioData, int channels, int sampleRate)
        {
            try
            {
                uint samplesPerChannel = (uint)(audioData.Length / channels);

                // Validate that the frame format matches what we configured the source for
                if (sampleRate != _configuredSampleRate)
                {
                    Utils.Error($"Sample rate mismatch! Expected {_configuredSampleRate}Hz, got {sampleRate}Hz. " +
                              "Audio data must be resampled to Unity's format before sending to LiveKit.");
                    return;
                }

                if (channels != _configuredChannels)
                {
                    Utils.Error($"Channel count mismatch! Expected {_configuredChannels} channels, got {channels} channels.");
                    return;
                }

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

