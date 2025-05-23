using LiveKit.Internal;
using LiveKit.Proto;
using LiveKit.Rooms.Tracks.Factory;

namespace LiveKit.Rooms.Tracks
{
    public class AudioTracks : IAudioTracks
    {
        private readonly ITracksFactory tracksFactory;

        public AudioTracks(ITracksFactory tracksFactory)
        {
            this.tracksFactory = tracksFactory;
        }

        public ITrack CreateAudioTrack(string name, RtcAudioSource source, IRoom room) =>
            tracksFactory.NewAudioTrack(name, source, room);
    }
} 