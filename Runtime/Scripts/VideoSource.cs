using System;
using UnityEngine;
using LiveKit.Proto;
using LiveKit.Internal;
using UnityEngine.Rendering;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using System.Threading;
using LiveKit.Internal.FFIClients.Requests;
using UnityEngine.Experimental.Rendering;
using UnityEngine.UI;
using System.Threading.Tasks;

namespace LiveKit
{
    public abstract class RtcVideoSource
    {
        public enum VideoStreamSource
        {
            WebCamera = 0,
            Screen = 1
        }

        internal FfiHandle Handle { get; }
        protected VideoStreamSource _sourceType;
        protected VideoBufferType _bufferType;
        protected VideoSourceInfo _info;

        public RtcVideoSource(VideoStreamSource sourceType, VideoBufferType bufferType)
        {
            _sourceType = sourceType;
            _bufferType = bufferType;
            using var request = FFIBridge.Instance.NewRequest<NewVideoSourceRequest>();
            var newVideoSource = request.request;
            newVideoSource.Type = VideoSourceType.VideoSourceNative;
            using var response = request.Send();
            FfiResponse res = response;
            _info = res.NewVideoSource.Source.Info;
            Handle = new FfiHandle((IntPtr)res.NewVideoSource.Source.Handle.Id);
        }

        protected TextureFormat GetTextureFormat(VideoBufferType type)
        {
            switch (type)
            {
                case VideoBufferType.Rgba:
                    return TextureFormat.RGBA32;
                case VideoBufferType.Argb:
                    return TextureFormat.ARGB32;
                default:
                    throw new NotImplementedException("TODO: Add TextureFormat support for type: " + type);
            }
        }

        protected int GetStrideForBuffer(VideoBufferType type)
        {
            switch (type)
            {
                case VideoBufferType.Rgba:
                case VideoBufferType.Argb:
                    return 4;
                default:
                    throw new NotImplementedException("TODO: Add stride support for type: " + type);
            }
        }
    }

    public class TextureVideoSource : RtcVideoSource
    {
        protected Texture _dest;
        public Texture Texture { get; }
        private NativeArray<byte> _data;
        private bool _reading = false;
        private bool isDisposed = true;
        //private Thread? sendThread;
        private bool _playing = false;

        public int GetWidth()
        {
            switch (_sourceType)
            {
                case VideoStreamSource.Screen:
                    return Screen.width;
                default:
                    return Texture.width;
            }
        }

        public int GetHeight()
        {
            switch (_sourceType)
            {
                case VideoStreamSource.Screen:
                    return Screen.height;
                default:
                    return Texture.height;
            }
        }

        public TextureVideoSource(VideoStreamSource sourceType, Texture texture = null, VideoBufferType bufferType = VideoBufferType.Rgba) : base(sourceType, bufferType)
        {
            Texture = texture;
            _data = new NativeArray<byte>(GetWidth() * GetHeight() * GetStrideForBuffer(bufferType), Allocator.Persistent);
            isDisposed = false;
        }

        public void Start()
        {
            Stop();
            _playing = true;
            //sendThread = new Thread(SendFrameThread);
            //sendThread.Start();
        }

        public void Stop()
        {
            _playing = false;
            //sendThread?.Abort();
            ClearRenderTexture();
        }

        ~TextureVideoSource()
        {
            if (!isDisposed)
            {
                ClearRenderTexture();
                _data.Dispose();
                isDisposed = true;
            }
        }

        private void ClearRenderTexture()
        {
            if (_dest)
            {
                switch (_sourceType)
                {
                    case VideoStreamSource.Screen:
                        var renderText = _dest as RenderTexture;
                        renderText.Release(); // can only be done on main thread
                        break;
                }
            }
        }

        public void Update()
        {
            if (_playing)
            {
                ReadBuffer();
                SendFrame();
            }
        }

        //protected void SendFrameThread()
        //{
        //    while (true)
        //    {
        //        Thread.Sleep(Constants.TASK_DELAY);
        //        if (_playing)
        //        {
        //            SendFrame();
        //        }
        //    }
        //}

        // Read the texture data into a native array asynchronously
        internal void ReadBuffer()
        {
            if (_reading)
                return;
            _reading = true;
            var gpuTextureFormat = GetTextureFormat(_bufferType); // currently is always this... may need to be dynamic for other platforms 
            switch (_sourceType)
            {
                case VideoStreamSource.Screen:
                    if (_dest == null) _dest = new RenderTexture(GetWidth(), GetHeight(), 0);
                    ScreenCapture.CaptureScreenshotIntoRenderTexture(_dest as RenderTexture);
                    break;
                default:
                    if (!SystemInfo.IsFormatSupported(Texture.graphicsFormat, FormatUsage.ReadPixels))
                    {
                        if (_dest == null)
                        {
                            _dest = new Texture2D(Texture.width, Texture.height, gpuTextureFormat, false);
                        }
                        Graphics.CopyTexture(Texture, _dest);
                    }
                    else
                    {
                        _dest = Texture;
                    }
                    break;
            }
            IntPtr pointer = _dest.GetNativeTexturePtr();

            AsyncGPUReadback.RequestIntoNativeArray(ref _data, _dest, 0, gpuTextureFormat, OnReadback);
        }

        private bool _requestPending = false;

        private void OnReadback(AsyncGPUReadbackRequest req)
        {
            if (!req.hasError)
            {
                _requestPending = true;
            }
            else
            {
                Utils.Error("GPU Read Back on Video Source Failed: " + req.ToString());
                _reading = false;
            }
        }

        private void SendFrame()
        {
            if (_requestPending && !isDisposed)
            {
                var buffer = new VideoBufferInfo();
                unsafe
                {
                    buffer.DataPtr = (ulong)NativeArrayUnsafeUtility.GetUnsafePtr(_data);
                }

                buffer.Type = _bufferType;
                buffer.Stride = (uint)GetWidth() * (uint)GetStrideForBuffer(_bufferType);
                buffer.Width = (uint)GetWidth();
                buffer.Height = (uint)GetHeight();

                // Send the frame to WebRTC
                using var request = FFIBridge.Instance.NewRequest<CaptureVideoFrameRequest>();
                var capture = request.request;
                capture.SourceHandle = (ulong)Handle.DangerousGetHandle();
                capture.Rotation = VideoRotation._0;
                capture.TimestampUs = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                capture.Buffer = buffer;
                using var response = request.Send();
                _reading = false;
                _requestPending = false;
                ClearRenderTexture();
            }
        }

    }
}

