using System;
using System.Runtime.InteropServices;
using LiveKit.Internal;
using LiveKit.Internal.FFIClients.Requests;
using LiveKit.Proto;
using RichTypes;
using UnityEngine;

namespace LiveKit.Audio
{
    public class MicrophoneRtcAudioSource2 : IRtcAudioSource, IDisposable
    {
        private const int DEFAULT_NUM_CHANNELS = 2;
        private const float VOLUME_MULTIPLIER = 15.0f;
        private const uint TARGET_SAMPLE_RATE = 48000; // LiveKit's default
        
        private readonly AudioBuffer buffer = new();
        private readonly object lockObject = new();

        private readonly IAudioFilter audioFilter;
        private readonly Apm apm;
        private readonly ApmReverseStream? reverseStream;
        private readonly GameObject gameObject;

        private bool handleBorrowed;
        private bool disposed;

        private readonly FfiHandle handle;
        private readonly float[] floatSamplesBuffer;
        private readonly PCMSample[] pcmSamplesBuffer;
        private readonly AudioBuffer resampledBuffer = new();

        private MicrophoneRtcAudioSource2(
            IAudioFilter audioFilter,
            Apm apm,
            ApmReverseStream? apmReverseStream
        )
        {
            reverseStream = apmReverseStream;

            using var request = FFIBridge.Instance.NewRequest<NewAudioSourceRequest>();
            var newAudioSource = request.request;
            newAudioSource.Type = AudioSourceType.AudioSourceNative;
            newAudioSource.NumChannels = DEFAULT_NUM_CHANNELS;
            newAudioSource.SampleRate = SampleRate.Hz48000.valueHz;

            using var options = request.TempResource<AudioSourceOptions>();
            newAudioSource.Options = options;
            newAudioSource.Options.EchoCancellation = true;
            newAudioSource.Options.NoiseSuppression = true;
            newAudioSource.Options.AutoGainControl = true;

            using var response = request.Send();
            FfiResponse res = response;
            handle = IFfiHandleFactory.Default.NewFfiHandle(res.NewAudioSource.Source.Handle!.Id);
            this.audioFilter = audioFilter;
            this.apm = apm;

            var maxSamplesPerFrame = (TARGET_SAMPLE_RATE / 100) * DEFAULT_NUM_CHANNELS;
            floatSamplesBuffer = new float[maxSamplesPerFrame];
            pcmSamplesBuffer = new PCMSample[maxSamplesPerFrame];
        }

        public static Result<MicrophoneRtcAudioSource2> New(IAudioFilter microphoneAudioFilter)
        {
            Apm apm = Apm.NewDefault();
            apm.SetStreamDelay(Apm.EstimateStreamDelayMs());

            Result<ApmReverseStream> reverseStream = ApmReverseStream.New(apm);
            if (reverseStream.Success == false)
            {
                return Result<MicrophoneRtcAudioSource2>.ErrorResult(
                    $"Cannot create reverse stream: {reverseStream.ErrorMessage}"
                );
            }

            return Result<MicrophoneRtcAudioSource2>.SuccessResult(new MicrophoneRtcAudioSource2(microphoneAudioFilter, apm, reverseStream.Value));
        }

        FfiHandle IRtcAudioSource.BorrowHandle()
        {
            if (handleBorrowed)
            {
                Utils.Error("Borrowing already borrowed handle, may cause undefined behaviour");
            }

            handleBorrowed = true;
            return handle;
        }

        public void Start()
        {
            Stop();
            if (!audioFilter.IsValid == true)
            {
                Utils.Error("AudioFilter or AudioSource is null - cannot start audio capture");
                return;
            }

            audioFilter!.AudioRead += OnAudioRead;
            reverseStream?.Start();
        }

        public void Stop()
        {
            if (audioFilter.IsValid == true)
                audioFilter.AudioRead -= OnAudioRead;

            reverseStream?.Stop();

            lock (lockObject)
            {
                buffer.Dispose();
                resampledBuffer.Dispose();
            }
        }

        private void OnAudioRead(Span<float> data, int channels, int sampleRate)
        {
            if (disposed) return;

            lock (lockObject)
            {
                buffer.Write(data, (uint)channels, (uint)sampleRate);
            }

            ProcessAvailableFrames();
        }

        private void ProcessAvailableFrames()
        {
            if (disposed) return;

            while (true)
            {
                AudioFrame? frame = null;
                
                // Only lock for buffer read operation
                lock (lockObject)
                {
                    var frameResult = buffer.ReadDuration(ApmFrame.FRAME_DURATION_MS);
                    if (frameResult.Has == false) break;
                    frame = frameResult.Value;
                }

                if (frame == null) break;

                using (frame.Value)
                {
                    ProcessSingleFrame(frame.Value);
                }
            }
        }

        private void ProcessSingleFrame(AudioFrame frame)
        {
            var audioBytes = MemoryMarshal.Cast<byte, PCMSample>(frame.AsSpan());

            var apmFrame = ApmFrame.New(
                audioBytes,
                frame.NumChannels,
                frame.SamplesPerChannel,
                new SampleRate(frame.SampleRate),
                out string? error
            );
            if (error != null)
            {
                Utils.Error($"Error during creation ApmFrame: {error}");
                return;
            }

            var apmResult = apm.ProcessStream(apmFrame);
            if (apmResult.Success == false)
                Utils.Error($"Error during processing stream: {apmResult.ErrorMessage}");

            var processedFrame = ApplyVolumeAndResampling(frame);
            ProcessAudioFrame(processedFrame);
        }

