using LiveKit.Proto;

namespace LiveKit.Rooms.Info
{
    public class MemoryRoomInfo : IRoomInfo
    {
        public string Sid { get; private set; }
        public string Name { get; private set; }
        public string Metadata { get; set; }
        
        public void UpdateFromInfo(RoomInfo info)
        {
            Sid = info.Sid!;
            Name = info.Name!;
            Metadata = info.Metadata!;
        }
    }
}