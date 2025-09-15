using LiveKit.Types;

namespace LiveKit.Rooms.Streaming
{
    public interface IStreams<TStream> where TStream : class
    {
        /// <returns>Caller doesn't care about disposing the Stream, returns null if stream is not found</returns>
        BorrowResult TryBorrowStream(string identity, string sid, out Weak<TStream> stream);
        
        bool Release(TStream stream); 

        void FreeAll();
    }

    public enum BorrowResult
    {
        Success,
        NotFound,
        AlreadyBorrowed
    }
}