using System;
using LiveKit.Audio;
using LiveKit.Internal;
using RichTypes;
using UnityEngine;

namespace LiveKit.Rooms.Streaming.Audio
{
    public enum SpatializationMode
    {
        None,
        ILD,
        ITD,
        ITD_ILD,
        ParametricHRTF
    }

    public class LivekitAudioSource : MonoBehaviour
    {
        private static ulong counter;

        private int sampleRate;
        private Weak<AudioStream> stream = Weak<AudioStream>.Null;
        private AudioSource audioSource = null!;

        [Header("Spatialization")]
        public SpatializationMode spatializationMode = SpatializationMode.None;

        [Header("ILD — Interaural Level Difference")]
        [Range(0f, 1f)] public float ildStrength = 1f;

        [Header("ITD — Interaural Time Difference")]
        [Range(0.05f, 0.15f)] public float headRadius = 0.0875f;

        [Header("Parametric HRTF — Head Shadow")]
        [Range(500f, 4000f)] public float shadowCutoffHz = 1500f;
        [Range(0f, 1f)] public float shadowStrength = 0.7f;
        [Range(0f, 1f)] public float elevationInfluence = 0.5f;

        private float azimuthAngle;
        private float elevationAngle;

        private const float SPEED_OF_SOUND = 343f;
        private const int DELAY_BUFFER_SIZE = 256;
        private const int DELAY_BUFFER_MASK = DELAY_BUFFER_SIZE - 1;

        private float[] delayBuffer;
        private int delayWritePos;

        private WavWriter? wavWriter;
        private PCMSample[] wavBuffer = Array.Empty<PCMSample>();

        public bool IsWavActive => wavWriter.HasValue;

        /// <summary>
        /// Set the 3D angles from the main thread. Read on the audio thread.
        /// </summary>
        /// <param name="azimuth">Horizontal angle in radians (-PI..+PI). 0 = front, +PI/2 = right.</param>
        /// <param name="elevation">Vertical angle in radians (-PI/2..+PI/2). 0 = level, +PI/2 = above.</param>
        public void SetSpatialAngles(float azimuth, float elevation)
        {
            azimuthAngle = azimuth;
            elevationAngle = elevation;
        }

        public static LivekitAudioSource New(bool explicitName = false, bool mono = false)
        {
            var gm = new GameObject();
            var audioSource = gm.AddComponent<AudioSource>();
            var source = gm.AddComponent<LivekitAudioSource>();
            source.audioSource = audioSource;

            if (mono)
                source.spatializationMode = SpatializationMode.ILD;

            if (explicitName) source.name = $"{nameof(LivekitAudioSource)}_{counter++}";
            return source;
        }

        public void Construct(Weak<AudioStream> audioStream)
        {
            stream = audioStream;
        }

        public void Free()
        {
            stream = Weak<AudioStream>.Null;
            DisposeWavWriter();
        }

        private void DisposeWavWriter()
        {
            if (wavWriter.HasValue && wavWriter.Value.IsDisposed() == false)
            {
                wavWriter.Value.Dispose();
                wavWriter = null;
            }
        }

        private void OnDestroy()
        {
            DisposeWavWriter();
        }

        public void Play()
        {
            audioSource.Play();
        }

        public void Stop()
        {
            audioSource.Stop();
        }

        public void SetVolume(float target)
        {
            audioSource.volume = target;
        }

        public Result ToggleRecordWavOutput()
        {
            if (wavWriter.HasValue)
            {
                DisposeWavWriter();
                return Result.SuccessResult();
            }

            return StartRecordWavOutput();
        }

        public Result StartRecordWavOutput()
        {
            if (wavWriter != null)
            {
                return Result.ErrorResult("Already recording");
            }

            string path = StreamKeyUtils.NewPersistentFilePathByName($"livekit_audio_source_hz{sampleRate}");
            Result<WavWriter> writerResult = WavWriter.NewFromPath(path);
            if (writerResult.Success == false)
            {
                return writerResult;
            }

            wavWriter = writerResult.Value;
            return Result.SuccessResult();
        }

        private void OnEnable()
        {
            OnAudioConfigurationChanged(false);
            AudioSettings.OnAudioConfigurationChanged += OnAudioConfigurationChanged;
        }

        private void OnDisable()
        {
            AudioSettings.OnAudioConfigurationChanged -= OnAudioConfigurationChanged;
        }

        private void OnAudioConfigurationChanged(bool deviceWasChanged)
        {
            sampleRate = AudioSettings.outputSampleRate;

            // Enable recording with different sample_rate
            if (wavWriter.HasValue)
            {
                DisposeWavWriter();
                Result result = StartRecordWavOutput();
                if (result.Success == false)
                {
                    Utils.Error($"Cannot restart wav recording for output: {result.ErrorMessage}");
                }
            }
        }

