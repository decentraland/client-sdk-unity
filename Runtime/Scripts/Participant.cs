using System;
using System.Linq;
using System.Collections.Generic;
using LiveKit.Internal;
using LiveKit.Proto;
using UnityEngine;
using System.Threading.Tasks;
using System.Threading;

namespace LiveKit
{
    public class Participant
    {
        public delegate void PublishDelegate(RemoteTrackPublication publication);

        private ParticipantInfo _info;
        public string Sid => _info.Sid;
        public string Identity => _info.Identity;
        public string Name => _info.Name;
        public string Metadata => _info.Metadata;
        public void SetMeta(string meta)
        {
            _info.Metadata = meta;
        }

        internal FfiHandle Handle;

        public bool Speaking { private set; get; }
        public float AudioLevel { private set; get; }
        public ConnectionQuality ConnectionQuality { private set; get; }

        public event PublishDelegate TrackPublished;
        public event PublishDelegate TrackUnpublished;

        private Room _room;
        public Room Room
        {
            get
            {
                return _room;
            }
        }

        public IReadOnlyDictionary<string, TrackPublication> Tracks => _tracks;
        internal readonly Dictionary<string, TrackPublication> _tracks = new();

        protected Participant(ParticipantInfo info, Room room, FfiHandle handle)
        {
            _room =  room;
            Handle = handle;
            UpdateInfo(info);
        }
      
        internal void UpdateInfo(ParticipantInfo info)
        {
            _info = info;
        }

        internal void OnTrackPublished(RemoteTrackPublication publication)
        {
            TrackPublished?.Invoke(publication);
        }

        internal void OnTrackUnpublished(RemoteTrackPublication publication)
        {
            TrackUnpublished?.Invoke(publication);
        }

    }

    public sealed class LocalParticipant : Participant
    {
        public new IReadOnlyDictionary<string, LocalTrackPublication> Tracks =>
            base.Tracks.ToDictionary(p => p.Key, p => (LocalTrackPublication)p.Value);

        internal LocalParticipant(ParticipantInfo info, Room room, FfiHandle handle) : base(info, room, handle) { }

        public PublishTrackInstruction PublishTrack(ILocalTrack localTrack, TrackPublishOptions options)
        {
            var track = (Track)localTrack;
            var publish = new PublishTrackRequest();
            publish.LocalParticipantHandle = (ulong)Handle.DangerousGetHandle();
            publish.TrackHandle =   (ulong)track.Handle.DangerousGetHandle();
            publish.Options = options;

            

            var request = new FfiRequest();
            request.PublishTrack = publish;
            var resp = FfiClient.SendRequest(request);
            if (resp!=null)
                return new PublishTrackInstruction(resp.PublishTrack.AsyncId, Room);
            return null;
        }
    }

    public sealed class RemoteParticipant : Participant
    {
        public new IReadOnlyDictionary<string, RemoteTrackPublication> Tracks =>
            base.Tracks.ToDictionary(p => p.Key, p => (RemoteTrackPublication)p.Value);

        internal RemoteParticipant(ParticipantInfo info, Room room, FfiHandle handle) : base(info, room, handle) { }
    }

    public sealed class PublishTrackInstruction 
    {
        public bool IsDone { protected set; get; }
        public bool IsError { protected set; get; }

        private ulong _asyncId;
        private Room _room;


        internal PublishTrackInstruction(ulong asyncId, Room room)
        {
            _asyncId = asyncId;
            _room = room;
            FfiClient.Instance.PublishTrackReceived += OnPublish;
        }

        void OnPublish(PublishTrackCallback e)
        {
            if (e.AsyncId != _asyncId)
                return;

            IsError = !string.IsNullOrEmpty(e.Error);
            IsDone = true;


            Utils.Debug("TODO Completed Publishing Track: " + IsError + " " + e.HasError);


            FfiClient.Instance.PublishTrackReceived -= OnPublish;
        }
    }
}
