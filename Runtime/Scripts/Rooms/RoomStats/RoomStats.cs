using LiveKit.Internal;
using LiveKit.Internal.FFIClients.Requests;
using LiveKit.Proto;
using System;

namespace LiveKit.Rooms.RoomStats
{
    public class RoomStats : IRoomStats
    {
        public static readonly RoomStats Instance = new();

        private ulong currentAsyncId;

        public event Action<GetStatsCallback> StatsReceived;

        private RoomStats()
        {
            FfiClient.Instance.GetStatsReceived += OnGetStatsReceived;
        }

        private void OnGetStatsReceived(GetStatsCallback e)
        {
            if (e.AsyncId == currentAsyncId)
                StatsReceived?.Invoke(e);
            e.Stats[0].cas
        }

        public void RequestStats()
        {
            using var requestWrap = FFIBridge.Instance.NewRequest<GetStatsRequest>();
            var request = requestWrap.request;
            request.
            using var responseWrap = requestWrap.Send();
            FfiResponse response = responseWrap;
            currentAsyncId = response.GetStats.AsyncId;
        }
    }
}