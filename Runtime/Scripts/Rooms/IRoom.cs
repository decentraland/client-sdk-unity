using System.Threading;
using System.Threading.Tasks;
using LiveKit.Rooms.ActiveSpeakers;
using LiveKit.Rooms.DataPipes;
using LiveKit.Rooms.Participants;

namespace LiveKit.Rooms
{
    public interface IRoom
    {
        event Room.MetaDelegate? RoomMetadataChanged;
        event Room.ParticipantDelegate? ParticipantConnected;
        event Room.ParticipantDelegate? ParticipantMetadataChanged;
        event Room.ParticipantDelegate? ParticipantDisconnected;
        event Room.LocalPublishDelegate? LocalTrackPublished;
        event Room.LocalPublishDelegate? LocalTrackUnpublished;
        event Room.PublishDelegate? TrackPublished;
        event Room.PublishDelegate? TrackUnpublished;
        event Room.SubscribeDelegate? TrackSubscribed;
        event Room.SubscribeDelegate? TrackUnsubscribed;
        event Room.MuteDelegate? TrackMuted;
        event Room.MuteDelegate? TrackUnmuted;
        event Room.ConnectionQualityChangeDelegate? ConnectionQualityChanged;
        event Room.ConnectionStateChangeDelegate? ConnectionStateChanged;
        event Room.ConnectionDelegate? ConnectionUpdated;
        
        IActiveSpeakers ActiveSpeakers { get; }
        
        IParticipantsHub Participants { get; }
        
        IDataPipe DataPipe { get; }
        
        Task<bool> Connect(string url, string authToken, CancellationToken cancelToken);

        void Disconnect();
    }
}