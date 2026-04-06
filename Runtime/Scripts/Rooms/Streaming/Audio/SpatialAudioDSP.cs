using System.Runtime.CompilerServices;
using Unity.Profiling;
using UnityEngine;

namespace LiveKit.Rooms.Streaming.Audio
{
    /// <summary>
    /// Encapsulates all spatialization DSP processing and filter state.
    /// Config fields remain on <see cref="LivekitAudioSource"/>; this class owns
    /// only the mutable per-instance filter memories and the processing pipeline.
    /// Called from the audio thread via <see cref="LivekitAudioSource.OnAudioFilterRead"/>.
    /// </summary>
    public class SpatialAudioDSP
    {
        private static readonly ProfilerMarker s_MarkerSpatial = new("LiveKit.Spatial");
        private static readonly ProfilerMarker s_MarkerITD = new("LiveKit.Spatial.ITD");
        private static readonly ProfilerMarker s_MarkerILD = new("LiveKit.Spatial.ILD");
        private static readonly ProfilerMarker s_MarkerHeadShadow = new("LiveKit.Spatial.HeadShadow");
        private static readonly ProfilerMarker s_MarkerHRTF = new("LiveKit.Spatial.HRTF");

        private const float SPEED_OF_SOUND = 343f;
        private const int DELAY_BUFFER_SIZE = 256;
        private const int DELAY_BUFFER_MASK = DELAY_BUFFER_SIZE - 1;

        private float azimuthAngle;
        private float elevationAngle;

        private float[] delayBuffer;
        private int delayWritePos;

        // Cascade one-pole filter states (up to 4 poles per ear)
        private float lpfS1L, lpfS2L, lpfS3L, lpfS4L;
        private float lpfS1R, lpfS2R, lpfS3R, lpfS4R;

        // Biquad filter states (Direct Form II Transposed, per ear)
        private float bqZ1L, bqZ2L;
        private float bqZ1R, bqZ2R;

        // MultiBand3 biquad states (LPF + HPF per ear)
        private float mbLpfZ1L, mbLpfZ2L, mbHpfZ1L, mbHpfZ2L;
        private float mbLpfZ1R, mbLpfZ2R, mbHpfZ1R, mbHpfZ2R;

        // DualShelf first-order high shelf states (xPrev, yPrev per shelf per ear)
        private float dsXp1L, dsYp1L, dsXp1R, dsYp1R;
        private float dsXp2L, dsYp2L, dsXp2R, dsYp2R;

        // HRTF primary pinna notch states (peaking EQ biquad, both ears)
        private float hrfZ1L, hrfZ2L, hrfZ1R, hrfZ2R;

        // HRTF secondary pinna notch states
        private float hrf2Z1L, hrf2Z2L, hrf2Z1R, hrf2Z2R;

        private float[] monoBuffer;

        public void SetAngles(float azimuth, float elevation)
        {
            azimuthAngle = azimuth;
            elevationAngle = elevation;
        }

        /// <summary>
        /// Runs the full spatialization pipeline on the audio buffer.
        /// Reads config fields directly from the source to avoid struct copying overhead.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Process(float[] data, int channels, int sampleRate, LivekitAudioSource src)
        {
            bool spatialized = !src.bypassSpatialization
                               && (src.ildMode != ILDMode.None || src.enableITD || src.enableHRTF);

            if (!spatialized || channels < 2) return;

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

            if (src.enableITD)
                ProcessITD(data, channels, sampleRate, samplesPerChannel, az, el, src.headRadius);

            if (src.ildMode != ILDMode.None)
                ProcessILD(data, channels, samplesPerChannel, az, el, src.ildStrength);

            if (src.ildMode == ILDMode.HeadShadow)
                ProcessHeadShadow(data, channels, sampleRate, samplesPerChannel, az, el, src);

            if (src.enableHRTF)
                ProcessHRTF(data, channels, sampleRate, samplesPerChannel, el, src);
        }

