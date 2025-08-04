using System;
using System.Linq;
using RichTypes;
using RustAudio;

namespace LiveKit.Scripts.Audio
{
    public class MicrophoneAudioFilter : IAudioFilter, IDisposable
    {
        public readonly uint SampleRate;
        public readonly uint Channels;
        public readonly string Name;
        private readonly RustAudioSource native;

        private bool disposed;

        private static volatile MicrophoneAudioFilter? instance;

        public bool IsRecording { get; private set; }

        public bool IsValid => disposed == false;

        public event IAudioFilter.OnAudioDelegate? AudioRead;

        static MicrophoneAudioFilter()
        {
            RustAudioClient.OnAudioRead += AudioCallback;
        }

        private MicrophoneAudioFilter(uint sampleRate, uint channels, string name, RustAudioSource native)
        {
            SampleRate = sampleRate;
            Channels = channels;
            Name = name;
            this.native = native;
        }

        public static Result<MicrophoneAudioFilter> New(string? microphoneName = null)
        {
            if (instance != null)
                return Result<MicrophoneAudioFilter>.ErrorResult("Only single instance allowed per time");

            Result<string[]> deviceNames = RustAudioClient.AvailableDeviceNames();
            if (deviceNames.Success == false)
            {
                return Result<MicrophoneAudioFilter>.ErrorResult(
                    $"Cannot get device names: {deviceNames.ErrorMessage}");
            }

            if (deviceNames.Value.Length == 0)
            {
                return Result<MicrophoneAudioFilter>.ErrorResult(
                    "No available input devices");
            }

            string name;

            if (microphoneName == null)
                name = deviceNames.Value.First();
            else
            {
                if (deviceNames.Value.Any(m => m == microphoneName))
                    name = microphoneName;
                else
                    return Result<MicrophoneAudioFilter>.ErrorResult($"No microphone named: {microphoneName}");
            }

            Result<RustAudioSource> source = RustAudioClient.NewStream(name);

            if (source.Success == false)
            {
                return Result<MicrophoneAudioFilter>.ErrorResult($"Cannot create new stream: {source.ErrorMessage}");
            }

            var rustSource = source.Value;

            instance = new MicrophoneAudioFilter(
                rustSource.sampleRate,
                rustSource.channels,
                name,
                rustSource
            );

            return Result<MicrophoneAudioFilter>.SuccessResult(instance);
        }

        public static string[] AvailableDeviceNamesOrEmpty()
        {
            var result = RustAudioClient.AvailableDeviceNames();
            return result.Success ? result.Value : Array.Empty<string>();
        }

        private static void AudioCallback(Span<float> data)
        {
            instance?.AudioRead?.Invoke(data, (int)instance.Channels, (int)instance.SampleRate);
        }

        public void StartCapture()
        {
            native.StartCapture();
        }

        public void StopCapture()
        {
            native.PauseCapture();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            native.Dispose();
        }
    }
}