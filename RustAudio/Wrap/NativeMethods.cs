using System;
using System.Runtime.InteropServices;
using RichTypes;
using UnityEngine;

namespace RustAudio
{
    public static class NativeMethods
    {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN || PLATFORM_STANDALONE_WIN
        private const string EXTENSION = ".dll";
#else
    private const string EXTENSION = ".dylib";
#endif

        private const string LIB = "rust_audio" + EXTENSION;

        [StructLayout(LayoutKind.Sequential)]
        public struct DeviceNamesResult
        {
            public IntPtr names; // *const *const c_char
            public int length; // i32
            public IntPtr errorMessage; // *const c_char
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct InputStreamResult
        {
            public IntPtr streamPtr; // *mut c_void
            public uint sampleRate; // u32
            public uint channels; // u32
            public IntPtr errorMessage; // *const c_char
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ResultFFI
        {
            public IntPtr errorMessage; // *const c_char
        }


        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void AudioCallback(IntPtr data, int length);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void ErrorCallback(IntPtr message);


        [DllImport(LIB, CallingConvention = CallingConvention.Cdecl)]
        public static extern DeviceNamesResult rust_audio_input_device_names();

        [DllImport(LIB, CallingConvention = CallingConvention.Cdecl)]
        public static extern void rust_audio_free_c_char_array(IntPtr ptr, UIntPtr len);

        [DllImport(LIB, CallingConvention = CallingConvention.Cdecl)]
        public static extern void rust_audio_free(IntPtr ptr);

        [DllImport(LIB, CallingConvention = CallingConvention.Cdecl)]
        public static extern InputStreamResult rust_audio_input_stream_new(
            [MarshalAs(UnmanagedType.LPStr)] string deviceName,
            AudioCallback audioCallback,
            ErrorCallback errorCallback
        );

        [DllImport(LIB, CallingConvention = CallingConvention.Cdecl)]
        public static extern ResultFFI rust_audio_input_stream_start(IntPtr streamPtr);

        [DllImport(LIB, CallingConvention = CallingConvention.Cdecl)]
        public static extern ResultFFI rust_audio_input_stream_pause(IntPtr streamPtr);

        public static Option<string> PtrToStringAndFree(IntPtr ptr)
        {
            if (ptr == IntPtr.Zero)
            {
                Debug.LogWarning("FFI returned null pointer");
                return Option<string>.None;
            }

            string result = Marshal.PtrToStringAnsi(ptr);
            rust_audio_free(ptr);
            return Option<string>.Some(result);
        }

        public static Result<string[]> GetDeviceNames()
        {
            var res = rust_audio_input_device_names();

            var error = PtrToStringAndFree(res.errorMessage);
            if (error.Has)
            {
                return Result<string[]>.ErrorResult(error.Value);
            }

            var result = new string[res.length];
            var ptrArray = new IntPtr[res.length];
            Marshal.Copy(res.names, ptrArray, 0, res.length);

            for (int i = 0; i < res.length; i++)
                result[i] = Marshal.PtrToStringAnsi(ptrArray[i]);

            rust_audio_free_c_char_array(res.names, (UIntPtr)res.length);

            return Result<string[]>.SuccessResult(result);
        }
    }
}