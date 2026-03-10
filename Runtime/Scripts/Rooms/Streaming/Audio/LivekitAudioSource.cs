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

    public enum ShadowFilterOrder
    {
        OnePole6dB,
        TwoPole12dB,
        ThreePole18dB,
        FourPole24dB,
        Biquad12dB,
        MultiBand3
    }

    public class LivekitAudioSource : MonoBehaviour
    {
        private static ulong counter;

        private int sampleRate;
        private Weak<AudioStream> stream = Weak<AudioStream>.Null;
        private AudioSource audioSource = null!;

        [Header("ILD — Interaural Level Difference")]
        [Tooltip("None = stereo passthrough (no spatialization).\n" +
                 "EqualPower = volume-only L/R pan via equal-power law (cos/sin gains).\n" +
                 "HeadShadow = EqualPower + frequency-dependent low-pass filter on the far ear, " +
                 "simulating the physical head blocking high frequencies (head shadow effect).\n\n" +
                 "Ref: Van Wanrooij & Van Opstal (2004), J.Neurosci. — head shadow is the dominant ILD cue above 1.5 kHz.")]
        public ILDMode ildMode = ILDMode.None;
        [Tooltip("How strongly the volume shifts between ears. 0 = center (no pan), 1 = full stereo panning.\n\n" +
                 "Measured ILD at 90° azimuth: ~5 dB at 1 kHz, ~15 dB at 4 kHz, up to ~20 dB at 8 kHz.")]
        [Range(0f, 1f)] public float ildStrength = 1f;

        [Header("Head Shadow Filter (HeadShadow mode only)")]
        [Tooltip("LPF order for the far (contralateral) ear. Real head shadow slope is ~8–10 dB/octave.\n\n" +
                 "OnePole (6 dB/oct) — subtle, closest to gentle real-world diffraction.\n" +
                 "TwoPole (12 dB/oct) — DEFAULT, best match to measured head shadow slope.\n" +
                 "ThreePole (18 dB/oct) — exaggerated, useful for artistic emphasis.\n" +
                 "FourPole (24 dB/oct) — very aggressive, 'phone in a pillow' effect.\n" +
                 "Biquad (12 dB/oct + Q) — same slope as TwoPole but with adjustable resonance peak.\n" +
                 "MultiBand3 — 3-band crossover with independent per-band gain (dB). " +
                 "Most accurate: matches measured head shadow curve (<500 Hz: -2 dB, 1-2 kHz: -10 dB, >2 kHz: -20 dB).\n\n" +
                 "Ref: Blauert (1997) — ~5 dB ILD at 1 kHz → ~20 dB at 8 kHz for 90° azimuth source.")]
        public ShadowFilterOrder shadowFilterOrder = ShadowFilterOrder.TwoPole12dB;
        [Tooltip("Cutoff frequency (Hz) of the LPF applied to the far ear at maximum angle (90° azimuth).\n" +
                 "Physical basis: head diameter ~17.5 cm ≈ wavelength at ~2 kHz. Shadow becomes significant above ~1–1.5 kHz.\n\n" +
                 "1500 Hz = physically realistic (default). Lower values exaggerate the effect for voice (fundamentals 100–300 Hz, formants 300–3000 Hz).")]
        [Range(200f, 4000f)] public float shadowCutoffHz = 1500f;
        [Tooltip("Blend of the head shadow effect. 0 = no filtering (cutoff stays at 20 kHz), 1 = full filtering down to shadowCutoffHz.\n" +
                 "At intermediate values, the effective cutoff = lerp(20000, shadowCutoffHz, strength × |sin(azimuth)|).")]
        [Range(0f, 1f)] public float shadowStrength = 1f;
        [Tooltip("Resonance (Q factor) for Biquad12dB mode only.\n" +
                 "0.707 = Butterworth (maximally flat passband, no resonance peak).\n" +
                 "Values > 1 add a resonant peak at cutoff, making the transition sharper and more 'colored'.\n\n" +
                 "Ref: Robert Bristow-Johnson, Audio EQ Cookbook.")]
        [Range(0.5f, 3f)] public float biquadQ = 0.707f;

        [Header("MultiBand3 Crossover (MultiBand3 mode only)")]
        [Tooltip("Crossover frequency between Low and Mid bands (Hz).\n" +
                 "Below this frequency: sound passes almost unchanged (head is transparent to long wavelengths).\n" +
                 "500 Hz default — head diameter ~17.5 cm is much smaller than wavelength at 500 Hz (~69 cm).\n\n" +
                 "Ref: Head shadow becomes measurable above ~500 Hz (Blauert, 1997).")]
        [Range(200f, 2000f)] public float crossoverLowMid = 500f;
        [Tooltip("Crossover frequency between Mid and High bands (Hz).\n" +
                 "Above this frequency: strong attenuation — head fully blocks short wavelengths.\n" +
                 "2000 Hz default — wavelength ~17 cm ≈ head diameter, strong shadow onset.\n\n" +
                 "Ref: ILD exceeds 10 dB above 2 kHz for 90° sources (Blauert, 1997).")]
        [Range(1000f, 8000f)] public float crossoverMidHigh = 2000f;
        [Tooltip("Attenuation of the Low band on the far ear (dB). Negative = quieter.\n" +
                 "-2 dB default — matches measured <500 Hz: ~0-2 dB shadow at 90° azimuth.\n" +
                 "Low frequencies diffract around the head easily, so minimal attenuation.")]
        [Range(-30f, 0f)] public float lowBandDb = -2f;
        [Tooltip("Attenuation of the Mid band on the far ear (dB). Negative = quieter.\n" +
                 "-10 dB default — matches measured 1-2 kHz: ~5-10 dB shadow at 90° azimuth.\n" +
                 "This range contains voice formants (300-3000 Hz), so changes here are very audible.")]
        [Range(-30f, 0f)] public float midBandDb = -10f;
        [Tooltip("Attenuation of the High band on the far ear (dB). Negative = quieter.\n" +
                 "-20 dB default — matches measured >4 kHz: ~15-20 dB shadow at 90° azimuth.\n" +
                 "High frequencies are strongly blocked by the head; this is the most noticeable band.")]
        [Range(-30f, 0f)] public float highBandDb = -20f;

        [Header("ITD — Interaural Time Difference")]
        [Tooltip("Enable interaural time delay on the far ear.\n" +
                 "The brain uses ITD (~0.6 ms max) to localize sounds at low frequencies (<1.5 kHz).\n" +
                 "Uses the Woodworth spherical-head model with linear-interpolated delay line.\n" +
                 "Works independently of ILD — can be combined with any ILD mode.\n\n" +
                 "Ref: Woodworth & Schlosberg (1954) — ITD = r(θ + sin θ)/c for spherical head.")]
        public bool enableITD;
        [Tooltip("Radius of the listener's head in meters. Determines max interaural delay.\n" +
                 "Average adult human head radius ≈ 0.0875 m (head width ~17.5 cm).\n" +
                 "Max ITD at this radius: ~0.65 ms ≈ 31 samples at 48 kHz.\n\n" +
                 "Ref: Algazi et al. (2001) — mean head radius 0.0875 m (CIPIC database).")]
        [Range(0.05f, 0.15f)] public float headRadius = 0.0875f;

        [Header("HRTF — Pinna / Spectral Cues")]
        [Tooltip("Enable pinna (ear shape) simulation via elevation-dependent notch filters.\n" +
                 "Provides the only cue for vertical localization (up/down) and front/back disambiguation.\n" +
                 "ILD and ITD alone cannot resolve the 'cone of confusion'.\n\n" +
                 "NOT YET IMPLEMENTED — planned for Iteration C.")]
        public bool enableHRTF;
        [Tooltip("How much the elevation angle affects the pinna notch frequency.\n" +
                 "0 = no vertical cues, 1 = full elevation-dependent filtering.\n" +
                 "Real pinna notches shift ~6–10 kHz depending on elevation.\n\n" +
                 "Ref: Hebrank & Wright (1974) — pinna spectral cues for vertical localization.")]
        [Range(0f, 1f)] public float elevationInfluence = 0.5f;

        private float azimuthAngle;
        private float elevationAngle;

        private const float SPEED_OF_SOUND = 343f;
        private const int DELAY_BUFFER_SIZE = 256;
        private const int DELAY_BUFFER_MASK = DELAY_BUFFER_SIZE - 1;

        private float[] delayBuffer;
        private int delayWritePos;

        // Cascade one-pole filter states (up to 4 poles per ear)
        private float lpfS1L, lpfS2L, lpfS3L, lpfS4L;
        private float lpfS1R, lpfS2R, lpfS3R, lpfS4R;

        // Biquad filter states (Direct Form II Transposed, per ear)
        private float bqZ1L, bqZ2L;
        private float bqZ1R, bqZ2R;

        // MultiBand3 biquad states (LPF + HPF per ear, Direct Form II Transposed)
        private float mbLpfZ1L, mbLpfZ2L, mbHpfZ1L, mbHpfZ2L;
        private float mbLpfZ1R, mbLpfZ2R, mbHpfZ1R, mbHpfZ2R;

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

            // Pre-compute HeadShadow filter coefficients
            bool headShadow = ildMode == ILDMode.HeadShadow;
            bool useBiquad = headShadow && shadowFilterOrder == ShadowFilterOrder.Biquad12dB;
            bool useMultiBand = headShadow && shadowFilterOrder == ShadowFilterOrder.MultiBand3;
            float lpfAlpha = 0f;
            float bqB0 = 0f, bqB1 = 0f, bqB2 = 0f, bqA1 = 0f, bqA2 = 0f;
            // MultiBand3 coefficients (LPF at crossoverLowMid, HPF at crossoverMidHigh)
            float mbLB0 = 0f, mbLB1 = 0f, mbLB2 = 0f, mbLA1 = 0f, mbLA2 = 0f;
            float mbHB0 = 0f, mbHB1 = 0f, mbHB2 = 0f, mbHA1 = 0f, mbHA2 = 0f;
            float mbGainLow = 1f, mbGainMid = 1f, mbGainHigh = 1f;
            bool shadowOnLeft = false;

            if (headShadow)
            {
                float shadowAmount = Mathf.Abs(Mathf.Sin(az)) * shadowStrength * Mathf.Cos(el);
                shadowOnLeft = az >= 0f;

                if (useMultiBand)
                {
                    // Per-band gains lerped by shadowAmount: 0 dB at front → configured dB at 90°
                    mbGainLow = Mathf.Pow(10f, lowBandDb * shadowAmount / 20f);
                    mbGainMid = Mathf.Pow(10f, midBandDb * shadowAmount / 20f);
                    mbGainHigh = Mathf.Pow(10f, highBandDb * shadowAmount / 20f);

                    const float butterQ = 0.707f;

                    // LPF biquad at crossoverLowMid
                    float w0L = 2f * Mathf.PI * crossoverLowMid / sampleRate;
                    float cosL = Mathf.Cos(w0L);
                    float sinL = Mathf.Sin(w0L);
                    float alphaL = sinL / (2f * butterQ);
                    float a0InvL = 1f / (1f + alphaL);
                    mbLB0 = (1f - cosL) * 0.5f * a0InvL;
                    mbLB1 = (1f - cosL) * a0InvL;
                    mbLB2 = mbLB0;
                    mbLA1 = -2f * cosL * a0InvL;
                    mbLA2 = (1f - alphaL) * a0InvL;

                    // HPF biquad at crossoverMidHigh
                    float w0H = 2f * Mathf.PI * crossoverMidHigh / sampleRate;
                    float cosH = Mathf.Cos(w0H);
                    float sinH = Mathf.Sin(w0H);
                    float alphaH = sinH / (2f * butterQ);
                    float a0InvH = 1f / (1f + alphaH);
                    mbHB0 = (1f + cosH) * 0.5f * a0InvH;
                    mbHB1 = -(1f + cosH) * a0InvH;
                    mbHB2 = mbHB0;
                    mbHA1 = -2f * cosH * a0InvH;
                    mbHA2 = (1f - alphaH) * a0InvH;
                }
                else if (useBiquad)
                {
                    float cutoff = Mathf.Lerp(20000f, shadowCutoffHz, shadowAmount);
                    float w0 = 2f * Mathf.PI * cutoff / sampleRate;
                    float cosW0 = Mathf.Cos(w0);
                    float sinW0 = Mathf.Sin(w0);
                    float alphaBq = sinW0 / (2f * biquadQ);
                    float a0Inv = 1f / (1f + alphaBq);

                    bqB0 = (1f - cosW0) * 0.5f * a0Inv;
                    bqB1 = (1f - cosW0) * a0Inv;
                    bqB2 = bqB0;
                    bqA1 = -2f * cosW0 * a0Inv;
                    bqA2 = (1f - alphaBq) * a0Inv;
                }
                else
                {
                    float cutoff = Mathf.Lerp(20000f, shadowCutoffHz, shadowAmount);
                    lpfAlpha = 1f / (1f + sampleRate / (2f * Mathf.PI * cutoff));
                }
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

                // Stage 3: HeadShadow — filter on contralateral ear
                if (headShadow)
                {
                    if (useMultiBand)
                    {
                        if (shadowOnLeft)
                            sampleL = ApplyMultiBandFilter(sampleL, mbLB0, mbLB1, mbLB2, mbLA1, mbLA2,
                                mbHB0, mbHB1, mbHB2, mbHA1, mbHA2, mbGainLow, mbGainMid, mbGainHigh, true);
                        else
                            sampleR = ApplyMultiBandFilter(sampleR, mbLB0, mbLB1, mbLB2, mbLA1, mbLA2,
                                mbHB0, mbHB1, mbHB2, mbHA1, mbHA2, mbGainLow, mbGainMid, mbGainHigh, false);
                    }
                    else
                    {
                        if (shadowOnLeft)
                            sampleL = ApplyShadowFilter(sampleL, useBiquad, lpfAlpha, bqB0, bqB1, bqB2, bqA1, bqA2, true);
                        else
                            sampleR = ApplyShadowFilter(sampleR, useBiquad, lpfAlpha, bqB0, bqB1, bqB2, bqA1, bqA2, false);
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

        private float ApplyShadowFilter(float input, bool useBiquad,
            float alpha, float b0, float b1, float b2, float a1, float a2, bool isLeft)
        {
            if (useBiquad)
            {
                // Biquad LPF — Direct Form II Transposed
                if (isLeft)
                {
                    float y = b0 * input + bqZ1L;
                    bqZ1L = b1 * input - a1 * y + bqZ2L;
                    bqZ2L = b2 * input - a2 * y;
                    return y;
                }
                else
                {
                    float y = b0 * input + bqZ1R;
                    bqZ1R = b1 * input - a1 * y + bqZ2R;
                    bqZ2R = b2 * input - a2 * y;
                    return y;
                }
            }

            // Cascade one-pole LPF — each pole adds 6 dB/octave rolloff
            if (isLeft)
            {
                lpfS1L += alpha * (input - lpfS1L);
                float s = lpfS1L;
                if (shadowFilterOrder >= ShadowFilterOrder.TwoPole12dB) { lpfS2L += alpha * (s - lpfS2L); s = lpfS2L; }
                if (shadowFilterOrder >= ShadowFilterOrder.ThreePole18dB) { lpfS3L += alpha * (s - lpfS3L); s = lpfS3L; }
                if (shadowFilterOrder >= ShadowFilterOrder.FourPole24dB) { lpfS4L += alpha * (s - lpfS4L); s = lpfS4L; }
                return s;
            }
            else
            {
                lpfS1R += alpha * (input - lpfS1R);
                float s = lpfS1R;
                if (shadowFilterOrder >= ShadowFilterOrder.TwoPole12dB) { lpfS2R += alpha * (s - lpfS2R); s = lpfS2R; }
                if (shadowFilterOrder >= ShadowFilterOrder.ThreePole18dB) { lpfS3R += alpha * (s - lpfS3R); s = lpfS3R; }
                if (shadowFilterOrder >= ShadowFilterOrder.FourPole24dB) { lpfS4R += alpha * (s - lpfS4R); s = lpfS4R; }
                return s;
            }
        }

        private float ApplyMultiBandFilter(float input,
            float lb0, float lb1, float lb2, float la1, float la2,
            float hb0, float hb1, float hb2, float ha1, float ha2,
            float gLow, float gMid, float gHigh, bool isLeft)
        {
            float low, high;

            if (isLeft)
            {
                low = lb0 * input + mbLpfZ1L;
                mbLpfZ1L = lb1 * input - la1 * low + mbLpfZ2L;
                mbLpfZ2L = lb2 * input - la2 * low;

                high = hb0 * input + mbHpfZ1L;
                mbHpfZ1L = hb1 * input - ha1 * high + mbHpfZ2L;
                mbHpfZ2L = hb2 * input - ha2 * high;
            }
            else
            {
                low = lb0 * input + mbLpfZ1R;
                mbLpfZ1R = lb1 * input - la1 * low + mbLpfZ2R;
                mbLpfZ2R = lb2 * input - la2 * low;

                high = hb0 * input + mbHpfZ1R;
                mbHpfZ1R = hb1 * input - ha1 * high + mbHpfZ2R;
                mbHpfZ2R = hb2 * input - ha2 * high;
            }

            float mid = input - low - high;
            return low * gLow + mid * gMid + high * gHigh;
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
