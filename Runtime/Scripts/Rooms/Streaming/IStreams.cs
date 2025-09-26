using System;
using System.Collections.Generic;

namespace LiveKit.Rooms.Streaming
{
    public interface IStreams<TStream, TInfo> where TStream : class
    {
        /// <returns>Caller doesn't care about disposing the Stream, returns null if stream is not found</returns>
        WeakReference<TStream>? ActiveStream(string identity, string sid);

        bool Release(TStream stream);

        void Free();

        void ListInfo(List<StreamInfo<TInfo>> output);
    }
}