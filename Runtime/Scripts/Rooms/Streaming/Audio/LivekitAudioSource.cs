#if !UNITY_WEBGL || UNITY_EDITOR

using System;
using System.Runtime.CompilerServices;
using LiveKit.Audio;
using LiveKit.Internal;
using RichTypes;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;

namespace LiveKit.Rooms.Streaming.Audio
{
    public class LivekitAudioSource : MonoBehaviour
    {
        private static readonly ProfilerMarker markerEqualPaowerDSP = new ("LiveKit.Spatial.ILD.EqualPower");
        private const float HALF_PI = math.PI * 0.5f;
        
        private static ulong counter;

        private int sampleRate;
        private Weak<AudioStream> stream = Weak<AudioStream>.Null;

        private volatile float azimuth;
        private volatile float elevation;
        private float prevGainL = 0.707f;
        private float prevGainR = 0.707f;

        [Header("SPATIALIZATION")]
        [SerializeField] private volatile bool spatialize;
        [SerializeField, Range(0f, 1f)] private volatile float ildStrength = 0.75f;
        [SerializeField] private volatile bool smoothPanning;
        
        private WavWriter? wavWriter;
        private PCMSample[] wavBuffer = Array.Empty<PCMSample>();

        public bool IsWavActive => wavWriter.HasValue;
        public AudioSource AudioSource { get; private set; } = null!;

        public static LivekitAudioSource New(bool explicitName = false, bool isSpatial = false)
        {
            var gm = new GameObject();
            var source = gm.AddComponent<LivekitAudioSource>();
            source.AudioSource = gm.AddComponent<AudioSource>();
            source.spatialize = isSpatial;
            if (explicitName) source.name = $"{nameof(LivekitAudioSource)}_{counter++}";
            return source;
        }
        
        public void SetSpatialAngles(float azimuth, float elevation)
        {
            this.azimuth = azimuth;
            this.elevation = elevation;
        }

        public void SetSpatialSettings(bool spatialize, float ildStrength, bool smoothPanning)
        {
            this.spatialize = spatialize;
            this.ildStrength = ildStrength;
            this.smoothPanning = smoothPanning;
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
            AudioSource.Play();
        }

        public void Stop()
        {
            AudioSource.Stop();
        }

        public void SetVolume(float target)
        {
            AudioSource.volume = target;
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
                    LiveKit.Internal.Utils.Error($"Cannot restart wav recording for output: {result.ErrorMessage}");
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

                // TODO: handle 5.1 and 7.1 sound system cases
                if (spatialize && channels == 2)
                    ApplySpatialPanning(data, channels);

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
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ApplySpatialPanning(float[] data, int channels)
        {
            using var _ = markerEqualPaowerDSP.Auto();

            int samplesPerChannel = data.Length / channels;

            float pan = math.sin(azimuth) * math.cos(elevation) * ildStrength;
            float p = (pan + 1f) * 0.5f;
            float gainL = math.cos(p * HALF_PI);
            float gainR = math.sin(p * HALF_PI);

            if (smoothPanning)
            {
                float invLen = 1f / samplesPerChannel;

                for (int i = 0; i < samplesPerChannel; i++)
                {
                    float t = i * invLen;
                    int offset = i * channels;
                    float mono = data[offset];
                    data[offset]     = mono * math.lerp(prevGainL, gainL, t);
                    data[offset + 1] = mono * math.lerp(prevGainR, gainR, t);
                }
            }
            else
            {
                for (int i = 0; i < samplesPerChannel; i++)
                {
                    int offset = i * channels;
                    float mono = data[offset];
                    data[offset]     = mono * gainL;
                    data[offset + 1] = mono * gainR;
                }
            }
            
            prevGainL = gainL;
            prevGainR = gainR;
        }
    }
}

#endif
