﻿using LiveKit.Internal.FFIClients;
using LiveKit.Internal.FFIClients.Requests;
using LiveKit.Proto;
using LiveKit.Rooms.Participants;
using LiveKit.Rooms.Tracks;
using UnityEngine;

namespace LiveKit.Rooms.Streaming.Audio
{
    public class AudioStreams : Streams<AudioStream>, IAudioStreams
    {
        public AudioStreams(IParticipantsHub participantsHub) : base(participantsHub, TrackKind.KindAudio)
        {
        }

        protected override AudioStream NewStreamInstance(ITrack track)
        {
            using var request = FFIBridge.Instance.NewRequest<NewAudioStreamRequest>();
            var newStream = request.request;
            newStream.TrackHandle = (ulong)track.Handle!.DangerousGetHandle();
            newStream.Type = AudioStreamType.AudioStreamNative;

            // TODO need to adjust at runtime to avoid inconsistencies
            if (Application.platform is RuntimePlatform.OSXPlayer
                || Application.platform is RuntimePlatform.OSXEditor
                || Application.platform is RuntimePlatform.OSXServer)
            {
                newStream.SampleRate = 44100;
            }
            else
            {
                newStream.SampleRate = 48000;
            }

            newStream.NumChannels = 2;

            using FfiResponseWrap response = request.Send();
            FfiResponse res = response;

            OwnedAudioStream streamInfo = res.NewAudioStream!.Stream!;
            return new AudioStream(this, streamInfo);
        }
    }
}