using LiveKit.Proto;

namespace LiveKit.Rooms.TrackPublications
{
    public interface ITrackPublicationFactory
    {
        TrackPublication NewTrackPublication(TrackPublicationInfo info);
        
        void Release(TrackPublication publication);
        
        static readonly ITrackPublicationFactory Default = new TrackPublicationFactory();
    }
}