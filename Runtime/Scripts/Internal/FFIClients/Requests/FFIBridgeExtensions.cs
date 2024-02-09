using LiveKit.Proto;

namespace LiveKit.Internal.FFIClients.Requests
{
    public static class FFIBridgeExtensions
    {
        public static FfiResponseWrap SendConnectRequest(this IFFIBridge ffiBridge, string url, string authToken)
        {
            using var request = ffiBridge.NewRequest<ConnectRequest>();
            using var roomOptions = request.TempResource<RoomOptions>();
            var connect = request.request;
            connect.Url = url;
            connect.Token = authToken;
            connect.Options = roomOptions;
            connect.Options.AutoSubscribe = false;
            return request.Send();
        }
    }
}