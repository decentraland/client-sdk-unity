using System;
using System.Collections.Generic;
using LiveKit.Proto;
using LiveKit.Rooms.Participants;
using LiveKit.Rooms.Tracks;

namespace LiveKit.Rooms.Streaming
{
    public abstract class Streams<T, TInfo> : IStreams<T, TInfo> where T : class, IDisposable
    {
        private readonly TrackKind requiredKind;
        private readonly IParticipantsHub participantsHub;
        private readonly Dictionary<StreamKey, T> streams = new();
        private readonly Dictionary<T, StreamKey> reverseLookup = new();
        private bool isDisposing = false;

        public Streams(IParticipantsHub participantsHub, TrackKind requiredKind)
        {
            this.participantsHub = participantsHub;
            this.requiredKind = requiredKind;
        }

        public WeakReference<T>? ActiveStream(string identity, string sid)
        {
            lock (this)
            {
                var key = new StreamKey(identity, sid);
                if (streams.TryGetValue(key, out var stream) == false)
                {
                    var participant = participantsHub.RemoteParticipant(identity);

                    if (participant == null)
                        if (identity == participantsHub.LocalParticipant().Identity)
                            participant = participantsHub.LocalParticipant();
                        else
                            return null;

                    if (participant.Tracks.TryGetValue(sid, out var trackPublication) == false)
                        return null;

                    if (trackPublication!.Track == null || trackPublication.Track!.Kind != requiredKind)
                        return null;

                    var track = trackPublication.Track!;

                    streams[key] = stream = NewStreamInstance(track);
                    reverseLookup[stream] = key;
                }

                return new WeakReference<T>(stream);
            }
        }

        public bool Release(T stream)
        {
            lock (this)
            {
                if (isDisposing) return false;

                if (reverseLookup.TryGetValue(stream, out var key))
                {
                    streams.Remove(key);
                    reverseLookup.Remove(stream);
                    return true;
                }

                return false;
            }
        }

        public void Free()
        {
            lock (this)
            {
                isDisposing = true;
                foreach (var stream in streams.Values) stream.Dispose();
                streams.Clear();
                reverseLookup.Clear();
                isDisposing = false;
            }
        }

        public void ListInfo(List<StreamInfo<TInfo>> output)
        {
            lock (this)
            {
                output.Clear();

                foreach (var (key, value) in streams)
                {
                    output.Add(new StreamInfo<TInfo>(key, InfoFromStream(value)));
                }
            }
        }

        protected abstract TInfo InfoFromStream(T stream);

        protected abstract T NewStreamInstance(ITrack track);
    }

}