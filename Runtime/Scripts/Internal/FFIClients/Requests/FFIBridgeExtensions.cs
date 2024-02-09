using LiveKit.Proto;
using UnityEngine.UIElements;

namespace LiveKit.Internal.FFIClients.Requests
{
    public static class FFIBridgeExtensions
    {
        public static FfiResponseWrap SendConnectRequest(this IFFIBridge ffiBridge, string url, string authToken)
        {
            Utils.Debug("Connect....");
            using var request = ffiBridge.NewRequest<ConnectRequest>();
            using var roomOptions = request.TempResource<RoomOptions>();
            var connect = request.request;
            connect.Url = url;
            connect.Token = authToken;
            connect.Options = roomOptions;
            connect.Options.AutoSubscribe = false;
            var response = request.Send();
            Utils.Debug($"Connect response.... {response}");
            return response;
        }
    }
}