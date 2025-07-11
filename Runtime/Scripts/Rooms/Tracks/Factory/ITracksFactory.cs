using LiveKit.Internal;
using LiveKit.Proto;
using LiveKit.Rooms.Participants;

namespace LiveKit.Rooms.Tracks.Factory
{
    public interface ITracksFactory
    {
        ITrack NewAudioTrack(string name, IRtcAudioSource source, IRoom room);

        ITrack NewVideoTrack(string name, RtcVideoSource source, Room room);

        ITrack NewTrack(FfiHandle? handle, TrackInfo info, Room room, Participant participant);
    }
}