        private AudioFrame ApplyVolumeAndResampling(in AudioFrame originalFrame)
        {
            var pcmSamples = MemoryMarshal.Cast<byte, PCMSample>(originalFrame.AsSpan());
            
            // Apply volume directly to PCM samples
            for (int i = 0; i < pcmSamples.Length; i++)
            {
                var sample = pcmSamples[i].data;
                var amplifiedSample = (short)(sample * VOLUME_MULTIPLIER);
                
                // Clamp to prevent overflow
                if (amplifiedSample > 32767) amplifiedSample = 32767;
                else if (amplifiedSample < -32768) amplifiedSample = -32768;
                
                pcmSamples[i] = new PCMSample(amplifiedSample);
            }

            if (originalFrame.SampleRate != TARGET_SAMPLE_RATE)
            {
                var resampledPcmSamples = ResamplePCMAudio(pcmSamples, originalFrame.SampleRate, TARGET_SAMPLE_RATE);
                
                var newSamplesPerChannel = (uint)(resampledPcmSamples.Length / originalFrame.NumChannels);
                var newFrame = new AudioFrame(TARGET_SAMPLE_RATE, originalFrame.NumChannels, newSamplesPerChannel);
                
                var newFrameSpan = MemoryMarshal.Cast<byte, PCMSample>(newFrame.AsSpan());
                resampledPcmSamples.CopyTo(newFrameSpan);
                
                return newFrame;
            }
            
            return originalFrame;
        }

        private PCMSample[] ResamplePCMAudio(ReadOnlySpan<PCMSample> inputSamples, uint inputSampleRate, uint outputSampleRate)
        {
            if (inputSampleRate == outputSampleRate)
                return inputSamples.ToArray();

            var ratio = (double)outputSampleRate / inputSampleRate;
            var outputLength = (int)(inputSamples.Length * ratio);
            var outputSamples = new PCMSample[outputLength];

            for (int i = 0; i < outputLength; i++)
            {
                var inputIndex = i / ratio;
                var inputIndexFloor = (int)Math.Floor(inputIndex);
                var inputIndexCeil = Math.Min(inputIndexFloor + 1, inputSamples.Length - 1);
                var fraction = inputIndex - inputIndexFloor;

                if (inputIndexFloor >= inputSamples.Length)
                {
                    outputSamples[i] = new PCMSample(0);
                }
                else if (inputIndexFloor == inputIndexCeil)
                {
                    outputSamples[i] = inputSamples[inputIndexFloor];
                }
                else
                {
                    var sample1 = inputSamples[inputIndexFloor].data;
                    var sample2 = inputSamples[inputIndexCeil].data;
                    var fractionFloat = (float)fraction;
                    var interpolatedValue = (short)(sample1 * (1 - fractionFloat) + sample2 * fractionFloat);
                    outputSamples[i] = new PCMSample(interpolatedValue);
                }
            }

            return outputSamples;
        }

        private float[] ResampleAudio(ReadOnlySpan<float> inputSamples, uint inputSampleRate, uint outputSampleRate)
        {
            if (inputSampleRate == outputSampleRate)
                return inputSamples.ToArray();

            var ratio = (double)outputSampleRate / inputSampleRate;
            var outputLength = (int)(inputSamples.Length * ratio);
            var outputSamples = new float[outputLength];

            for (int i = 0; i < outputLength; i++)
            {
                var inputIndex = i / ratio;
                var inputIndexFloor = (int)Math.Floor(inputIndex);
                var inputIndexCeil = Math.Min(inputIndexFloor + 1, inputSamples.Length - 1);
                var fraction = inputIndex - inputIndexFloor;

                if (inputIndexFloor >= inputSamples.Length)
                {
                    outputSamples[i] = 0;
                }
                else if (inputIndexFloor == inputIndexCeil)
                {
                    outputSamples[i] = inputSamples[inputIndexFloor];
                }
                else
                {
                    var fractionFloat = (float)fraction;
                    outputSamples[i] = inputSamples[inputIndexFloor] * (1 - fractionFloat) + inputSamples[inputIndexCeil] * fractionFloat;
                }
            }

            return outputSamples;
        }

        private void ProcessAudioFrame(in AudioFrame frame)
        {
            if (disposed) return;

            try
            {
                using var request = FFIBridge.Instance.NewRequest<CaptureAudioFrameRequest>();
                using var audioFrameBufferInfo = request.TempResource<AudioFrameBufferInfo>();

                var pushFrame = request.request;
                pushFrame.SourceHandle = (ulong)handle.DangerousGetHandle();
                pushFrame.Buffer = audioFrameBufferInfo;
                pushFrame.Buffer.DataPtr = (ulong)frame.Data;
                pushFrame.Buffer.NumChannels = frame.NumChannels;
                pushFrame.Buffer.SampleRate = frame.SampleRate;
                pushFrame.Buffer.SamplesPerChannel = frame.SamplesPerChannel;

                using var response = request.Send();

                pushFrame.Buffer.DataPtr = 0;
                pushFrame.Buffer.NumChannels = 0;
                pushFrame.Buffer.SampleRate = 0;
                pushFrame.Buffer.SamplesPerChannel = 0;
            }
            catch (Exception e)
            {
                Utils.Error("Audio Framedata error: " + e.Message + "\nStackTrace: " + e.StackTrace);
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                Utils.Error($"{nameof(MicrophoneRtcAudioSource)} is already disposed");
                return;
            }

            disposed = true;
            
            Stop();

            buffer.Dispose();
            resampledBuffer.Dispose();
            apm.Dispose();
            reverseStream?.Dispose();

            if (handleBorrowed == false)
                handle.Dispose();
        }
    }
}
