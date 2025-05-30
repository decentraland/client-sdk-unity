using System;
using UnityEngine;
using LiveKit.Proto;
using LiveKit.Internal;
using LiveKit.Internal.FFIClients.Requests;

namespace LiveKit
{
    public class RtcAudioSource : IDisposable
    {
        private const float S16_MAX_VALUE = 32767f;
        private const float S16_MIN_VALUE = -32768f;
        private const float S16_SCALE_FACTOR = 32768f;

        private AudioSource audioSource;
        private IAudioFilter audioFilter;
        private short[] audioBuffer;
        private readonly object lockObject = new();
        private bool isRunning;
        
        // Cache Unity's audio settings to avoid main thread access from audio thread
        private readonly uint configuredSampleRate;
        private readonly uint configuredChannels;
        
        internal FfiHandle handle { get; }

        public bool IsRunning => isRunning;

        /// <summary>
        /// Configures Unity's audio system for optimal LiveKit compatibility.
        /// Call this once at application startup.
        /// </summary>
        public static void ConfigureUnityAudioForLiveKit()
        {
            // Set Unity to use 48kHz to match LiveKit's transmission format
            var configuration = AudioSettings.GetConfiguration();
            configuration.sampleRate = 48000;  // 48kHz - LiveKit standard
            configuration.dspBufferSize = 512;  // Good balance of latency and performance
            
            if (!AudioSettings.Reset(configuration))
            {
                Utils.Error("Failed to configure Unity audio for 48kHz. Using default settings.");
            }
            else
            {
                Utils.Debug("Unity audio configured for LiveKit: 48kHz sample rate");
            }
        }

        public RtcAudioSource(AudioSource audioSource, IAudioFilter audioFilter, uint? forceChannels = null)
        {
            if (audioSource == null)
            {
                Utils.Error("RtcAudioSource - AudioSource is null");
                throw new ArgumentException("AudioSource must be valid");
            }

            // Use Unity's audio system format since that's what OnAudioFilterRead provides
            var actualSampleRate = (uint)AudioSettings.outputSampleRate;
            
            uint actualChannels;
            if (forceChannels.HasValue)
            {
                // Use explicitly specified channel count (e.g., 1 for voice chat)
                actualChannels = forceChannels.Value;
            }
            else
            {
                // Use Unity's audio system format since that's what OnAudioFilterRead provides
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
            handle = IFfiHandleFactory.Default.NewFfiHandle(res.NewAudioSource.Source.Handle!.Id);
            this.audioSource = audioSource;
            this.audioFilter = audioFilter;

            // Cache Unity's audio settings to avoid main thread access from audio thread
            configuredSampleRate = actualSampleRate;
            configuredChannels = actualChannels;
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
            audioFilter.AudioRead += OnAudioRead;
            audioSource.Play();
        }

        public void Stop()
        {
            if (disposed) return;
            
            if (audioFilter?.IsValid == true) audioFilter.AudioRead -= OnAudioRead;
            if (audioSource) audioSource.Stop();

            isRunning = false;
            audioBuffer = null;
        }

        private void OnAudioRead(float[] data, int channels, int sampleRate)
        {
            if (!isRunning || data == null || data.Length == 0) return;

            lock (lockObject)
            {
                // Recreate buffer only when size changes
                if (audioBuffer == null || audioBuffer.Length != data.Length)
                {
                    audioBuffer = new short[data.Length];
                }

                // Convert float samples to 16-bit PCM
                for (int i = 0; i < data.Length; i++)
                {
                    float sample = data[i] * S16_SCALE_FACTOR;
                    audioBuffer[i] = (short)Math.Clamp(sample + (sample >= 0 ? 0.5f : -0.5f), S16_MIN_VALUE, S16_MAX_VALUE);
                }

                SendAudioData(audioBuffer, channels, sampleRate);
            }
        }

        private void SendAudioData(short[] audioData, int channels, int sampleRate)
        {
            try
            {
                uint samplesPerChannel = (uint)(audioData.Length / channels);

                // Validate that the frame format matches what we configured the source for
                if (sampleRate != configuredSampleRate)
                {
                    Utils.Error($"Sample rate mismatch! Expected {configuredSampleRate}Hz, got {sampleRate}Hz. " +
                              "Audio data must be resampled to Unity's format before sending to LiveKit.");
                    return;
                }

                if (channels != configuredChannels)
                {
                    Utils.Error($"Channel count mismatch! Expected {configuredChannels} channels, got {channels} channels.");
                    return;
                }

                unsafe
                {
                    fixed (short* dataPtr = audioData)
                    {
                        using var request = FFIBridge.Instance.NewRequest<CaptureAudioFrameRequest>();
                        using var audioFrameBufferInfo = request.TempResource<AudioFrameBufferInfo>();

                        var captureFrame = request.request;
                        captureFrame.SourceHandle = (ulong)handle.DangerousGetHandle();
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
                
                handle?.Dispose();
                disposed = true;
            }
        }

        ~RtcAudioSource()
        {
            Dispose(false);
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
    }
}

