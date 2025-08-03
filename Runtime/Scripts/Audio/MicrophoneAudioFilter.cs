using System;
using AOT;
using LiveKit.Internal;
using RichTypes;
using UnityEngine;
using NativeMethods = RustAudio.NativeMethods;

namespace LiveKit.Scripts.Audio
{
    public class MicrophoneAudioFilter : IAudioFilter, IDisposable
    {
        public readonly uint SampleRate;
        public readonly uint Channels;
        public readonly string Name;
        private readonly IntPtr native;

        private bool disposed;

        private static volatile MicrophoneAudioFilter? instance;

        public bool IsRecording { get; private set; }
        
        public bool IsValid => disposed;

        public event IAudioFilter.OnAudioDelegate? AudioRead;

        private MicrophoneAudioFilter(uint sampleRate, uint channels, string name, IntPtr native)
        {
            SampleRate = sampleRate;
            Channels = channels;
            Name = name;
            this.native = native;
        }

        public static Result<MicrophoneAudioFilter> New()
        {
            if (instance != null)
                return Result<MicrophoneAudioFilter>.ErrorResult("Only single instance allowed per time");

            Result<string[]> deviceNames = NativeMethods.GetDeviceNames();
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

            string name = deviceNames.Value[0];

            NativeMethods.InputStreamResult stream =
                NativeMethods.rust_audio_input_stream_new(name, AudioCallback, ErrorCallback);

            var error = NativeMethods.PtrToStringAndFree(stream.errorMessage);
            return error.Has
                ? Result<MicrophoneAudioFilter>.ErrorResult($"Cannot create new stream: {error.Value}")
                : Result<MicrophoneAudioFilter>.SuccessResult(
                    new MicrophoneAudioFilter(
                        stream.sampleRate,
                        stream.channels,
                        name,
                        stream.streamPtr
                    )
                );
        }

        [MonoPInvokeCallback(typeof(NativeMethods.AudioCallback))]
        private static void AudioCallback(IntPtr data, int length)
        {
            if (instance == null)
                return;

            unsafe
            {
                Span<float> span = new Span<float>(data.ToPointer(), length);
                instance.AudioRead?.Invoke(span, (int)instance.Channels, (int)instance.SampleRate);
            }
        }

        [MonoPInvokeCallback(typeof(NativeMethods.ErrorCallback))]
        private static void ErrorCallback(IntPtr msg)
        {
            Option<string> error = NativeMethods.PtrToStringAndFree(msg);
            if (error.Has)
                Debug.LogError(error);
        }

        public void StartCapture()
        {
            var result = NativeMethods.rust_audio_input_stream_start(native);
            var message = NativeMethods.PtrToStringAndFree(result.errorMessage);
            if (message.Has)
            {
                Utils.Error($"Cannot start microphone stream '{Name}' due error: {message.Value}");
                return;
            }
            IsRecording = true;    
        }

        public void StopCapture()
        {
            var result = NativeMethods.rust_audio_input_stream_pause(native);
            var message = NativeMethods.PtrToStringAndFree(result.errorMessage);
            if (message.Has)
            {
                Utils.Error($"Cannot pause microphone stream '{Name}' due error: {message.Value}");
                return;
            }
            IsRecording = false;    
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            IsRecording = false;    
            instance = null;
            NativeMethods.rust_audio_free(native);
        }
    }
}