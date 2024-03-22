using System;
using System.Buffers;
using LiveKit.Internal;
using LiveKit.Proto;
using Google.Protobuf.Collections;
using System.Threading;
using System.Threading.Tasks;
using LiveKit.Internal.FFIClients.Pools.Memory;
using LiveKit.Internal.FFIClients.Requests;
using LiveKit.Rooms.ActiveSpeakers;
using LiveKit.Rooms.AsyncInstractions;
using LiveKit.Rooms.DataPipes;
using LiveKit.Rooms.Info;
using LiveKit.Rooms.Participants;
using LiveKit.Rooms.Participants.Factory;
using LiveKit.Rooms.TrackPublications;
using LiveKit.Rooms.Tracks;
using LiveKit.Rooms.Tracks.Factory;
using LiveKit.Rooms.Tracks.Hub;
using RoomInfo = LiveKit.Proto.RoomInfo;

namespace LiveKit.Rooms
{
    public class Room : IRoom
    {
        public delegate void MetaDelegate(string metaData);
        public delegate void RemoteParticipantDelegate(Participant participant);

        internal FfiHandle Handle { get; private set; } = null!;

        public event MetaDelegate? RoomMetadataChanged;
        public event LocalPublishDelegate? LocalTrackPublished;
        public event LocalPublishDelegate? LocalTrackUnpublished;
        public event PublishDelegate? TrackPublished;
        public event PublishDelegate? TrackUnpublished;
        public event SubscribeDelegate? TrackSubscribed;
        public event SubscribeDelegate? TrackUnsubscribed;
        public event MuteDelegate? TrackMuted;
        public event MuteDelegate? TrackUnmuted;
        public event ConnectionQualityChangeDelegate? ConnectionQualityChanged;
        public event ConnectionStateChangeDelegate? ConnectionStateChanged;
        public event ConnectionDelegate? ConnectionUpdated;

        public IActiveSpeakers ActiveSpeakers => activeSpeakers;

        public IParticipantsHub Participants => participantsHub;

        public IDataPipe DataPipe => dataPipe;

        public IRoomInfo Info => roomInfo;
        
        private readonly IMemoryPool memoryPool;
        private readonly IMutableActiveSpeakers activeSpeakers;
        private readonly IMutableParticipantsHub participantsHub;
        private readonly ITracksFactory tracksFactory;
        private readonly IFfiHandleFactory ffiHandleFactory;
        private readonly IParticipantFactory participantFactory;
        private readonly ITrackPublicationFactory trackPublicationFactory;
        private readonly IMutableDataPipe dataPipe;
        private readonly IMutableRoomInfo roomInfo;

        public Room() : this(
            new ArrayMemoryPool(ArrayPool<byte>.Shared!),
            new DefaultActiveSpeakers(),
            new ParticipantsHub(),
            new TracksFactory(), 
            IFfiHandleFactory.Default, 
            IParticipantFactory.Default, 
            ITrackPublicationFactory.Default, 
            new DataPipe(), 
            new MemoryRoomInfo()
        )
        {
        }

        public Room(IMemoryPool memoryPool, IMutableActiveSpeakers activeSpeakers, IMutableParticipantsHub participantsHub, ITracksFactory tracksFactory, IFfiHandleFactory ffiHandleFactory, IParticipantFactory participantFactory, ITrackPublicationFactory trackPublicationFactory, IMutableDataPipe dataPipe, IMutableRoomInfo roomInfo)
        {
            this.memoryPool = memoryPool;
            this.activeSpeakers = activeSpeakers;
            this.participantsHub = participantsHub;
            this.tracksFactory = tracksFactory;
            this.ffiHandleFactory = ffiHandleFactory;
            this.participantFactory = participantFactory;
            this.trackPublicationFactory = trackPublicationFactory;
            this.dataPipe = dataPipe;
            this.roomInfo = roomInfo;
            dataPipe.Assign(participantsHub);
        }

        public void UpdateLocalMetadata(string metadata)
        {
            using var request = FFIBridge.Instance.NewRequest<UpdateLocalMetadataRequest>();
            request.request.Metadata = metadata;
            using var response = request.Send();
        }

        public Task<bool> ConnectAsync(string url, string authToken, CancellationToken cancelToken, bool autoSubscribe)
        {
            using var response = FFIBridge.Instance.SendConnectRequest(url, authToken, autoSubscribe);
            FfiResponse res = response;
            return new ConnectInstruction(res.Connect!.AsyncId, this, cancelToken)
                .AwaitWithSuccess();
        }

        public async Task DisconnectAsync(CancellationToken cancellationToken)
        {
            using var response = FFIBridge.Instance.SendDisconnectRequest(this);
            FfiResponse res = response;
            var instruction = new DisconnectInstruction(res.Disconnect!.AsyncId, this, cancellationToken);
            await instruction.AwaitWithSuccess();
            ffiHandleFactory.Release(Handle);
        }

