using System;
using System.Collections.Generic;
using LiveKit.Proto;
using LiveKit.Rooms.Participants;
using LiveKit.Rooms.Tracks;
using LiveKit.Types;

namespace LiveKit.Rooms.Streaming
{
    public abstract class Streams<T> : IStreams<T> where T : class, IDisposable
    {
        private readonly TrackKind requiredKind;
        private readonly IParticipantsHub participantsHub;
        private readonly Dictionary<StreamKey, Owned<T>> streams = new();
        private readonly Dictionary<T, StreamKey> reverseLookup = new();

        private readonly HashSet<StreamKey> borrowedSet = new();

        private bool isDisposing = false;

        public Streams(IParticipantsHub participantsHub, TrackKind requiredKind)
        {
            this.participantsHub = participantsHub;
            this.requiredKind = requiredKind;
        }

        public BorrowResult TryBorrowStream(string identity, string sid, out Weak<T> stream)
        {
            lock (this)
            {
                var key = new StreamKey(identity, sid);

                if (borrowedSet.Contains(key))
                {
                    stream = Weak<T>.Null;
                    return BorrowResult.AlreadyBorrowed;
                }

                if (streams.TryGetValue(key, out Owned<T>? targetStream) == false)
                {
                    var participant = participantsHub.RemoteParticipant(identity);

                    if (participant == null)
                        if (identity == participantsHub.LocalParticipant().Identity)
                            participant = participantsHub.LocalParticipant();
                        else
                        {
                            stream = Weak<T>.Null;
                            return BorrowResult.NotFound;
                        }

                    if (participant.Tracks.TryGetValue(sid, out var trackPublication) == false)
                    {
                        stream = Weak<T>.Null;
                        return BorrowResult.NotFound;
                    }

                    if (trackPublication!.Track == null || trackPublication.Track!.Kind != requiredKind)
                    {
                        stream = Weak<T>.Null;
                        return BorrowResult.NotFound;
                    }

                    ITrack track = trackPublication.Track!;

                    T streamInstance = NewStreamInstance(track);
                    streams[key] = targetStream = new Owned<T>(streamInstance);
                    reverseLookup[streamInstance] = key;
                }

                borrowedSet.Add(key);
                stream = targetStream!.Downgrade();
                return BorrowResult.Success;
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
                    borrowedSet.Remove(key);
                    return true;
                }

                return false;
            }
        }

        public void FreeAll()
        {
            lock (this)
            {
                isDisposing = true;
                foreach (Owned<T>? stream in streams.Values)
                {
                    stream.Dispose(out T? resource);
                    resource!.Dispose();
                }

                streams.Clear();
                reverseLookup.Clear();
                isDisposing = false;
            }
        }

        protected abstract T NewStreamInstance(ITrack track);
    }

}