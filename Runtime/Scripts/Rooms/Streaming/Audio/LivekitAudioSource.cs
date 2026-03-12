using System;
using LiveKit.Audio;
using LiveKit.Internal;
using RichTypes;
using Unity.Profiling;
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
        private static readonly ProfilerMarker s_MarkerSpatial = new("LiveKit.Spatial");
        private static readonly ProfilerMarker s_MarkerITD = new("LiveKit.Spatial.ITD");
        private static readonly ProfilerMarker s_MarkerILD = new("LiveKit.Spatial.ILD");
        private static readonly ProfilerMarker s_MarkerHeadShadow = new("LiveKit.Spatial.HeadShadow");
        private static readonly ProfilerMarker s_MarkerHRTF = new("LiveKit.Spatial.HRTF");

        private static ulong counter;

        private int sampleRate;
        private Weak<AudioStream> stream = Weak<AudioStream>.Null;
        private AudioSource audioSource = null!;

        [Header("Bypass")]
        [Tooltip("Skip all spatialization — raw stereo from LiveKit, identical to the old audio flow.\n" +
                 "Use for A/B quality comparison.")]
        public bool bypassSpatialization;

        [Header("ILD — Interaural Level Difference")]
        [Tooltip("None = stereo passthrough (no spatialization).\n" +
                 "EqualPower = volume-only L/R pan via equal-power law (cos/sin gains).\n" +
                 "HeadShadow = EqualPower + frequency-dependent low-pass filter on the far ear, " +
                 "simulating the physical head blocking high frequencies (head shadow effect).\n\n" +
                 "Ref: Van Wanrooij & Van Opstal (2004), J.Neurosci. — head shadow is the dominant ILD cue above 1.5 kHz.")]
        public ILDMode ildMode = ILDMode.HeadShadow;
        [Tooltip("How strongly the volume shifts between ears. 0 = center (no pan), 1 = full stereo panning.\n\n" +
                 "Measured ILD at 90° azimuth: ~5 dB at 1 kHz, ~15 dB at 4 kHz, up to ~20 dB at 8 kHz.")]
        [Range(0f, 1f)] public float ildStrength = 0f;

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
        public ShadowFilterOrder shadowFilterOrder = ShadowFilterOrder.MultiBand3;
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
        public bool enableITD = true;
        [Tooltip("Radius of the listener's head in meters. Determines max interaural delay.\n" +
                 "Average adult human head radius ≈ 0.0875 m (head width ~17.5 cm).\n" +
                 "Max ITD at this radius: ~0.65 ms ≈ 31 samples at 48 kHz.\n\n" +
                 "Ref: Algazi et al. (2001) — mean head radius 0.0875 m (CIPIC database).")]
        [Range(0.05f, 0.15f)] public float headRadius = 0.05f;

        [Header("HRTF — Pinna / Spectral Cues")]
        [Tooltip("Enable pinna (ear shape) simulation via elevation-dependent notch filters.\n" +
                 "Provides the only cue for vertical localization (up/down) and front/back disambiguation.\n" +
                 "ILD and ITD alone cannot resolve the 'cone of confusion' — a cone of directions with identical ILD/ITD.\n\n" +
                 "Primary notch: biquad peaking EQ at pinnaNotchFreq, shifted by elevation.\n" +
                 "Secondary notch: at pinnaSecondaryRatio × primary freq (set pinnaSecondaryStrength > 0 to enable).\n\n" +
                 "Ref: Hebrank & Wright (1974) — pinna spectral cues at 6-10 kHz for vertical localization.")]
        public bool enableHRTF = true;
        [Tooltip("How much the elevation angle shifts the notch frequency.\n" +
                 "0 = notch at fixed pinnaNotchFreq (no vertical cues), 1 = full shift (±40% of base freq).\n" +
                 "Positive elevation (above) shifts notch up, negative (below) shifts down.\n\n" +
                 "Ref: Hebrank & Wright (1974) — pinna notch frequency varies ~6-10 kHz with elevation.")]
        [Range(0f, 1f)] public float elevationInfluence = 0.5f;
        [Tooltip("Base notch frequency at elevation = 0° (horizontal plane).\n" +
                 "Pinna resonance creates a spectral notch typically at ~6-8 kHz.\n" +
                 "7000 Hz default — center of typical pinna notch range.\n\n" +
                 "Ref: Shaw (1997), Lopez-Poveda & Meddis (1996).")]
        [Range(4000f, 12000f)] public float pinnaNotchFreq = 7000f;
        [Tooltip("Q factor (narrowness) of the notch filter. Higher Q = narrower, deeper notch.\n" +
                 "3-5 is typical for pinna notches. Too narrow may be inaudible on speech.\n\n" +
                 "4 default — good balance of perceptibility and naturalness.")]
        [Range(1f, 10f)] public float pinnaNotchQ = 4f;
        [Tooltip("Depth of the primary pinna notch in dB. Negative = quieter at notch frequency.\n" +
                 "-9 dB default — matches typical measured pinna notch depth.\n" +
                 "Realistic range: -6 to -15 dB.\n\n" +
                 "Ref: Lopez-Poveda & Meddis (1996) — measured pinna notch depths 6-15 dB.")]
        [Range(-20f, 0f)] public float pinnaNotchDepthDb = -9f;

        [Header("HRTF — Secondary Notch (C2)")]
        [Tooltip("Frequency ratio of secondary notch relative to primary.\n" +
                 "1.6× default — second harmonic of pinna cavity resonance.\n" +
                 "Set pinnaSecondaryStrength to 0 to disable (fallback to single-notch C1 mode).\n\n" +
                 "Ref: Lopez-Poveda & Meddis (1996) — multiple pinna notches at harmonic ratios.")]
        [Range(1.2f, 2.5f)] public float pinnaSecondaryRatio = 1.6f;
        [Tooltip("Depth of secondary notch relative to primary. 0 = disabled (C1 only), 1 = same depth as primary.\n" +
                 "0.6 default — secondary is typically shallower.\n\n" +
                 "Set to 0 to compare single-notch (C1) vs dual-notch (C2) in real time.")]
        [Range(0f, 1f)] public float pinnaSecondaryStrength = 0f;

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

        // HRTF primary pinna notch states (peaking EQ biquad, both ears)
        private float hrfZ1L, hrfZ2L, hrfZ1R, hrfZ2R;

        // HRTF secondary pinna notch states (peaking EQ biquad, both ears)
        private float hrf2Z1L, hrf2Z2L, hrf2Z1R, hrf2Z2R;

        private float[] monoBuffer;

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

                bool spatialized = !bypassSpatialization && (ildMode != ILDMode.None || enableITD || enableHRTF);
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
            using var _ = s_MarkerSpatial.Auto();

            float az = azimuthAngle;
            float el = elevationAngle;
            int samplesPerChannel = data.Length / channels;

            if (monoBuffer == null || monoBuffer.Length < samplesPerChannel)
                monoBuffer = new float[samplesPerChannel];

            for (int i = 0; i < samplesPerChannel; i++)
            {
                float mono = data[i * channels];
                monoBuffer[i] = mono;

                int offset = i * channels;
                data[offset] = mono;
                data[offset + 1] = mono;
                for (int ch = 2; ch < channels; ch++)
                    data[offset + ch] = mono;
            }

            // Stage 1: ITD — delay the contralateral ear
            if (enableITD)
            {
                using var itd = s_MarkerITD.Auto();

                float sinAz = Mathf.Sin(az);
                float effectiveAz = Mathf.Min(Mathf.Abs(az), Mathf.PI * 0.5f) * Mathf.Cos(el);
                float itdSamples = headRadius * (effectiveAz + Mathf.Sin(effectiveAz)) / SPEED_OF_SOUND * sampleRate;

                // Continuous blend: sin(az) smoothly passes through 0 at front AND back,
                // eliminating the click from binary ear switching at ±π
                float delayL = itdSamples * Mathf.Max(0f, sinAz);
                float delayR = itdSamples * Mathf.Max(0f, -sinAz);

                if (delayBuffer == null)
                    delayBuffer = new float[DELAY_BUFFER_SIZE];

                for (int i = 0; i < samplesPerChannel; i++)
                {
                    delayBuffer[delayWritePos & DELAY_BUFFER_MASK] = monoBuffer[i];
                    int offset = i * channels;
                    data[offset] = ReadDelayed(delayL);
                    data[offset + 1] = ReadDelayed(delayR);
                    delayWritePos++;
                }
            }

            // Stage 2: ILD — apply level difference
            if (ildMode != ILDMode.None)
            {
                using var ild = s_MarkerILD.Auto();

                float pan = Mathf.Sin(az) * Mathf.Cos(el) * ildStrength;
                float p = (pan + 1f) * 0.5f;
                float gainL = Mathf.Cos(p * Mathf.PI * 0.5f);
                float gainR = Mathf.Sin(p * Mathf.PI * 0.5f);

                for (int i = 0; i < samplesPerChannel; i++)
                {
                    int offset = i * channels;
                    data[offset] *= gainL;
                    data[offset + 1] *= gainR;
                }
            }

            // Stage 3: HeadShadow — LPF on contralateral ear
            if (ildMode == ILDMode.HeadShadow)
            {
                using var hs = s_MarkerHeadShadow.Auto();

                bool useBiquad = shadowFilterOrder == ShadowFilterOrder.Biquad12dB;
                bool useMultiBand = shadowFilterOrder == ShadowFilterOrder.MultiBand3;

                float shadowAmount = Mathf.Abs(Mathf.Sin(az)) * shadowStrength * Mathf.Cos(el);
                bool shadowOnLeft = az >= 0f;

                float lpfAlpha = 0f;
                float bqB0 = 0f, bqB1 = 0f, bqB2 = 0f, bqA1 = 0f, bqA2 = 0f;
                float mbLB0 = 0f, mbLB1 = 0f, mbLB2 = 0f, mbLA1 = 0f, mbLA2 = 0f;
                float mbHB0 = 0f, mbHB1 = 0f, mbHB2 = 0f, mbHA1 = 0f, mbHA2 = 0f;
                float mbGainLow = 1f, mbGainMid = 1f, mbGainHigh = 1f;

                if (useMultiBand)
                {
                    mbGainLow = Mathf.Pow(10f, lowBandDb * shadowAmount / 20f);
                    mbGainMid = Mathf.Pow(10f, midBandDb * shadowAmount / 20f);
                    mbGainHigh = Mathf.Pow(10f, highBandDb * shadowAmount / 20f);

                    const float butterQ = 0.707f;

                    float w0Lf = 2f * Mathf.PI * crossoverLowMid / sampleRate;
                    float cosLf = Mathf.Cos(w0Lf);
                    float sinLf = Mathf.Sin(w0Lf);
                    float alphaLf = sinLf / (2f * butterQ);
                    float a0InvLf = 1f / (1f + alphaLf);
                    mbLB0 = (1f - cosLf) * 0.5f * a0InvLf;
                    mbLB1 = (1f - cosLf) * a0InvLf;
                    mbLB2 = mbLB0;
                    mbLA1 = -2f * cosLf * a0InvLf;
                    mbLA2 = (1f - alphaLf) * a0InvLf;

                    float w0Hf = 2f * Mathf.PI * crossoverMidHigh / sampleRate;
                    float cosHf = Mathf.Cos(w0Hf);
                    float sinHf = Mathf.Sin(w0Hf);
                    float alphaHf = sinHf / (2f * butterQ);
                    float a0InvHf = 1f / (1f + alphaHf);
                    mbHB0 = (1f + cosHf) * 0.5f * a0InvHf;
                    mbHB1 = -(1f + cosHf) * a0InvHf;
                    mbHB2 = mbHB0;
                    mbHA1 = -2f * cosHf * a0InvHf;
                    mbHA2 = (1f - alphaHf) * a0InvHf;
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

                for (int i = 0; i < samplesPerChannel; i++)
                {
                    int offset = i * channels;
                    if (useMultiBand)
                    {
                        if (shadowOnLeft)
                            data[offset] = ApplyMultiBandFilter(data[offset], mbLB0, mbLB1, mbLB2, mbLA1, mbLA2,
                                mbHB0, mbHB1, mbHB2, mbHA1, mbHA2, mbGainLow, mbGainMid, mbGainHigh, true);
                        else
                            data[offset + 1] = ApplyMultiBandFilter(data[offset + 1], mbLB0, mbLB1, mbLB2, mbLA1, mbLA2,
                                mbHB0, mbHB1, mbHB2, mbHA1, mbHA2, mbGainLow, mbGainMid, mbGainHigh, false);
                    }
                    else
                    {
                        if (shadowOnLeft)
                            data[offset] = ApplyShadowFilter(data[offset], useBiquad, lpfAlpha, bqB0, bqB1, bqB2, bqA1, bqA2, true);
                        else
                            data[offset + 1] = ApplyShadowFilter(data[offset + 1], useBiquad, lpfAlpha, bqB0, bqB1, bqB2, bqA1, bqA2, false);
                    }
                }
            }

            // Stage 4: HRTF — pinna notch filters (both ears, elevation-dependent)
            if (enableHRTF)
            {
                using var hrtf = s_MarkerHRTF.Auto();

                float normalizedEl = el / (Mathf.PI * 0.5f);
                float primaryFreq = pinnaNotchFreq * (1f + elevationInfluence * normalizedEl * 0.4f);
                primaryFreq = Mathf.Clamp(primaryFreq, 2000f, sampleRate * 0.45f);

                ComputePeakingEQ(primaryFreq, pinnaNotchQ, pinnaNotchDepthDb, sampleRate,
                    out float nB0p, out float nB1p, out float nB2p, out float nA1p, out float nA2p);

                bool hasSecondary = pinnaSecondaryStrength > 0.01f;
                float nB0s = 0f, nB1s = 0f, nB2s = 0f, nA1s = 0f, nA2s = 0f;

                if (hasSecondary)
                {
                    float secondaryFreq = primaryFreq * pinnaSecondaryRatio;
                    secondaryFreq = Mathf.Clamp(secondaryFreq, 2000f, sampleRate * 0.45f);
                    float secondaryDepth = pinnaNotchDepthDb * pinnaSecondaryStrength;

                    ComputePeakingEQ(secondaryFreq, pinnaNotchQ, secondaryDepth, sampleRate,
                        out nB0s, out nB1s, out nB2s, out nA1s, out nA2s);
                }

                for (int i = 0; i < samplesPerChannel; i++)
                {
                    int offset = i * channels;
                    data[offset] = ApplyBiquad(data[offset], nB0p, nB1p, nB2p, nA1p, nA2p, ref hrfZ1L, ref hrfZ2L);
                    data[offset + 1] = ApplyBiquad(data[offset + 1], nB0p, nB1p, nB2p, nA1p, nA2p, ref hrfZ1R, ref hrfZ2R);

                    if (hasSecondary)
                    {
                        data[offset] = ApplyBiquad(data[offset], nB0s, nB1s, nB2s, nA1s, nA2s, ref hrf2Z1L, ref hrf2Z2L);
                        data[offset + 1] = ApplyBiquad(data[offset + 1], nB0s, nB1s, nB2s, nA1s, nA2s, ref hrf2Z1R, ref hrf2Z2R);
                    }
                }
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
        /// Peaking EQ biquad coefficients (Bristow-Johnson Audio EQ Cookbook).
        /// With negative dBGain this creates a notch at the specified frequency.
        /// </summary>
        private static void ComputePeakingEQ(float freq, float q, float dbGain, int sr,
            out float b0, out float b1, out float b2, out float a1, out float a2)
        {
            float A = Mathf.Pow(10f, dbGain / 40f);
            float w0 = 2f * Mathf.PI * freq / sr;
            float cosW0 = Mathf.Cos(w0);
            float sinW0 = Mathf.Sin(w0);
            float alpha = sinW0 / (2f * q);

            float a0Inv = 1f / (1f + alpha / A);
            b0 = (1f + alpha * A) * a0Inv;
            b1 = -2f * cosW0 * a0Inv;
            b2 = (1f - alpha * A) * a0Inv;
            a1 = b1;
            a2 = (1f - alpha / A) * a0Inv;
        }

        /// <summary>
        /// Generic Direct Form II Transposed biquad filter. Used for HRTF notch processing.
        /// </summary>
        private static float ApplyBiquad(float input, float b0, float b1, float b2,
            float a1, float a2, ref float z1, ref float z2)
        {
            float y = b0 * input + z1;
            z1 = b1 * input - a1 * y + z2;
            z2 = b2 * input - a2 * y;
            return y;
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