        private void OnEventReceived(RoomEvent e)
        {
            if (e.RoomHandle != (ulong)Handle.DangerousGetHandle())
            {
                Utils.Debug("Ignoring. Different Room... " + e);
                return;
            }
            Utils.Debug($"Room {Info.Name} Event Type: {e.MessageCase}   ---> ({e.RoomHandle} <=> {(ulong)Handle.DangerousGetHandle()})");
            switch (e.MessageCase)
            {
                case RoomEvent.MessageOneofCase.RoomMetadataChanged:
                {
                        roomInfo.UpdateMetadata(e.RoomMetadataChanged!.Metadata!);
                        RoomMetadataChanged?.Invoke(roomInfo.Metadata);
                    }
                    break;
                case RoomEvent.MessageOneofCase.ParticipantMetadataChanged:
                    {
                        var participant = this.ParticipantEnsured(e.ParticipantNameChanged!.ParticipantSid!);
                        participant.UpdateMeta(e.ParticipantMetadataChanged!.Metadata!);
                        participantsHub.NotifyParticipantUpdate(participant, UpdateFromParticipant.MetadataChanged);
                    }
                    break;
                case RoomEvent.MessageOneofCase.ParticipantConnected:
                {
                    var participant = participantFactory.NewRemote(
                        this,
                            e.ParticipantConnected!.Info!.Info!,
                            null,
                            ffiHandleFactory.NewFfiHandle(e.ParticipantConnected.Info.Handle!.Id
                        )
                    );
                    participantsHub.AddRemote(participant);
                    participantsHub.NotifyParticipantUpdate(participant, UpdateFromParticipant.Connected);
                }
                    break;
                case RoomEvent.MessageOneofCase.ParticipantDisconnected:
                    {
                        var info = e.ParticipantDisconnected!;
                        var participant = participantsHub.RemoteParticipantEnsured(info.ParticipantSid!);
                        participantsHub.RemoveRemote(participant);
                        participantsHub.NotifyParticipantUpdate(participant, UpdateFromParticipant.Disconnected);
                    }
                    break;
                case RoomEvent.MessageOneofCase.LocalTrackUnpublished:
                    {
                        if (Participants.LocalParticipant().Tracks.ContainsKey(e.LocalTrackUnpublished!.PublicationSid))
                        {
                            var publication = Participants.LocalParticipant().TrackPublication(e.LocalTrackUnpublished.PublicationSid!);
                            LocalTrackUnpublished?.Invoke(publication, Participants.LocalParticipant());
                        }
                        else
                        {
                            Utils.Debug("Unable to find local track after unpublish: " + e.LocalTrackPublished!.TrackSid);
                        }
                    }
                    break;
                case RoomEvent.MessageOneofCase.LocalTrackPublished:
                    {
                        if(Participants.LocalParticipant().Tracks.ContainsKey(e.LocalTrackPublished!.TrackSid))
                        {
                            var publication = Participants.LocalParticipant().TrackPublication(e.LocalTrackPublished.TrackSid!);
                            LocalTrackPublished?.Invoke(publication, Participants.LocalParticipant());
                        } else
                        {
                            Utils.Debug("Unable to find local track after publish: " + e.LocalTrackPublished.TrackSid);
                        }
                    }
                    break;
                case RoomEvent.MessageOneofCase.TrackPublished:
                    {
                        var participant = participantsHub.RemoteParticipantEnsured(e.TrackPublished!.ParticipantSid!);
                        var publication = trackPublicationFactory.NewTrackPublication(e.TrackPublished.Publication!.Handle, e.TrackPublished.Publication!.Info!);
                        participant.Publish(publication);
                        TrackPublished?.Invoke(publication, participant);
                    }
                    break;
                case RoomEvent.MessageOneofCase.TrackUnpublished:
                    {
                        var participant = participantsHub.RemoteParticipantEnsured(e.TrackUnpublished!.ParticipantSid!);
                        participant.UnPublish(e.TrackUnpublished.PublicationSid!, out var unpublishedTrack);
                        TrackUnpublished?.Invoke(unpublishedTrack, participant);
                    }
                    break;
                case RoomEvent.MessageOneofCase.TrackSubscribed:
                    {
                        var info = e.TrackSubscribed!.Track!.Info!;
                        var participant = participantsHub.RemoteParticipantEnsured(e.TrackSubscribed.ParticipantSid!);
                        var publication = participant.TrackPublication(info.Sid!);
                        var trackHandle = ffiHandleFactory.NewFfiHandle((IntPtr)e.TrackSubscribed.Track.Handle.Id);

                        var track = tracksFactory.NewTrack(trackHandle, info, this, participant);
                        publication.UpdateTrack(track);
                        TrackSubscribed?.Invoke(track, publication, participant);
                    }
                    break;
                case RoomEvent.MessageOneofCase.TrackUnsubscribed:
                    {
                        var participant = this.ParticipantEnsured(e.TrackUnsubscribed!.ParticipantSid!);
                        var publication = participant.TrackPublication(e.TrackUnsubscribed.TrackSid!);
                        publication.RemoveTrack(out var removedTrack);
                        TrackUnsubscribed?.Invoke(removedTrack!, publication, participant);
                    }
                    break;
                case RoomEvent.MessageOneofCase.TrackMuted:
                    {
                        var trackMuted = e.TrackMuted!;
                        var participant = this.ParticipantEnsured(trackMuted.ParticipantSid!);
                        var publication = participant.TrackPublication(trackMuted.TrackSid!);
                        publication.UpdateMuted(true);
                        TrackMuted?.Invoke(publication, participant);
                    }
                    break;
                case RoomEvent.MessageOneofCase.TrackUnmuted:
                    {
                        var trackUnmuted = e.TrackUnmuted!;
                        var participant = this.ParticipantEnsured(trackUnmuted.ParticipantSid!);
                        var publication = participant.TrackPublication(trackUnmuted.TrackSid!);
                        publication.UpdateMuted(false);
                        TrackUnmuted?.Invoke(publication, participant);
                    }
                    break;
                case RoomEvent.MessageOneofCase.ActiveSpeakersChanged:
                    {
                        activeSpeakers.UpdateCurrentActives(e.ActiveSpeakersChanged!.ParticipantSids!);
                    }
                    break;
                case RoomEvent.MessageOneofCase.ConnectionQualityChanged:
                    {
                        var participant = this.ParticipantEnsured(e.ConnectionQualityChanged!.ParticipantSid!);
                        var quality = e.ConnectionQualityChanged.Quality;
                        ConnectionQualityChanged?.Invoke(quality, participant);
                    }
                    break;
                case RoomEvent.MessageOneofCase.DataReceived:
                    {
                        var dataInfo = e.DataReceived!.Data!;
                        using var memory = dataInfo.ReadAndDispose(memoryPool);
                        var participant = this.ParticipantEnsured(e.DataReceived.ParticipantSid!);
                        dataPipe.Notify(memory.Span(), participant, e.DataReceived.Kind);
                    }
                    break;
                case RoomEvent.MessageOneofCase.ConnectionStateChanged:
                    roomInfo.UpdateConnectionState(e.ConnectionStateChanged!.State);
                    ConnectionStateChanged?.Invoke(e.ConnectionStateChanged!.State);
                    break;
                /*case RoomEvent.MessageOneofCase.Connected:
                    Connected?.Invoke(this);
                    break;*/
                case RoomEvent.MessageOneofCase.Eos:
                case RoomEvent.MessageOneofCase.Disconnected:
                    ConnectionUpdated?.Invoke(this, ConnectionUpdate.Disconnected);
                    break;
                case RoomEvent.MessageOneofCase.Reconnecting:
                    ConnectionUpdated?.Invoke(this, ConnectionUpdate.Reconnecting);
                    break;
                case RoomEvent.MessageOneofCase.Reconnected:
                    ConnectionUpdated?.Invoke(this, ConnectionUpdate.Reconnected);
                    break;
                case RoomEvent.MessageOneofCase.None:
                    //ignore
                    break;
                case RoomEvent.MessageOneofCase.TrackSubscriptionFailed:
                    //ignore
                    break;
                case RoomEvent.MessageOneofCase.ParticipantNameChanged:
                    //ignore
                    break;
                case RoomEvent.MessageOneofCase.E2EeStateChanged:
                    //ignore
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        internal void OnConnect(FfiOwnedHandle roomHandle, RoomInfo info, OwnedParticipant participant, RepeatedField<ConnectCallback.Types.ParticipantWithTracks> participants)
        {
            Utils.Debug($"OnConnect.... {roomHandle.Id}  {participant.Handle!.Id}");
            Utils.Debug(info);

            activeSpeakers.Clear();
            participantsHub.Clear();

            Handle = ffiHandleFactory.NewFfiHandle(roomHandle.Id);
            roomInfo.UpdateFromInfo(info);

            var selfParticipant = participantFactory.NewParticipant(
                participant.Info!,
                this,
                ffiHandleFactory.NewFfiHandle(participant.Handle.Id),
                Origin.Local
            );
            participantsHub.AssignLocal(selfParticipant);
            // Add already connected participant
            foreach (var p in participants)
            {
                var remote = participantFactory.NewRemote(
                    this,
                    p.Participant!.Info!, p.Publications,
                    ffiHandleFactory.NewFfiHandle(p.Participant.Handle!.Id)
                );
                participantsHub.AddRemote(remote);
            }

            FfiClient.Instance.RoomEventReceived += OnEventReceived;
            FfiClient.Instance.DisconnectReceived += OnDisconnectReceived;
            ConnectionUpdated?.Invoke(this, ConnectionUpdate.Connected);
        }

        private void OnDisconnectReceived(DisconnectCallback e)
        {
            FfiClient.Instance.DisconnectReceived -= OnDisconnectReceived;
            Utils.Debug($"OnDisconnect.... {e}");
        }

        public void OnDisconnect()
        {
            FfiClient.Instance.RoomEventReceived -= OnEventReceived;
        }
    }
}
