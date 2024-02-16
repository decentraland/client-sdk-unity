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
        public FfiHandle Handle => _handle;
        internal uint Width => _info.Width;
        internal uint Height => _info.Height;
        internal uint Stride => _info.Stride;
        private VideoBufferType _type;
        internal VideoBufferType Type => _type;
      
        private bool _disposed = false;

        public bool IsValid => !Handle.IsClosed && !Handle.IsInvalid;

        // Explicitly ask for FFIHandle 
        protected VideoFrame(FfiHandle handle, VideoBufferInfo info)
        {
            _info = info;
            _handle = handle;
            
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
        public virtual long GetMemorySize()
        {
            return  Height * Stride;
        }

        /// VideoFrameBuffer takes ownership of the FFIHandle
        internal static VideoFrame FromOwnedInfo(OwnedVideoBuffer ownedInfo)
        {
            var info = ownedInfo.Info;
            //ownedInfo.Handle
            VideoFrame frame = new VideoFrame(new FfiHandle((IntPtr)info.DataPtr), info);
            return frame;
        }

        public VideoFrame Convert(VideoBufferType type, bool flipY )
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