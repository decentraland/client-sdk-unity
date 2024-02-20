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
    public class CameraVideoSource : RtcVideoSource
    {
        public Camera Camera { get; }

        public override int GetWidth()
        {
            return Camera.pixelWidth;
        }

        public override int GetHeight()
        {
            return Camera.pixelHeight;
        }

        public CameraVideoSource(Camera camera, VideoBufferType bufferType = VideoBufferType.Rgba) : base(VideoStreamSource.Screen, bufferType)
        {
            Camera = camera;
            Camera.onPostRender += OnCameraPostRender;
            //camera.targetTexture = new RenderTexture(GetWidth(), GetHeight(), 0); ;
            //_dest = camera.targetTexture;
            //_data = new NativeArray<byte>(GetWidth() * GetHeight() * GetStrideForBuffer(bufferType), Allocator.Persistent);
        }

        private void OnCameraPostRender(Camera cam)
        {
            if(_playing)
            {
                if (_dest == null)
                {
                    _dest = new Texture2D(GetWidth(), GetHeight(),  TextureFormat.RGBA64, false);
                }
                Debug.Log(GetTextureFormat(_bufferType) +" vs "+cam.activeTexture.graphicsFormat);
                Graphics.CopyTexture(cam.activeTexture, _dest);
            }
        }

        ~CameraVideoSource()
        {
            Dispose();
            ClearRenderTexture();
        }

        public override void Stop()
        {
            base.Stop();
            ClearRenderTexture();
        }

        private void ClearRenderTexture()
        {
            if (_dest)
            {
                var renderText = _dest as RenderTexture;
                renderText.Release(); // can only be done on main thread
            }
        }

        // Read the texture data into a native array asynchronously
        protected override void ReadBuffer()
        {
            if (_reading)
                return;
            _reading = true;

            IntPtr pointer = _dest.GetNativeTexturePtr();
            AsyncGPUReadback.RequestIntoNativeArray(ref _data, _dest, 0, GetTextureFormat(_bufferType), OnReadback);
        }
    }
}

