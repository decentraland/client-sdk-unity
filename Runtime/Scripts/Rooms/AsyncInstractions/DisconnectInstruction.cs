using System.Threading;
using System.Collections.Generic;
using LiveKit.Internal;
using LiveKit.Proto;

namespace LiveKit.Rooms.AsyncInstractions
{
    public sealed class DisconnectInstruction : AsyncInstruction
    {
        private ulong asyncId;
        private readonly Room room;
        private bool asyncIdSet;
        private readonly List<ulong> receivedAsyncIds;
        private readonly object _lock = new object();

        internal DisconnectInstruction(Room room, CancellationToken token) : base(token)
        {
            this.room = room;
            this.asyncIdSet = false;
            this.receivedAsyncIds = new List<ulong>();
            FfiClient.Instance.DisconnectReceived += OnDisconnect;
        }

        internal void SetAsyncId(ulong asyncId)
        {
            lock (_lock)
            {
                this.asyncId = asyncId;
                asyncIdSet = true;

                if (receivedAsyncIds.Contains(asyncId))
                    Complete();
            }
        }

        private void OnDisconnect(DisconnectCallback e)
        {
            lock (_lock)
            {
                if (!asyncIdSet)
                {
                    receivedAsyncIds.Add(e.AsyncId);
                    return;
                }

                if (asyncId != e.AsyncId)
                    return;

                Complete();
            }
        }

        private void Complete()
        {
            FfiClient.Instance.DisconnectReceived -= OnDisconnect;
            room.OnDisconnect();
            ErrorMessage = string.Empty;
            IsDone = true;
            receivedAsyncIds.Clear();
        }
    }
}
