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
        protected VideoSourceInfo _info;

        public RtcVideoSource(VideoStreamSource sourceType)
        {
            _sourceType = sourceType;
            using var request = FFIBridge.Instance.NewRequest<NewVideoSourceRequest>();
            var newVideoSource = request.request;
            newVideoSource.Type = VideoSourceType.VideoSourceNative;
            using var response = request.Send();
            FfiResponse res = response;
            _info = res.NewVideoSource.Source.Info;
            Handle = new FfiHandle((IntPtr)res.NewVideoSource.Source.Handle.Id);
        }
    }

    public class TextureVideoSource : RtcVideoSource
    {
        protected Texture _dest;
        public Texture Texture { get; }
        private NativeArray<byte> _data;
        private bool _reading = false;
        private bool isDisposed = true;
        //private Thread? readVideoThread;
        private bool _playing = false;
        private RawImage _debugRenderer;

        public int GetWidth()
        {
            switch(_sourceType)
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

        public TextureVideoSource(VideoStreamSource sourceType, Texture texture, RawImage debugRenderer = null) : base(sourceType)
        {
            _debugRenderer = debugRenderer;
            //NativeLeakDetection.Mode = NativeLeakDetectionMode.Enabled;
            Texture = texture;
            _data = new NativeArray<byte>(GetWidth()* GetHeight() * 4, Allocator.Persistent);
            Debug.LogError("Using: " + GetWidth() + " by " + GetHeight());
            isDisposed = false;
        }

        public void Start()
        {
            Stop();
            _playing = true;
            //readVideoThread = new Thread(Update);
            //readVideoThread.Start();
        }

        public void Stop()
        {
            _playing = false;
            //readVideoThread?.Abort();
        }

        ~TextureVideoSource()
        {
            if (!isDisposed)
            {
                _data.Dispose();
                isDisposed = true;
            }
        }

        int count;
        public void Update()
        {
            count++;
            //while (true)
            //{
            //    Thread.Sleep(Constants.TASK_DELAY);
            if (_playing && count % 100 == 0)
            {
                ReadBuffer();
                ReadBack();
            }
            //}
        }

        // Read the texture data into a native array asynchronously
        internal void ReadBuffer()
        {
            if (_reading)
                return;

            _reading = true;
            var gpuTextureFormat = TextureFormat.RGBA32;
            switch (_sourceType) // todo: to different classes?
            {
                case VideoStreamSource.Screen:
                    if (_dest == null) _dest = new RenderTexture(GetWidth(), GetHeight(), 0);
                    ScreenCapture.CaptureScreenshotIntoRenderTexture(_dest as RenderTexture);

//                    var graphicDevice = SystemInfo.graphicsDeviceType;
//                    var flipY = graphicDevice == GraphicsDeviceType.OpenGLCore ||
//graphicDevice == GraphicsDeviceType.OpenGLES2 ||
//graphicDevice == GraphicsDeviceType.OpenGLES3 ||
//graphicDevice == GraphicsDeviceType.Vulkan ?
//false :
//true;
                    break;
                default:
                    if (!SystemInfo.IsFormatSupported(Texture.graphicsFormat, FormatUsage.ReadPixels))
                    {
                        Debug.Log("Copy into texture");
                        if (_dest == null)
                        {
                            Debug.Log("Creating texture 2d");
                            gpuTextureFormat = TextureFormat.RGBA32;
                            _dest = new Texture2D(Texture.width, Texture.height, gpuTextureFormat, false);
                        }
                        Graphics.CopyTexture(Texture, _dest);
                    }
                    else
                    {
                        Debug.Log("Using original");
                        _dest = Texture;
                    }
                    break;
            }
            IntPtr pointer = _dest.GetNativeTexturePtr();
            Debug.Log("What is " + _dest + " and " + _dest.graphicsFormat);
           
            AsyncGPUReadback.RequestIntoNativeArray(ref _data, _dest, 0, gpuTextureFormat, OnReadback);
        }

        private bool _requestPending = false;

        private void OnReadback(AsyncGPUReadbackRequest req)
        {
            if (!req.hasError)
            {
                _requestPending = true;
            } else
            {
                Debug.Log("Read Back Failed: " + req.ToString());
                _reading = false;
            }
        }

        private void ReadBack()
        {
            if (_requestPending && !isDisposed)
            {
                // ToI420
                //ToArgbRequest
                using var requestToI420 = FFIBridge.Instance.NewRequest<ToI420Request>();
                //using var requestToArgb = FFIBridge.Instance.NewRequest<ToArgbRequest>();
                using var argbInfoWrap = requestToI420.TempResource<ArgbBufferInfo>();

                var argbInfo = argbInfoWrap.value;
                //FfiHandle datahandle;
                unsafe
                {
                    argbInfo.Ptr = (ulong)NativeArrayUnsafeUtility.GetUnsafePtr(_data);

                   // datahandle = new FfiHandle((IntPtr)NativeArrayUnsafeUtility.GetUnsafePtr(_data));
                }
                
                argbInfo.Format = VideoFormatType.FormatArgb; // note: format FormatRgba throws exception as not being supported
                argbInfo.Stride = (uint)GetWidth()* 4;
                argbInfo.Width = (uint)GetWidth();
                argbInfo.Height = (uint)GetHeight();

                var toI420 = requestToI420.request;
                toI420.FlipY = true;
                toI420.Argb = argbInfo;
                using var responseToI420 = requestToI420.Send();
                FfiResponse res = responseToI420;

                var bufferInfo = res.ToI420.Buffer;
                var buffer = VideoFrameBuffer.Create(new FfiHandle((IntPtr)bufferInfo.Handle.Id), bufferInfo.Info);


                // Send the frame to WebRTC

                using var request = FFIBridge.Instance.NewRequest<CaptureVideoFrameRequest>();
                using var frameInfoWrap = request.TempResource<VideoFrameInfo>();

                var frameInfo = frameInfoWrap.value;
                frameInfo.Rotation = VideoRotation._0;
                frameInfo.TimestampUs = DateTimeOffset.Now.ToUnixTimeMilliseconds();

                var capture = request.request;
                capture.SourceHandle = (ulong)Handle.DangerousGetHandle();
                capture.Handle = (ulong)buffer.Handle.DangerousGetHandle();
                capture.Frame = frameInfo;
                Debug.Log("Sending Frame");
                using var response = request.Send();

                //FfiResponse captureFrameRes = response;
                //captureFrameRes.CaptureVideoFrame.;
                _reading = false;
                _requestPending = false;
                buffer.Handle.Dispose();
                //_data.Dispose();
                switch (_sourceType)
                {
                    case VideoStreamSource.Screen:
                        var renderText = _dest as RenderTexture;
                        renderText.Release();
                        break;
                }
            }
        }

    }
}
