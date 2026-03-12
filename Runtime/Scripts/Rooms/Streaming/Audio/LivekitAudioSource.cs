using System;
using System.Threading;
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

        internal readonly SpatialAudioDSP spatialDsp = new ();

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

        private float lipSyncAmplitude;
        private float lipSyncSpeechAmplitude;
        private float lipSyncBandLow;
        private float lipSyncBandMid;
        private float lipSyncBandHigh;

        // Bandpass filter state (audio thread only)
        private float hpState;
        private float hpPrevInput;
        private float lpState;

        /// <summary>
        /// Speech band filter cutoffs (Hz). Written from the main thread,
        /// read on the audio thread. Stale reads are harmless.
        /// </summary>
        public float speechBandLowHz = 300f;
        public float speechBandHighHz = 3000f;

        /// <summary>
        /// Pre-spatialization full-spectrum RMS amplitude (0..1).
        /// </summary>
        public float LipSyncAmplitude => Interlocked.CompareExchange(ref lipSyncAmplitude, 0f, 0f);

        /// <summary>
        /// Pre-spatialization speech-band RMS amplitude (0..1).
        /// Bandpass filtered to <see cref="speechBandLowHz"/>–<see cref="speechBandHighHz"/> Hz
        /// to reject music and environmental noise outside the voice range.
        /// </summary>
        public float LipSyncSpeechAmplitude => Interlocked.CompareExchange(ref lipSyncSpeechAmplitude, 0f, 0f);

        /// <summary>Goertzel energy in 200–800 Hz band (vowels A, O). Normalized 0..1.</summary>
        public float LipSyncBandLow => Interlocked.CompareExchange(ref lipSyncBandLow, 0f, 0f);

        /// <summary>Goertzel energy in 800–2500 Hz band (vowels E, I, formant transitions).</summary>
        public float LipSyncBandMid => Interlocked.CompareExchange(ref lipSyncBandMid, 0f, 0f);

        /// <summary>Goertzel energy in 2500–8000 Hz band (sibilants S, SH, F).</summary>
        public float LipSyncBandHigh => Interlocked.CompareExchange(ref lipSyncBandHigh, 0f, 0f);

        private WavWriter? wavWriter;
        private PCMSample[] wavBuffer = Array.Empty<PCMSample>();

        public bool IsWavActive => wavWriter.HasValue;

        /// <summary>
        /// Set the 3D angles from the main thread. Read on the audio thread.
        /// </summary>
        public void SetSpatialAngles(float azimuth, float elevation)
        {
            spatialDsp.SetAngles(azimuth, elevation);
        }

        public static LivekitAudioSource New(bool explicitName = false, bool spatial = false)
        {
            var gm = new GameObject();
            var audioSource = gm.AddComponent<AudioSource>();
            var source = gm.AddComponent<LivekitAudioSource>();
            source.audioSource = audioSource;
            source.bypassSpatialization = !spatial;

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

        private void ComputeLipSyncAmplitudes(float[] data, int channels)
        {
            int monoLen = data.Length / channels;
            if (monoLen == 0) return;

            float dt = 1f / sampleRate;
            float hpRc = 1f / (2f * Mathf.PI * speechBandLowHz);
            float hpAlpha = hpRc / (hpRc + dt);
            float lpRc = 1f / (2f * Mathf.PI * speechBandHighHz);
            float lpAlpha = dt / (lpRc + dt);

            // Goertzel coefficients: coeff = 2·cos(2π·f/fs)
            float gCoeffLow = 2f * Mathf.Cos(2f * Mathf.PI * 500f / sampleRate);
            float gCoeffMid = 2f * Mathf.Cos(2f * Mathf.PI * 1500f / sampleRate);
            float gCoeffHigh = 2f * Mathf.Cos(2f * Mathf.PI * 4000f / sampleRate);

            float gL1 = 0f, gL2 = 0f;
            float gM1 = 0f, gM2 = 0f;
            float gH1 = 0f, gH2 = 0f;

            float fullSum = 0f;
            float bandSum = 0f;
            int bandCount = 0;

            for (int i = 0; i < data.Length; i++)
            {
                fullSum += data[i] * data[i];

                if (i % channels == 0)
                {
                    float x = data[i];

                    // Bandpass filter (Step 2.5)
                    float hp = hpAlpha * (hpState + x - hpPrevInput);
                    hpPrevInput = x;
                    hpState = hp;
                    float bp = lpState + lpAlpha * (hp - lpState);
                    lpState = bp;
                    bandSum += bp * bp;

                    // Goertzel accumulators (Step 3)
                    float s;
                    s = x + gCoeffLow * gL1 - gL2; gL2 = gL1; gL1 = s;
                    s = x + gCoeffMid * gM1 - gM2; gM2 = gM1; gM1 = s;
                    s = x + gCoeffHigh * gH1 - gH2; gH2 = gH1; gH1 = s;

                    bandCount++;
                }
            }

            Interlocked.Exchange(ref lipSyncAmplitude, Mathf.Sqrt(fullSum / data.Length));
            Interlocked.Exchange(ref lipSyncSpeechAmplitude, bandCount > 0 ? Mathf.Sqrt(bandSum / bandCount) : 0f);

            // Goertzel energy: E = s1² + s0² - coeff·s1·s0, normalized by N²
            float invN2 = 1f / ((float)bandCount * bandCount);
            float eLow = (gL1 * gL1 + gL2 * gL2 - gCoeffLow * gL1 * gL2) * invN2;
            float eMid = (gM1 * gM1 + gM2 * gM2 - gCoeffMid * gM1 * gM2) * invN2;
            float eHigh = (gH1 * gH1 + gH2 * gH2 - gCoeffHigh * gH1 * gH2) * invN2;

            Interlocked.Exchange(ref lipSyncBandLow, Mathf.Sqrt(eLow));
            Interlocked.Exchange(ref lipSyncBandMid, Mathf.Sqrt(eMid));
            Interlocked.Exchange(ref lipSyncBandHigh, Mathf.Sqrt(eHigh));
        }

        private void OnAudioFilterRead(float[] data, int channels)
        {
            Option<AudioStream> resource = stream.Resource;
            if (resource.Has)
            {
                resource.Value.ReadAudio(data.AsSpan(), channels, sampleRate);

                ComputeLipSyncAmplitudes(data, channels);

                spatialDsp.Process(data, channels, sampleRate, this);

                if (wavWriter.HasValue)
                {
                    if (data.Length != wavBuffer.Length)
                    {
                        wavBuffer = new PCMSample[data.Length];
                    }

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
    }
}
