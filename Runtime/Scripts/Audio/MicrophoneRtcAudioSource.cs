using System;
using LiveKit.Internal;
using LiveKit.Internal.FFIClients.Requests;
using LiveKit.Proto;
using LiveKit.Rooms.Streaming.Audio;
using LiveKit.Runtime.Scripts.Audio;
using LiveKit.Scripts.Audio;
using Livekit.Types;
using RichTypes;
using UnityEngine;
using UnityEngine.Audio;

namespace LiveKit.Audio
{
    public class MicrophoneRtcAudioSource : IRtcAudioSource, IDisposable
    {
        private const int DEFAULT_NUM_CHANNELS = 2;

        private readonly AudioResampler audioResampler = AudioResampler.New();

        private readonly Mutex<NativeAudioBuffer> buffer = new(new NativeAudioBuffer(200));

        private readonly MicrophoneAudioFilter deviceMicrophoneAudioSource;

        private readonly Apm apm;
        private readonly ApmReverseStream? reverseStream;
        private readonly GameObject gameObject;

        private bool handleBorrowed;
        private bool disposed;

        private readonly FfiHandle handle;

        public bool IsRecording => deviceMicrophoneAudioSource.IsRecording;

        private MicrophoneRtcAudioSource(
            MicrophoneAudioFilter deviceMicrophoneAudioSource,
            Apm apm,
            ApmReverseStream? apmReverseStream
        )
        {
            this.deviceMicrophoneAudioSource = deviceMicrophoneAudioSource;
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
            this.apm = apm;
        }

        public static Result<MicrophoneRtcAudioSource> New(
            MicrophoneSelection? microphoneSelection = null,
            AudioMixerGroup? audioMixerGroup = null)
        {
            MicrophoneSelection selection;
            if (microphoneSelection.HasValue)
                selection = microphoneSelection.Value;
            else
            {
                Result<MicrophoneSelection> result = MicrophoneSelection.Default();
                if (result.Success)
                    selection = result.Value;
                else
                    return Result<MicrophoneRtcAudioSource>.ErrorResult(result.ErrorMessage!);
            }

            Apm apm = Apm.NewDefault();
            apm.SetStreamDelay(Apm.EstimateStreamDelayMs());

            Result<ApmReverseStream> reverseStream = ApmReverseStream.New(apm);
            if (reverseStream.Success == false)
            {
                return Result<MicrophoneRtcAudioSource>.ErrorResult(
                    $"Cannot create reverse stream: {reverseStream.ErrorMessage}"
                );
            }

            var source = MicrophoneAudioFilter.New(microphoneSelection?.name);
            if (source.Success == false)
            {
                return Result<MicrophoneRtcAudioSource>.ErrorResult(
                    $"Cannot create source: {source.ErrorMessage}"
                );
            }

            return Result<MicrophoneRtcAudioSource>.SuccessResult(
                new MicrophoneRtcAudioSource(source.Value, apm, reverseStream.Value)
            );
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
            if (deviceMicrophoneAudioSource.IsValid == false)
            {
                Utils.Error("AudioFilter or AudioSource is null - cannot start audio capture");
                return;
            }

            deviceMicrophoneAudioSource.AudioRead += OnAudioRead;
            deviceMicrophoneAudioSource.StartCapture();
            reverseStream?.Start();
        }

        public void Stop()
        {
            if (deviceMicrophoneAudioSource.IsValid) deviceMicrophoneAudioSource.AudioRead -= OnAudioRead;
            deviceMicrophoneAudioSource.StopCapture();

            reverseStream?.Stop();
        }

        public void Toggle()
        {
            if (IsRecording)
                Stop();
            else
                Start();
        }

        public void SwitchMicrophone(MicrophoneSelection microphoneSelection)
        {
            throw new Exception("Not implemented");
            //    deviceMicrophoneAudioSource.SwitchMicrophone(microphoneSelection);
        }

        private void OnAudioRead(Span<float> data, int channels, int sampleRate)
        {
            using var guard = buffer.Lock();

            PCMSample[] converted = new PCMSample[data.Length];
            for (int i = 0; i < data.Length; i++)
            {
                converted[i] = PCMSample.FromUnitySample(data[i]);
            }

            guard.Value.Write(converted, (uint)channels, (uint)sampleRate);
            while (true)
            {
                var ms10ToRead = sampleRate / 100;
                Option<AudioFrame> frameResult = guard.Value.Read((uint)sampleRate, (uint)channels, (uint)ms10ToRead);
                if (frameResult.Has == false) break;
                using AudioFrame rawFrame = frameResult.Value;
                using OwnedAudioFrame frame =
                    audioResampler.LiveKitCompatibleRemixAndResample(rawFrame, DEFAULT_NUM_CHANNELS);

                Span<PCMSample> audioBytes = frame.AsPCMSampleSpan();

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
                    break;
                }

                var apmResult = apm.ProcessStream(apmFrame);
                if (apmResult.Success == false)
                    Utils.Error($"Error during processing stream: {apmResult.ErrorMessage}");

                ProcessAudioFrame(frame);
            }
        }

        private void ProcessAudioFrame(in OwnedAudioFrame frame)
        {
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

            using (var guard = buffer.Lock()) guard.Value.Dispose();

            apm.Dispose();
            audioResampler.Dispose();
            reverseStream?.Dispose();
            deviceMicrophoneAudioSource.Dispose();

            if (handleBorrowed == false)
                handle.Dispose();

            if (gameObject)
                UnityEngine.Object.Destroy(gameObject);
        }
    }
}