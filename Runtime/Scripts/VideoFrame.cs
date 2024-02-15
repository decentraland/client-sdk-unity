using System;
using LiveKit.Internal;
using LiveKit.Proto;
using System.Runtime.CompilerServices;
using LiveKit.Internal.FFIClients.Requests;

namespace LiveKit
{
    public class VideoFrame : IDisposable
    {
        private VideoBufferInfo _info;
        internal VideoBufferInfo Info => _info;

        private FfiHandle _handle;
        internal FfiHandle Handle => _handle;
        private uint _width;
        internal uint Width => _width;
        private uint _height;
        internal uint Height => _height;
        private VideoBufferType _type;
        internal VideoBufferType Type => _type;
      
        private bool _disposed = false;

        public bool IsValid => !Handle.IsClosed && !Handle.IsInvalid;

        // Explicitly ask for FFIHandle 
        protected VideoFrame(FfiHandle handle, VideoBufferInfo info)
        {
            _handle = handle;
            _width = info.Width;
            _height = info.Height;
            _type = info.Type;
            var memSize = GetMemorySize();
            if (memSize > 0)
                GC.AddMemoryPressure(memSize);
        }

        ~VideoFrame()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                Handle.Dispose();

                var memSize = GetMemorySize();
                if (memSize > 0)
                    GC.RemoveMemoryPressure(memSize);

                _disposed = true;
            }
        }

        /// Used for GC.AddMemoryPressure(Int64)
        /// TODO(theomonnom): Remove the default implementation when each buffer type is implemented  cc MindTrust_VID
        internal virtual long GetMemorySize()
        {
            return -1;
        }

        /// VideoFrameBuffer takes ownership of the FFIHandle
        internal static VideoFrame FromOwnedInfo(OwnedVideoBuffer ownedInfo)
        {
            var info = ownedInfo.Info;
            //ownedInfo.Handle
            VideoFrame frame = new VideoFrame(new FfiHandle((IntPtr)info.DataPtr), info);
            return frame;
        }

        public VideoFrame Convert(VideoBufferType type, bool flipY = false)
        {
            using var request = FFIBridge.Instance.NewRequest<VideoConvertRequest>();
            var alloc = request.request;
            alloc.FlipY = flipY;
            alloc.DstType = type;
            alloc.Buffer = Info;
            using var response = request.Send();
            FfiResponse res = response;
            if(res.VideoConvert.HasError)
            {
                throw new Exception(res.VideoConvert.Error);
            }
            return FromOwnedInfo(res.VideoConvert.Buffer);
        }
 
    }
     
}