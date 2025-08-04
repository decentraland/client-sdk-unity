using System;
using AOT;
using RichTypes;
using UnityEditor;
using UnityEngine;

namespace RustAudio
{
    public static class RustAudioClient
    {
        public delegate void OnAudioDelegate(Span<float> data);

        public static event OnAudioDelegate OnAudioRead;

#if UNITY_EDITOR
        static RustAudioClient()
        {
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;
            Application.quitting += Quit;
        }

        static void OnBeforeAssemblyReload()
        {
            NativeMethods.rust_audio_deinit();
        }

        static void OnAfterAssemblyReload()
        {
            InitializeSdk();
        }
#else
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Init()
        {
            Application.quitting += Quit;
            InitializeSdk();
        }
#endif

        private static void Quit()
        {
#if NO_LIVEKIT_MODE
            return;
#endif
#if UNITY_EDITOR
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload -= OnAfterAssemblyReload;
#endif
        }

        private static void InitializeSdk()
        {
            var result = NativeMethods.rust_audio_init(AudioCallback, ErrorCallback);
            if (result.errorMessage != IntPtr.Zero)
            {
                Debug.LogError(
                    $"Cannot initialize rust audio: {NativeMethods.PtrToStringAndFree(result.errorMessage).Value}"
                );
            }
            else
            {
                Debug.Log("RustAudio initialized");
            }
        }

        public static void ForceReInit()
        {
            NativeMethods.rust_audio_deinit();
            Debug.Log("RustAudio deinitialized");
            InitializeSdk();
        }

        [MonoPInvokeCallback(typeof(NativeMethods.AudioCallback))]
        private static void AudioCallback(IntPtr data, int length)
        {
            unsafe
            {
                Span<float> span = new Span<float>(data.ToPointer(), length);
                OnAudioRead?.Invoke(span);
            }
        }

        [MonoPInvokeCallback(typeof(NativeMethods.ErrorCallback))]
        private static void ErrorCallback(IntPtr msg)
        {
            Option<string> error = NativeMethods.PtrToStringAndFree(msg);
            if (error.Has)
                Debug.LogError(error);
        }


        public static Result<string[]> AvailableDeviceNames()
        {
            return NativeMethods.GetDeviceNames();
        }


        public static Result<RustAudioSource> NewStream(string deviceName)
        {
            var result = NativeMethods.rust_audio_input_stream_new(deviceName);
            var error = NativeMethods.PtrToStringAndFree(result.errorMessage);
            if (error.Has)
            {
                return Result<RustAudioSource>.ErrorResult($"Cannot create new stream: {error.Value}");
            }

            return Result<RustAudioSource>.SuccessResult(
                new RustAudioSource(deviceName, result.streamPtr, result.sampleRate, result.channels)
            );
        }
    }

    public class RustAudioSource : IDisposable
    {
        private readonly IntPtr streamPtr;
        public readonly string name;
        public readonly uint sampleRate;
        public readonly uint channels;
        private bool disposed;

        public bool IsRecording { get; private set; }

        internal RustAudioSource(string name, IntPtr streamPtr, uint sampleRate, uint channels)
        {
            this.name = name;
            this.streamPtr = streamPtr;
            this.sampleRate = sampleRate;
            this.channels = channels;
            IsRecording = false;
        }


        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            IsRecording = false;
            NativeMethods.rust_audio_input_stream_free(streamPtr);
        }

        public void StartCapture()
        {
            var result = NativeMethods.rust_audio_input_stream_start(streamPtr);
            var message = NativeMethods.PtrToStringAndFree(result.errorMessage);
            if (message.Has)
            {
                Debug.LogError($"Cannot start microphone stream '{name}' due error: {message.Value}");
                return;
            }

            IsRecording = true;
        }

        public void PauseCapture()
        {
            var result = NativeMethods.rust_audio_input_stream_pause(streamPtr);
            var message = NativeMethods.PtrToStringAndFree(result.errorMessage);
            if (message.Has)
            {
                Debug.LogError($"Cannot pause microphone stream '{name}' due error: {message.Value}");
                return;
            }

            IsRecording = false;
        }
    }
}