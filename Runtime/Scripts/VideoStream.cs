using System;
using LiveKit.Internal;
using LiveKit.Proto;
using UnityEngine;
using Unity.Collections.LowLevel.Unsafe;
using System.Threading;
using LiveKit.Internal.FFIClients.Requests;

namespace LiveKit
{
    public sealed class VideoFrameEvent
    {
        private VideoFrame _frame;
        internal VideoFrame Frame => _frame;
        private long _timestamp;
        internal long Timestamp => _timestamp;
        private VideoRotation _rotation;
        internal VideoRotation Rotation => _rotation;

       

        public VideoFrameEvent(VideoFrame frame, long timeStamp, VideoRotation rot)
        {
            _frame = frame;
            _timestamp = timeStamp;
            _rotation = rot;
        }

        public bool IsValid
        {
            get
            {
                if(_frame != null)
                {
                    return _frame.IsValid;
                }
                return false;
            }
        }

        public void Dispose()
        {
            _frame?.Dispose();
        }
    }

    public class VideoStream
    {
        public delegate void FrameReceiveDelegate(VideoFrameEvent frameEvent);


        public delegate void TextureReceiveDelegate(Texture2D tex2d);


        public delegate void TextureUploadDelegate();


        internal FfiHandle Handle { get; private set; }

        private VideoStreamInfo _info;
        private bool _dirty = false;
        private bool _playing = false;
        private volatile bool disposed = false;

        // Thread for parsing textures
        //private Thread? frameThread;

        /// Called when we receive a new frame from the VideoTrack
        public event FrameReceiveDelegate FrameReceived;

        /// Called when we receive a new texture (first texture or the resolution changed)
        public event TextureReceiveDelegate TextureReceived;

        /// Called when we upload the texture to the GPU
        public event TextureUploadDelegate TextureUploaded;

        /// The texture changes every time the video resolution changes.
        /// Can be null if UpdateRoutine isn't started
        public Texture2D Texture { private set; get; }
        public VideoFrameEvent? VideoBuffer { get; private set; }
        
        private readonly object _lock = new();

        public VideoStream(IVideoTrack videoTrack, VideoBufferType format)
        {
            if (!videoTrack.Room.TryGetTarget(out var room))
                throw new InvalidOperationException("videotrack's room is invalid");

            if (!videoTrack.Participant.TryGetTarget(out var participant))
                throw new InvalidOperationException("videotrack's participant is invalid");
             

            using var request = FFIBridge.Instance.NewRequest<NewVideoStreamRequest>();
            var newVideoStream = request.request;
            newVideoStream.TrackHandle = (ulong)videoTrack.Handle.DangerousGetHandle();
            newVideoStream.Format = format;
            newVideoStream.NormalizeStride = true;
            newVideoStream.Type = VideoStreamType.VideoStreamNative;
            using var response = request.Send();
            FfiResponse res = response;
            var streamInfo = res.NewVideoStream.Stream;

            Handle = new FfiHandle((IntPtr)streamInfo.Handle.Id);
            FfiClient.Instance.VideoStreamEventReceived += OnVideoStreamEvent;
        }

        ~VideoStream()
        {
            Dispose(false);
        }

        public void Start()
        {
            Stop();
            _playing = true;
            //frameThread = new Thread(GetFrame);
            //frameThread.Start();
        }

        public void Stop()
        {
            _playing = false;
            //frameThread?.Abort();
        }


        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (!disposed)
            {
                if (disposing)
                    VideoBuffer?.Dispose();

                disposed = true;
            }
        }


        public void Update()
        { 
            if (!_playing || disposed) return;
            //{

            //    lock (_lock)
            //    {
            //        if (VideoBuffer == null || !VideoBuffer.IsValid || !_dirty)
            //            return;

            //        _dirty = false;
            //        var rWidth = VideoBuffer.Frame.Width;
            //        var rHeight = VideoBuffer.Frame.Height;

            //        var textureChanged = false;
            //        if (Texture == null || Texture.width != rWidth || Texture.height != rHeight)
            //        {
            //            Texture = new Texture2D((int)rWidth, (int)rHeight, TextureFormat.ARGB32, true, true);
            //            textureChanged = true;
            //        }

            //        var textureData = Texture.GetRawTextureData<byte>();
            //        unsafe
            //        {
            //            var destPtr = NativeArrayUnsafeUtility.GetUnsafePtr(textureData);

            //            //VideoBuffer.Frame.Info.DataPtr

            //            //VideoBuffer.ToARGB(VideoFormatType.FormatAbgr, (IntPtr)destPtr, (uint)Texture.width * 4, (uint)Texture.width,
            //            //    (uint)Texture.height);
            //        }
            //        //Texture.LoadRawTextureData(data);

            //        Debug.LogError("Apply new text");
            //        Texture.Apply();

            //        if (textureChanged)
            //            TextureReceived?.Invoke(Texture);

            //        TextureUploaded?.Invoke();
            //    }
            //}
        }

        private void OnVideoStreamEvent(VideoStreamEvent e)
        {
            if (e.StreamHandle != (ulong)Handle.DangerousGetHandle())
                return;

            if (e.MessageCase != VideoStreamEvent.MessageOneofCase.FrameReceived)
                return;

              
            var bufferInfo = e.FrameReceived.Buffer.Info;
            var handle = new FfiHandle((IntPtr)e.FrameReceived.Buffer.Handle.Id);

            var frame = VideoFrame.FromOwnedInfo(e.FrameReceived.Buffer);
            var evt = new VideoFrameEvent(frame, e.FrameReceived.TimestampUs, e.FrameReceived.Rotation);
            
            
            //var buffer = VideoFrameBuffer.Create(handle, bufferInfo);

            lock (_lock)
            {
                VideoBuffer.Dispose();
                VideoBuffer = evt;
                _dirty = true;
            }

            FrameReceived?.Invoke(evt);
        }
    }
}