        // Called by Unity on the Audio thread
        private void OnAudioFilterRead(float[] data, int channels)
        {
            Option<AudioStream> resource = stream.Resource;
            if (resource.Has)
            {
                resource.Value.ReadAudio(data.AsSpan(), channels, sampleRate);

                if (spatializationMode != SpatializationMode.None && channels >= 2)
                {
                    int samplesPerChannel = data.Length / channels;

                    switch (spatializationMode)
                    {
                        case SpatializationMode.ILD:
                            ApplyILD(data, channels, samplesPerChannel);
                            break;
                        case SpatializationMode.ITD:
                            ApplyITD(data, channels, samplesPerChannel, false);
                            break;
                        case SpatializationMode.ITD_ILD:
                            ApplyITD(data, channels, samplesPerChannel, true);
                            break;
                    }
                }

                if (wavWriter.HasValue)
                {
                    if (data.Length != wavBuffer.Length)
                    {
                        wavBuffer = new PCMSample[data.Length];
                    }

                    // TODO SIMD
                    for (var i = 0; i < data.Length; i++)
                    {
                        wavBuffer[i] = PCMSample.FromUnitySample(data[i]);
                    }

                    WavWriter writer = wavWriter.Value;
                    writer.Write(wavBuffer, (uint)channels, (uint)sampleRate);
                    wavWriter = writer;
                }
            }
        }

        private void ApplyILD(float[] data, int channels, int samplesPerChannel)
        {
            float az = azimuthAngle;
            float el = elevationAngle;

            float pan = Mathf.Sin(az) * Mathf.Cos(el) * ildStrength;

            float p = (pan + 1f) * 0.5f;
            float gainL = Mathf.Cos(p * Mathf.PI * 0.5f);
            float gainR = Mathf.Sin(p * Mathf.PI * 0.5f);

            for (int i = 0; i < samplesPerChannel; i++)
            {
                float sample = data[i * channels];
                int offset = i * channels;
                data[offset] = sample * gainL;
                data[offset + 1] = sample * gainR;

                // Fallback for surround formats (quad, 5.1, 7.1): fill remaining channels with mono, no panning
                for (int ch = 2; ch < channels; ch++)
                    data[offset + ch] = sample;
            }
        }

        /// <param name="withILD">When true, applies ILD gains on top of the ITD delay (ITD_ILD mode).</param>
        private void ApplyITD(float[] data, int channels, int samplesPerChannel, bool withILD)
        {
            float az = azimuthAngle;
            float el = elevationAngle;

            // Reduce effective azimuth by elevation: source directly above → no ITD
            float effectiveAz = Mathf.Min(Mathf.Abs(az), Mathf.PI * 0.5f) * Mathf.Cos(el);

            // Woodworth spherical-head model: ITD in seconds
            float itdSeconds = headRadius * (effectiveAz + Mathf.Sin(effectiveAz)) / SPEED_OF_SOUND;
            float itdSamples = itdSeconds * sampleRate;

            // Source to the right (az > 0) → contralateral (left) ear is delayed
            float delayL, delayR;
            if (az >= 0f)
            {
                delayL = itdSamples;
                delayR = 0f;
            }
            else
            {
                delayL = 0f;
                delayR = itdSamples;
            }

            float gainL = 1f;
            float gainR = 1f;
            if (withILD)
            {
                float pan = Mathf.Sin(az) * Mathf.Cos(el) * ildStrength;
                float p = (pan + 1f) * 0.5f;
                gainL = Mathf.Cos(p * Mathf.PI * 0.5f);
                gainR = Mathf.Sin(p * Mathf.PI * 0.5f);
            }

            if (delayBuffer == null)
                delayBuffer = new float[DELAY_BUFFER_SIZE];

            for (int i = 0; i < samplesPerChannel; i++)
            {
                float sample = data[i * channels];

                delayBuffer[delayWritePos & DELAY_BUFFER_MASK] = sample;

                float sampleL = ReadDelayed(delayL);
                float sampleR = ReadDelayed(delayR);

                int offset = i * channels;
                data[offset] = sampleL * gainL;
                data[offset + 1] = sampleR * gainR;

                // Fallback for surround formats (quad, 5.1, 7.1): fill remaining channels with mono, no panning
                for (int ch = 2; ch < channels; ch++)
                    data[offset + ch] = sample;

                delayWritePos++;
            }
        }

        /// <summary>
        /// Read from the circular delay buffer with fractional-sample linear interpolation.
        /// Must be called after writing the current sample to delayBuffer[delayWritePos].
        /// </summary>
        private float ReadDelayed(float delaySamples)
        {
            float readPos = delayWritePos - delaySamples;
            int idx = (int)readPos;
            float frac = readPos - idx;

            return delayBuffer[idx & DELAY_BUFFER_MASK] * (1f - frac)
                 + delayBuffer[(idx + 1) & DELAY_BUFFER_MASK] * frac;
        }
    }
}
