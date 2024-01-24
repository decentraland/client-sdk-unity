using System;
using LiveKit.Proto;

namespace LiveKit.Internal.FFIClients
{
    public interface IFFIClient : IDisposable
    {
        [Obsolete("Use poolable version")]
        FfiResponse SendRequest(FfiRequest request);
    }
}