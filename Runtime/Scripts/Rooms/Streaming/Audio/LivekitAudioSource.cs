using System;
using LiveKit.Audio;
using LiveKit.Internal;
using RichTypes;
using UnityEngine;

namespace LiveKit.Rooms.Streaming.Audio
{
    public enum ILDMode
    {
        None,
        EqualPower,
        HeadShadow
    }

    public class LivekitAudioSource : MonoBehaviour
    {
        private static ulong counter;

        private int sampleRate;
        private Weak<AudioStream> stream = Weak<AudioStream>.Null;
        private AudioSource audioSource = null!;

        [Header("ILD — Interaural Level Difference")]
        [Tooltip("None = stereo passthrough. EqualPower = volume-only pan (L/R gain). HeadShadow = EqualPower + low-pass filter on the far ear (head blocks high frequencies).")]
        public ILDMode ildMode = ILDMode.None;
        [Tooltip("How strongly the volume shifts between left and right ear. 0 = no panning (center), 1 = full panning.")]
        [Range(0f, 1f)] public float ildStrength = 1f;
        [Tooltip("Minimum cutoff frequency (Hz) of the low-pass filter on the far ear at 90° azimuth. Lower = more muffled sound behind the head. Only used in HeadShadow mode.")]
        [Range(500f, 4000f)] public float shadowCutoffHz = 1500f;
        [Tooltip("Strength of the head shadow effect. 0 = no filtering, 1 = full low-pass on the far ear. Only used in HeadShadow mode.")]
        [Range(0f, 1f)] public float shadowStrength = 0.7f;

        [Header("ITD — Interaural Time Difference")]
        [Tooltip("Enable time delay on the far ear. The brain uses this delay (~0.6 ms max) to localize sounds at low frequencies. Works independently of ILD.")]
        public bool enableITD;
        [Tooltip("Radius of the listener's head in meters. Determines the maximum interaural delay. Average human head ≈ 0.0875 m.")]
        [Range(0.05f, 0.15f)] public float headRadius = 0.0875f;

        [Header("HRTF — Pinna / Spectral Cues")]
        [Tooltip("Enable pinna (ear shape) simulation via notch filters. Provides vertical localization cues (up/down) that ILD and ITD cannot. Not yet implemented.")]
        public bool enableHRTF;
        [Tooltip("How much the elevation angle affects the pinna notch filter frequency. 0 = no vertical cues, 1 = full elevation-dependent filtering.")]
        [Range(0f, 1f)] public float elevationInfluence = 0.5f;

        private float azimuthAngle;
        private float elevationAngle;

        private const float SPEED_OF_SOUND = 343f;
        private const int DELAY_BUFFER_SIZE = 256;
        private const int DELAY_BUFFER_MASK = DELAY_BUFFER_SIZE - 1;

        private float[] delayBuffer;
        private int delayWritePos;

        private float lpfStateL;
        private float lpfStateR;

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
                source.ildMode = ILDMode.EqualPower;

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

                bool spatialized = ildMode != ILDMode.None || enableITD || enableHRTF;
                if (spatialized && channels >= 2)
                    ApplySpatializationPipeline(data, channels);

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

        private void ApplySpatializationPipeline(float[] data, int channels)
        {
            float az = azimuthAngle;
            float el = elevationAngle;
            int samplesPerChannel = data.Length / channels;

            // Pre-compute ILD gains
            float gainL = 1f;
            float gainR = 1f;
            if (ildMode != ILDMode.None)
            {
                float pan = Mathf.Sin(az) * Mathf.Cos(el) * ildStrength;
                float p = (pan + 1f) * 0.5f;
                gainL = Mathf.Cos(p * Mathf.PI * 0.5f);
                gainR = Mathf.Sin(p * Mathf.PI * 0.5f);
            }

            // Pre-compute ITD delays
            float delayL = 0f;
            float delayR = 0f;
            if (enableITD)
            {
                float effectiveAz = Mathf.Min(Mathf.Abs(az), Mathf.PI * 0.5f) * Mathf.Cos(el);
                float itdSamples = headRadius * (effectiveAz + Mathf.Sin(effectiveAz)) / SPEED_OF_SOUND * sampleRate;

                if (az >= 0f)
                    delayL = itdSamples;
                else
                    delayR = itdSamples;

                if (delayBuffer == null)
                    delayBuffer = new float[DELAY_BUFFER_SIZE];
            }

            // Pre-compute HeadShadow LPF coefficient
            float lpfAlpha = 0f;
            bool shadowOnLeft = false;
            if (ildMode == ILDMode.HeadShadow)
            {
                float shadowAmount = Mathf.Abs(Mathf.Sin(az)) * shadowStrength * Mathf.Cos(el);
                float cutoff = Mathf.Lerp(20000f, shadowCutoffHz, shadowAmount);
                lpfAlpha = 1f / (1f + sampleRate / (2f * Mathf.PI * cutoff));
                shadowOnLeft = az >= 0f;
            }

            for (int i = 0; i < samplesPerChannel; i++)
            {
                float mono = data[i * channels];
                float sampleL = mono;
                float sampleR = mono;

                // Stage 1: ITD — delay the contralateral ear
                if (enableITD)
                {
                    delayBuffer[delayWritePos & DELAY_BUFFER_MASK] = mono;
                    sampleL = ReadDelayed(delayL);
                    sampleR = ReadDelayed(delayR);
                    delayWritePos++;
                }

                // Stage 2: ILD — apply level difference
                sampleL *= gainL;
                sampleR *= gainR;

                // Stage 3: HeadShadow — one-pole LPF on contralateral ear
                if (ildMode == ILDMode.HeadShadow)
                {
                    if (shadowOnLeft)
                    {
                        lpfStateL += lpfAlpha * (sampleL - lpfStateL);
                        sampleL = lpfStateL;
                    }
                    else
                    {
                        lpfStateR += lpfAlpha * (sampleR - lpfStateR);
                        sampleR = lpfStateR;
                    }
                }

                int offset = i * channels;
                data[offset] = sampleL;
                data[offset + 1] = sampleR;

                // Fallback for surround formats (quad, 5.1, 7.1): fill remaining channels with mono, no panning
                for (int ch = 2; ch < channels; ch++)
                    data[offset + ch] = mono;
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
