using System;
using LiveKit.Proto;

namespace LiveKit.Internal.FFIClients
{
    public interface IPoolableFFIClient : IFFIClient
    {
        FfiResponse SendRequest(Action<FfiRequest> requestSetUp, Action<FfiRequest> requestCleanUp);

        void Release(FfiResponse response);
    }
}