using System;
using LiveKit.client_sdk_unity.Runtime.Scripts.Internal.FFIClients;
using LiveKit.Proto;

namespace LiveKit.Internal.FFIClients
{
    public interface IPoolableFFIClient : IFFIClient
    {
        FfiResponseWrap SendRequest(Action<FfiRequest> requestSetUp, Action<FfiRequest> requestCleanUp);

        void Release(FfiResponse response);
    }
}