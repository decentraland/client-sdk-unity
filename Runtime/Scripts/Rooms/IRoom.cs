using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using LiveKit.Proto;
using LiveKit.Rooms.AsyncInstractions;
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
        event Room.SpeakersChangeDelegate? ActiveSpeakersChanged;
        event Room.ConnectionQualityChangeDelegate? ConnectionQualityChanged;
        event Room.ConnectionStateChangeDelegate? ConnectionStateChanged;
        event Room.ConnectionDelegate? Connected;
        event Room.ConnectionDelegate? Disconnected;
        event Room.ConnectionDelegate? Reconnecting;
        event Room.ConnectionDelegate? Reconnected;
        
        IParticipantsHub Participants { get; }
        
        IDataPipe DataPipe { get; }
        
        Task<bool> Connect(string url, string authToken, CancellationToken cancelToken);

        void Disconnect();
    }
}