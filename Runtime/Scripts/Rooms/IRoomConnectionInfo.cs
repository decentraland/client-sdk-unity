using LiveKit.Proto;
using LiveKit.Rooms.Participants;

namespace LiveKit.Rooms
{
    public delegate void ConnectionQualityChangeDelegate(ConnectionQuality quality, Participant participant);


    public delegate void ConnectionStateChangeDelegate(ConnectionState connectionState);


    public delegate void ConnectionDelegate(IRoom room, ConnectionUpdate connectionUpdate);


    public readonly struct ConnectionUpdate
    {
        public ConnectionUpdateType Type { get; }
        public DisconnectReason? DisconnectReason { get; }

        public ConnectionUpdate(ConnectionUpdateType type, DisconnectReason? disconnectReason = null)
        {
            Type = type;
            DisconnectReason = disconnectReason;
        }
    }

    public enum ConnectionUpdateType
    {
        Connected,
        Disconnected,
        Reconnecting,
        Reconnected
    }

    public interface IRoomConnectionInfo
    {
        event ConnectionQualityChangeDelegate? ConnectionQualityChanged;

        event ConnectionStateChangeDelegate? ConnectionStateChanged;

        event ConnectionDelegate? ConnectionUpdated;
    }
}