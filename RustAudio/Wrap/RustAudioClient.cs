using System;
using AOT;
using RichTypes;
using UnityEditor;
using UnityEngine;

namespace RustAudio
{
    public delegate void OnStreamAudioDelegate(Span<float> data);

    public static class RustAudioClient
    {
        public delegate void OnAudioDelegate(ulong streamId, Span<float> data);

        internal static event OnAudioDelegate OnAudioRead;

#if UNITY_EDITOR
        static RustAudioClient()
        {
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;
            Application.quitting += Quit;
        }

        static void OnBeforeAssemblyReload()
        {
            Debug.Log(nameof(OnBeforeAssemblyReload));
            DeInit();
        }

        static void OnAfterAssemblyReload()
        {
            Debug.Log(nameof(OnAfterAssemblyReload));
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
            DeInit();
            InitializeSdk();
        }

        public static void DeInit()
        {
            NativeMethods.rust_audio_deinit();
            Debug.Log("RustAudio deinitialized");
        }

        public static SystemStatus SystemStatus()
        {
            return NativeMethods.rust_audio_status();
        }

        [MonoPInvokeCallback(typeof(NativeMethods.AudioCallback))]
        private static void AudioCallback(ulong streamId, IntPtr data, int length)
        {
            unsafe
            {
                Span<float> span = new Span<float>(data.ToPointer(), length);
                OnAudioRead?.Invoke(streamId, span);
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
            var status = SystemStatus();
            if (status.hasAudioCallback == false || status.hasErrorCallback == false)
            {
                Debug.LogWarning("Callbacks are missing, initialize sdk");
                InitializeSdk();
            }

            var result = NativeMethods.rust_audio_input_stream_new(deviceName);
            var error = NativeMethods.PtrToStringAndFree(result.errorMessage);
            if (error.Has)
            {
                return Result<RustAudioSource>.ErrorResult($"Cannot create new stream: {error.Value}");
            }

            return Result<RustAudioSource>.SuccessResult(
                new RustAudioSource(deviceName, result.streamId, result.sampleRate, result.channels)
            );
        }
    }


    public class RustAudioSource : IDisposable
    {
        private readonly ulong streamId;
        public readonly string name;
        public readonly uint sampleRate;
        public readonly uint channels;
        private bool disposed;

        public event OnStreamAudioDelegate AudioRead;

        public bool IsRecording { get; private set; }

        internal RustAudioSource(string name, ulong streamId, uint sampleRate, uint channels)
        {
            this.name = name;
            this.streamId = streamId;
            this.sampleRate = sampleRate;
            this.channels = channels;
            IsRecording = false;

            RustAudioClient.OnAudioRead += RustAudioClientOnOnAudioRead;

            Debug.Log("RustAudioSource new");
        }

        private void RustAudioClientOnOnAudioRead(ulong stream, Span<float> data)
        {
            if (streamId == stream)
            {
                AudioRead?.Invoke(data);
            }
        }


        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            IsRecording = false;
            NativeMethods.rust_audio_input_stream_free(streamId);
            Debug.Log("RustAudioSource disposed");
        }

        public void StartCapture()
        {
            if (IsRecording)
                return;

            Debug.Log("RustAudioSource start");
            var result = NativeMethods.rust_audio_input_stream_start(streamId);
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
            if (IsRecording == false)
                return;

            Debug.Log("RustAudioSource pause");
            var result = NativeMethods.rust_audio_input_stream_pause(streamId);
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