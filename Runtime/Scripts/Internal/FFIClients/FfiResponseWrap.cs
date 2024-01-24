using System;
using LiveKit.Internal.FFIClients;
using LiveKit.Proto;

namespace LiveKit.client_sdk_unity.Runtime.Scripts.Internal.FFIClients
{
    public readonly struct FfiResponseWrap : IDisposable
    {
        private readonly FfiResponse response;
        private readonly IFFIClient client;

        public FfiResponseWrap(FfiResponse response, IFFIClient client)
        {
            this.response = response;
            this.client = client;
        }

        public void Dispose()
        {
            client.Release(response);
        }

        public static implicit operator FfiResponse(FfiResponseWrap wrap)
        {
            return wrap.response;
        }

        public override string ToString()
        {
            return response.ToString()!;
        }
    }
}