        private void ProcessITD(float[] data, int channels, int sampleRate,
            int samplesPerChannel, float az, float el, float headRadius)
        {
            using var _ = s_MarkerITD.Auto();

            float sinAz = Mathf.Sin(az);
            float effectiveAz = Mathf.Min(Mathf.Abs(az), Mathf.PI * 0.5f) * Mathf.Cos(el);
            float itdSamples = headRadius * (effectiveAz + Mathf.Sin(effectiveAz)) / SPEED_OF_SOUND * sampleRate;

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

        private static void ProcessILD(float[] data, int channels,
            int samplesPerChannel, float az, float el, float ildStrength)
        {
            using var _ = s_MarkerILD.Auto();

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

        private void ProcessHeadShadow(float[] data, int channels, int sampleRate,
            int samplesPerChannel, float az, float el, LivekitAudioSource src)
        {
            using var _ = s_MarkerHeadShadow.Auto();

            bool useBiquad = src.shadowFilterOrder == ShadowFilterOrder.Biquad12dB;
            bool useMultiBand = src.shadowFilterOrder == ShadowFilterOrder.MultiBand3;
            bool useDualShelf = src.shadowFilterOrder == ShadowFilterOrder.DualShelf;

            float shadowAmount = Mathf.Abs(Mathf.Sin(az)) * src.shadowStrength * Mathf.Cos(el);
            bool shadowOnLeft = az >= 0f;

            float lpfAlpha = 0f;
            float bqB0 = 0f, bqB1 = 0f, bqB2 = 0f, bqA1 = 0f, bqA2 = 0f;
            float mbLB0 = 0f, mbLB1 = 0f, mbLB2 = 0f, mbLA1 = 0f, mbLA2 = 0f;
            float mbHB0 = 0f, mbHB1 = 0f, mbHB2 = 0f, mbHA1 = 0f, mbHA2 = 0f;
            float mbGainLow = 1f, mbGainMid = 1f, mbGainHigh = 1f;

            float dsS1B0 = 0f, dsS1B1 = 0f, dsS1A1 = 0f;
            float dsS2B0 = 0f, dsS2B1 = 0f, dsS2A1 = 0f;
            float dsBaseGain = 1f;

            if (useDualShelf)
            {
                dsBaseGain = Mathf.Pow(10f, src.lowBandDb * shadowAmount / 20f);
                ComputeFirstOrderHighShelf(src.crossoverLowMid,
                    (src.midBandDb - src.lowBandDb) * shadowAmount, sampleRate,
                    out dsS1B0, out dsS1B1, out dsS1A1);
                ComputeFirstOrderHighShelf(src.crossoverMidHigh,
                    (src.highBandDb - src.midBandDb) * shadowAmount, sampleRate,
                    out dsS2B0, out dsS2B1, out dsS2A1);
            }
            else if (useMultiBand)
            {
                mbGainLow = Mathf.Pow(10f, src.lowBandDb * shadowAmount / 20f);
                mbGainMid = Mathf.Pow(10f, src.midBandDb * shadowAmount / 20f);
                mbGainHigh = Mathf.Pow(10f, src.highBandDb * shadowAmount / 20f);

                const float butterQ = 0.707f;

                float w0Lf = 2f * Mathf.PI * src.crossoverLowMid / sampleRate;
                float cosLf = Mathf.Cos(w0Lf);
                float sinLf = Mathf.Sin(w0Lf);
                float alphaLf = sinLf / (2f * butterQ);
                float a0InvLf = 1f / (1f + alphaLf);
                mbLB0 = (1f - cosLf) * 0.5f * a0InvLf;
                mbLB1 = (1f - cosLf) * a0InvLf;
                mbLB2 = mbLB0;
                mbLA1 = -2f * cosLf * a0InvLf;
                mbLA2 = (1f - alphaLf) * a0InvLf;

                float w0Hf = 2f * Mathf.PI * src.crossoverMidHigh / sampleRate;
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
                float cutoff = Mathf.Lerp(20000f, src.shadowCutoffHz, shadowAmount);
                float w0 = 2f * Mathf.PI * cutoff / sampleRate;
                float cosW0 = Mathf.Cos(w0);
                float sinW0 = Mathf.Sin(w0);
                float alphaBq = sinW0 / (2f * src.biquadQ);
                float a0Inv = 1f / (1f + alphaBq);

                bqB0 = (1f - cosW0) * 0.5f * a0Inv;
                bqB1 = (1f - cosW0) * a0Inv;
                bqB2 = bqB0;
                bqA1 = -2f * cosW0 * a0Inv;
                bqA2 = (1f - alphaBq) * a0Inv;
            }
            else
            {
                float cutoff = Mathf.Lerp(20000f, src.shadowCutoffHz, shadowAmount);
                lpfAlpha = 1f / (1f + sampleRate / (2f * Mathf.PI * cutoff));
            }

            for (int i = 0; i < samplesPerChannel; i++)
            {
                int offset = i * channels;
                if (useDualShelf)
                {
                    if (shadowOnLeft)
                        data[offset] = ApplyDualShelf(data[offset],
                            dsS1B0, dsS1B1, dsS1A1, dsS2B0, dsS2B1, dsS2A1, dsBaseGain, true);
                    else
                        data[offset + 1] = ApplyDualShelf(data[offset + 1],
                            dsS1B0, dsS1B1, dsS1A1, dsS2B0, dsS2B1, dsS2A1, dsBaseGain, false);
                }
                else if (useMultiBand)
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
                        data[offset] = ApplyShadowFilter(data[offset], useBiquad, lpfAlpha,
                            bqB0, bqB1, bqB2, bqA1, bqA2, src.shadowFilterOrder, true);
                    else
                        data[offset + 1] = ApplyShadowFilter(data[offset + 1], useBiquad, lpfAlpha,
                            bqB0, bqB1, bqB2, bqA1, bqA2, src.shadowFilterOrder, false);
                }
            }
        }

        private void ProcessHRTF(float[] data, int channels, int sampleRate,
            int samplesPerChannel, float el, LivekitAudioSource src)
        {
            using var _ = s_MarkerHRTF.Auto();

            float normalizedEl = el / (Mathf.PI * 0.5f);
            float primaryFreq = src.pinnaNotchFreq * (1f + src.elevationInfluence * normalizedEl * 0.4f);
            primaryFreq = Mathf.Clamp(primaryFreq, 2000f, sampleRate * 0.45f);

            ComputePeakingEQ(primaryFreq, src.pinnaNotchQ, src.pinnaNotchDepthDb, sampleRate,
                out float nB0p, out float nB1p, out float nB2p, out float nA1p, out float nA2p);

            bool hasSecondary = src.pinnaSecondaryStrength > 0.01f;
            float nB0s = 0f, nB1s = 0f, nB2s = 0f, nA1s = 0f, nA2s = 0f;

            if (hasSecondary)
            {
                float secondaryFreq = primaryFreq * src.pinnaSecondaryRatio;
                secondaryFreq = Mathf.Clamp(secondaryFreq, 2000f, sampleRate * 0.45f);
                float secondaryDepth = src.pinnaNotchDepthDb * src.pinnaSecondaryStrength;

                ComputePeakingEQ(secondaryFreq, src.pinnaNotchQ, secondaryDepth, sampleRate,
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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private float ApplyShadowFilter(float input, bool useBiquad,
            float alpha, float b0, float b1, float b2, float a1, float a2,
            ShadowFilterOrder filterOrder, bool isLeft)
        {
            if (useBiquad)
            {
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

            if (isLeft)
            {
                lpfS1L += alpha * (input - lpfS1L);
                float s = lpfS1L;
                if (filterOrder >= ShadowFilterOrder.TwoPole12dB) { lpfS2L += alpha * (s - lpfS2L); s = lpfS2L; }
                if (filterOrder >= ShadowFilterOrder.ThreePole18dB) { lpfS3L += alpha * (s - lpfS3L); s = lpfS3L; }
                if (filterOrder >= ShadowFilterOrder.FourPole24dB) { lpfS4L += alpha * (s - lpfS4L); s = lpfS4L; }
                return s;
            }
            else
            {
                lpfS1R += alpha * (input - lpfS1R);
                float s = lpfS1R;
                if (filterOrder >= ShadowFilterOrder.TwoPole12dB) { lpfS2R += alpha * (s - lpfS2R); s = lpfS2R; }
                if (filterOrder >= ShadowFilterOrder.ThreePole18dB) { lpfS3R += alpha * (s - lpfS3R); s = lpfS3R; }
                if (filterOrder >= ShadowFilterOrder.FourPole24dB) { lpfS4R += alpha * (s - lpfS4R); s = lpfS4R; }
                return s;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
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
        /// Computes first-order high shelf coefficients via bilinear transform.
        /// Unity gain at DC, gain = 10^(dBgain/20) at Nyquist, transition at freq.
        /// Analog prototype: H(s) = A·(s + ωc/√A) / (s + ωc·√A).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ComputeFirstOrderHighShelf(float freq, float dBgain, int sampleRate,
            out float b0, out float b1, out float a1)
        {
            float A = Mathf.Pow(10f, dBgain / 20f);
            float K = Mathf.Tan(Mathf.PI * freq / sampleRate);
            float sqA = Mathf.Sqrt(A);
            float norm = 1f / (1f + K * sqA);
            b0 = sqA * (sqA + K) * norm;
            b1 = sqA * (K - sqA) * norm;
            a1 = (K * sqA - 1f) * norm;
        }

        /// <summary>
        /// Two cascaded first-order high shelves + base gain.
        /// Approximates MultiBand3 staircase with smooth S-curve transitions.
        /// 11 FLOP/sample: 2 shelves × (3 mul + 2 add) + 1 mul (base gain).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private float ApplyDualShelf(float input,
            float s1B0, float s1B1, float s1A1,
            float s2B0, float s2B1, float s2A1,
            float baseGain, bool isLeft)
        {
            float y1, y2;

            if (isLeft)
            {
                y1 = s1B0 * input + s1B1 * dsXp1L - s1A1 * dsYp1L;
                dsXp1L = input; dsYp1L = y1;
                y2 = s2B0 * y1 + s2B1 * dsXp2L - s2A1 * dsYp2L;
                dsXp2L = y1; dsYp2L = y2;
            }
            else
            {
                y1 = s1B0 * input + s1B1 * dsXp1R - s1A1 * dsYp1R;
                dsXp1R = input; dsYp1R = y1;
                y2 = s2B0 * y1 + s2B1 * dsXp2R - s2A1 * dsYp2R;
                dsXp2R = y1; dsYp2R = y2;
            }

            return y2 * baseGain;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float ApplyBiquad(float input, float b0, float b1, float b2,
            float a1, float a2, ref float z1, ref float z2)
        {
            float y = b0 * input + z1;
            z1 = b1 * input - a1 * y + z2;
            z2 = b2 * input - a2 * y;
            return y;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
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
