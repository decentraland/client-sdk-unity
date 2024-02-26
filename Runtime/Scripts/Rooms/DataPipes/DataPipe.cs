using System;
using System.Collections.Generic;
using LiveKit.Internal;
using LiveKit.Internal.FFIClients.Requests;
using LiveKit.Proto;
using LiveKit.Rooms.Participants;

namespace LiveKit.Rooms.DataPipes
{
    public class DataPipe : IMutableDataPipe
    {
        private IParticipantsHub participantsHub = null!;

        public event ReceivedDataDelegate? DataReceived;

        public void PublishData(
            Span<byte> data,
            string topic,
            IReadOnlyCollection<string> sids,
            DataPacketKind kind = DataPacketKind.KindLossy
        )
        {
            unsafe
            {
                fixed (byte* pointer = data)
                {
                    PublishData(pointer, data.Length, topic, sids, kind);
                }
            }
        }

        private unsafe void PublishData(
            byte* data,
            int len,
            string topic,
            IReadOnlyCollection<string> sids,
            DataPacketKind kind = DataPacketKind.KindLossy
        )
        {
            using var request = FFIBridge.Instance.NewRequest<PublishDataRequest>();
            var dataRequest = request.request;
            dataRequest.DestinationSids!.Clear();
            dataRequest.DestinationSids.AddRange(sids);
            dataRequest.DataLen = (ulong)len;
            dataRequest.DataPtr = (ulong)data;
            dataRequest.Kind = kind;
            dataRequest.Topic = topic;
            dataRequest.LocalParticipantHandle = (ulong)participantsHub.LocalParticipant().Handle.DangerousGetHandle();
            Utils.Debug("Sending message: " + topic);
            using var response = request.Send();
        }

        public void Assign(IParticipantsHub participants)
        {
            participantsHub = participants;
        }

        public void Notify(ReadOnlySpan<byte> data, Participant participant, DataPacketKind kind)
        {
            DataReceived?.Invoke(data, participant, kind);
        }
    }
}