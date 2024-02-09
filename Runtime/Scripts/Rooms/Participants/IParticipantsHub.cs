using System.Collections.Generic;

namespace LiveKit.Rooms.Participants
{
    public interface IParticipantsHub
    {
        Participant LocalParticipant();

        Participant? RemoteParticipant(string sid);
        
        IReadOnlyCollection<string> RemoteParticipantSids();
    }

    public interface IMutableParticipantsHub : IParticipantsHub
    {
        void AssignLocal(Participant participant);

        void AddRemote(Participant participant);
        
        void RemoveRemote(Participant participant);
    }
}