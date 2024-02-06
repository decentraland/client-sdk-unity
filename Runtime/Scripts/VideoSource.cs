using System;
using UnityEngine;
using LiveKit.Proto;
using LiveKit.Internal;
using UnityEngine.Rendering;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using System.Threading;
using LiveKit.Internal.FFIClients.Requests;

namespace LiveKit
{
    public abstract class RtcVideoSource
    {
        internal FfiHandle Handle { get; }

        protected VideoSourceInfo _info;


        public RtcVideoSource()
        {
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
        public Texture Texture { get; }
        private NativeArray<byte> _data;
        private bool _reading = false;
        private bool isDisposed = true;
        //private Thread? readVideoThread;
        private bool _playing = false;

        public TextureVideoSource(Texture texture)
        {
            Texture = texture;
            _data = new NativeArray<byte>(Texture.width * Texture.height * 4, Allocator.Persistent);
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
            if (_playing && count%300==0)
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

            Debug.Log("Request into native array "+Texture.graphicsFormat);

            IntPtr pointer = Texture.GetNativeTexturePtr();
          

            _reading = true;
            //Texture.graphicsFormat
            // try once then
            Texture2D dest = new Texture2D(Texture.width, Texture.height, TextureFormat.ARGB32, false);
            ////dest.UpdateExternalTexture(pointer);
            //Debug.Log("Dest type? "+dest.graphicsFormat);
            Graphics.CopyTexture(Texture, dest);
            //Graphics.Blit
            AsyncGPUReadback.RequestIntoNativeArray(ref _data, dest, 0, TextureFormat.ARGB32, OnReadback);
        }

        private bool _requestPending = false;

        private void OnReadback(AsyncGPUReadbackRequest req)
        {
            if (!req.hasError)
            {
                _requestPending = true;
            } else
            {
                Debug.Log("Read Back Failed: "+req.ToString());
                _reading = false;
            }
        }

        private void ReadBack()
        {
            if (_requestPending && !isDisposed)
            {
                // ToI420
                var argbInfo = new ArgbBufferInfo(); // TODO: MindTrust_VID
                unsafe
                {
                    argbInfo.Ptr = (ulong)NativeArrayUnsafeUtility.GetUnsafePtr(_data);
                }

                argbInfo.Format = VideoFormatType.FormatArgb;
                argbInfo.Stride = (uint)Texture.width * 4;
                argbInfo.Width = (uint)Texture.width;
                argbInfo.Height = (uint)Texture.height;

                using var requestToI420 = FFIBridge.Instance.NewRequest<ToI420Request>();
                var toI420 = requestToI420.request;
                toI420.FlipY = true;
                toI420.Argb = argbInfo;
                using var responseToI420 = requestToI420.Send();
                FfiResponse res = responseToI420;

                var bufferInfo = res.ToI420.Buffer;
                var buffer = VideoFrameBuffer.Create(new FfiHandle((IntPtr)bufferInfo.Handle.Id), bufferInfo.Info);

                // Send the frame to WebRTC
                var frameInfo = new VideoFrameInfo();
                frameInfo.Rotation = VideoRotation._0;
                frameInfo.TimestampUs = DateTimeOffset.Now.ToUnixTimeMilliseconds();


                using var request = FFIBridge.Instance.NewRequest<CaptureVideoFrameRequest>();
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
            }
        }

    }
}