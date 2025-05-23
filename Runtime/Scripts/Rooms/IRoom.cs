using System.Threading;
using System.Threading.Tasks;
using LiveKit.Rooms.ActiveSpeakers;
using LiveKit.Rooms.DataPipes;
using LiveKit.Rooms.Info;
using LiveKit.Rooms.Participants;
using LiveKit.Rooms.Streaming.Audio;
using LiveKit.Rooms.Tracks;
using LiveKit.Rooms.Tracks.Hub;
using LiveKit.Rooms.VideoStreaming;

namespace LiveKit.Rooms
{
    public interface IRoom : ITracksHub, IRoomConnectionInfo
    {
        event Room.MetaDelegate? RoomMetadataChanged;
 
        event Room.SidDelegate? RoomSidChanged;
 
        IRoomInfo Info { get; }
        
        IActiveSpeakers ActiveSpeakers { get; }
        
        IParticipantsHub Participants { get; }
        
        IDataPipe DataPipe { get; }
        
        IVideoStreams VideoStreams { get; }

        IAudioStreams AudioStreams { get; }

        IAudioTracks AudioTracks { get; }

        void UpdateLocalMetadata(string metadata);

        void SetLocalName(string name);

        Task<bool> ConnectAsync(string url, string authToken, CancellationToken cancelToken, bool autoSubscribe);

        Task DisconnectAsync(CancellationToken cancellationToken); 
    }
}
