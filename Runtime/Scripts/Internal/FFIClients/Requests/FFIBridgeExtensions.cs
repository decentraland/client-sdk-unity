#if !UNITY_WEBGL || UNITY_EDITOR

using System;
using LiveKit.Proto;
using LiveKit.Rooms;

namespace LiveKit.Internal.FFIClients.Requests
{
    public static class FFIBridgeExtensions
    {
        /// <summary>
        ///     When enabled, loopback LiveKit URLs (ws(s)://localhost, 127.0.0.1, [::1]) are connected with an
        ///     explicit <see cref="IceTransportType.TransportAll" /> instead of leaving <c>rtc_config</c> unset.
        ///     <para>
        ///         With the bundled livekit-ffi builds an unset <c>rtc_config</c> already resolves to
        ///         <c>IceTransportsType::All</c> natively, so this is a pin rather than a behaviour change: it keeps
        ///         local rooms on direct ICE even if a future native default changes. It cannot override a server
        ///         that sends <c>force_relay</c>; that is applied natively after the client options.
        ///     </para>
        ///     <para>
        ///         Every other URL is sent with <c>rtc_config</c> unset (the native default), whatever this flag says,
        ///         so a remote world reached later in the same session never inherits the local override.
        ///     </para>
        /// </summary>
        public static bool UseTransportAllForLoopbackUrls { get; set; }

        public static FfiResponseWrap SendConnectRequest(this LiveKit.Internal.FFIClients.Requests.IFFIBridge ffiBridge, string url, string authToken, bool autoSubscribe)
        {
            Utils.Debug("Connect....");
            using var request = ffiBridge.NewRequest<ConnectRequest>();
            using var roomOptions = request.TempResource<LiveKit.Proto.RoomOptions>();
            var connect = request.request;
            connect.Url = url;
            connect.Token = authToken;
            connect.Options = roomOptions;
            connect.Options.AutoSubscribe = autoSubscribe;

            // RoomOptions comes from a shared pool and is not cleared on release, so RtcConfig must be
            // assigned on every connect. Otherwise a loopback override would leak into a later remote connect.
            // Leaving it null keeps the native livekit-ffi default for remote rooms.
            // The temp resource is scoped to the whole method so it stays alive until after Send().
            using var rtcConfig = request.TempResource<RtcConfig>();
            if (UseTransportAllForLoopbackUrls && IsLoopbackLiveKitUrl(url))
            {
                rtcConfig.value.IceTransportType = IceTransportType.TransportAll;
                roomOptions.value.RtcConfig = rtcConfig.value;
            }
            else
            {
                roomOptions.value.RtcConfig = null;
            }

            var response = request.Send();
            Utils.Debug($"Connect response.... {response}");
            return response;
        }

        private static bool IsLoopbackLiveKitUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
                || (uri.Scheme != "ws" && uri.Scheme != "wss"))
                return false;

            // Uri.Host returns IPv6 literals in brackets, so "[::1]" is the only IPv6 loopback form.
            return string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(uri.Host, "127.0.0.1", StringComparison.Ordinal)
                   || string.Equals(uri.Host, "[::1]", StringComparison.Ordinal);
        }
        
        public static FfiResponseWrap SendDisconnectRequest(this LiveKit.Internal.FFIClients.Requests.IFFIBridge ffiBridge, FfiHandle roomHandle)
        {
            using var request = ffiBridge.NewRequest<DisconnectRequest>();
            var disconnect = request.request;
            disconnect.RoomHandle = (ulong)roomHandle.DangerousGetHandle();
            Utils.Debug($"Disconnect.... {disconnect.RoomHandle}");
            var response = request.Send();
            // ReSharper disable once RedundantAssignment
            Utils.Debug($"Disconnect response.... {response}");
            return response;
        }
    }
}

#endif
