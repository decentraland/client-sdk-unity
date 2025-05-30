using System;
using LiveKit.Proto;

namespace LiveKit.Rooms.RoomStats
{
    public interface IRoomStats
    {
        event Action<GetStatsCallback> StatsReceived;

        void RequestStats();
    }
}