using System;
using System.Collections;
using UnityEngine;
using LiveKit.Proto;
using LiveKit.Internal;
using UnityEngine.Rendering;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace LiveKit
{
    public abstract class RtcVideoSource
    {
        private FfiHandle _handle;

        internal FfiHandle Handle
        {
            get { return _handle; }
        }

        protected VideoSourceInfo _info;

        public RtcVideoSource()
        {
            var newVideoSource = new NewVideoSourceRequest();
            newVideoSource.Type = VideoSourceType.VideoSourceNative;

            using var resp = FfiClient.Instance.SendRequest(
                r => r.NewVideoSource = newVideoSource,
                r => r.NewVideoSource = null
            );
            FfiResponse res = resp;

            _info = res.NewVideoSource.Source.Info;
            _handle = new FfiHandle((IntPtr)res.NewVideoSource.Source.Handle.Id);
        }
    }

    public class TextureVideoSource : RtcVideoSource
    {
        public Texture Texture { get; }
        private NativeArray<byte> data;
        private bool reading = false;
        private bool isDisposed = true;
        private Thread? readVideoThread;

        public TextureVideoSource(Texture texture)
        {
            Texture = texture;
            data = new NativeArray<byte>(Texture.width * Texture.height * 4, Allocator.Persistent);
            isDisposed = false;
        }

        public void Start()
        {
            Stop();
            readVideoThread = new Thread(Update);
            readVideoThread.Start();
        }

        public void Stop()
        {
            readVideoThread?.Abort();
        }

        ~TextureVideoSource()
        {
            if (!isDisposed)
            {
                data.Dispose();
                isDisposed = true;
            }
        }


        private void Update()
        {
            while (true)
            {
                Thread.Sleep(Constants.TASK_DELAY);
                ReadBuffer();
                ReadBack();
            }
        }

        // Read the texture data into a native array asynchronously
        internal void ReadBuffer()
        {
            if (reading)
                return;

            reading = true;
            AsyncGPUReadback.RequestIntoNativeArray(ref data, Texture, 0, TextureFormat.RGBA32, OnReadback);
        }

        private AsyncGPUReadbackRequest _readBackRequest;
        private bool _requestPending = false;

        private void OnReadback(AsyncGPUReadbackRequest req)
        {
            _readBackRequest = req;
            _requestPending = true;
        }

        private void ReadBack()
        {
            if (_requestPending && !isDisposed)
            {
                var req = _readBackRequest;
                if (req.hasError)
                {
                    Utils.Error("failed to read texture data");
                    return;
                }

                // ToI420
                var argbInfo = new ArgbBufferInfo(); // TODO: MindTrust_VID
                unsafe
                {
                    argbInfo.Ptr = (ulong)NativeArrayUnsafeUtility.GetUnsafePtr(data);
                }

                argbInfo.Format = VideoFormatType.FormatArgb;
                argbInfo.Stride = (uint)Texture.width * 4;
                argbInfo.Width = (uint)Texture.width;
                argbInfo.Height = (uint)Texture.height;

                var toI420 = new ToI420Request();
                toI420.FlipY = true;
                toI420.Argb = argbInfo;

                using var resp = FfiClient.Instance.SendRequest(
                    r => r.ToI420 = toI420,
                    r => r.ToI420 = null
                );
                FfiResponse res = resp;

                var bufferInfo = res.ToI420.Buffer;
                var buffer = VideoFrameBuffer.Create(new FfiHandle((IntPtr)bufferInfo.Handle.Id), bufferInfo.Info);

                // Send the frame to WebRTC
                var frameInfo = new VideoFrameInfo();
                frameInfo.Rotation = VideoRotation._0;
                frameInfo.TimestampUs = DateTimeOffset.Now.ToUnixTimeMilliseconds();

                var capture = new CaptureVideoFrameRequest();
                capture.SourceHandle = (ulong)Handle.DangerousGetHandle();
                capture.Handle = (ulong)buffer.Handle.DangerousGetHandle();
                capture.Frame = frameInfo;

                using var response = FfiClient.Instance.SendRequest(
                    r => r.CaptureVideoFrame = capture,
                    r => r.CaptureVideoFrame = null
                );
                reading = false;
                _requestPending = false;
            }
        }
    }
}