using System;
using System.Runtime.CompilerServices;
using LiveKit.Proto;

namespace LiveKit.Internal.FFIClients
{
    public static class FfiRequestExtensions
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Inject<T>(this FfiRequest ffiRequest, T request)
        {
            switch (request)
            {
                case DisposeRequest disposeRequest:
                    ffiRequest.Dispose = disposeRequest;
                    break;
                // Room
                case ConnectRequest connectRequest:
                    ffiRequest.Connect = connectRequest;
                    break;
                case DisconnectRequest disconnectRequest:
                    ffiRequest.Disconnect = disconnectRequest;
                    break;
                case PublishTrackRequest publishTrackRequest:
                    ffiRequest.PublishTrack = publishTrackRequest;
                    break;
                case UnpublishTrackRequest unpublishTrackRequest:
                    ffiRequest.UnpublishTrack = unpublishTrackRequest;
                    break;
                case PublishDataRequest publishDataRequest:
                    ffiRequest.PublishData = publishDataRequest;
                    break;
                case SetSubscribedRequest setSubscribedRequest:
                    ffiRequest.SetSubscribed = setSubscribedRequest;
                    break;
                case UpdateLocalMetadataRequest updateLocalMetadataRequest:
                    ffiRequest.UpdateLocalMetadata = updateLocalMetadataRequest;
                    break;
                case UpdateLocalNameRequest updateLocalNameRequest:
                    ffiRequest.UpdateLocalName = updateLocalNameRequest;
                    break;
                case GetSessionStatsRequest getSessionStatsRequest:
                    ffiRequest.GetSessionStats = getSessionStatsRequest;
                    break;
                // Track
                case CreateVideoTrackRequest createVideoTrackRequest:
                    ffiRequest.CreateVideoTrack = createVideoTrackRequest;
                    break;
                case CreateAudioTrackRequest createAudioTrackRequest:
                    ffiRequest.CreateAudioTrack = createAudioTrackRequest;
                    break;
                case GetStatsRequest getStatsRequest:
                    ffiRequest.GetStats = getStatsRequest;
                    break;
                // Video
                case AllocVideoBufferRequest allocVideoBufferRequest:
                    ffiRequest.AllocVideoBuffer = allocVideoBufferRequest;
                    break;
                case NewVideoStreamRequest newVideoStreamRequest:
                    ffiRequest.NewVideoStream = newVideoStreamRequest;
                    break;
                case NewVideoSourceRequest newVideoSourceRequest:
                    ffiRequest.NewVideoSource = newVideoSourceRequest;
                    break;
                case CaptureVideoFrameRequest captureVideoFrameRequest:
                    ffiRequest.CaptureVideoFrame = captureVideoFrameRequest;
                    break;
                case ToI420Request toI420Request:
                    ffiRequest.ToI420 = toI420Request;
                    break;
                case ToArgbRequest toArgbRequest:
                    ffiRequest.ToArgb = toArgbRequest;
                    break;
                // Audio
                case AllocAudioBufferRequest allocAudioBufferRequest:
                    ffiRequest.AllocAudioBuffer = allocAudioBufferRequest;
                    break;
                case NewAudioStreamRequest wewAudioStreamRequest:
                    ffiRequest.NewAudioStream = wewAudioStreamRequest;
                    break;
                case NewAudioSourceRequest newAudioSourceRequest:
                    ffiRequest.NewAudioSource = newAudioSourceRequest;
                    break;
                case CaptureAudioFrameRequest captureAudioFrameRequest:
                    ffiRequest.CaptureAudioFrame = captureAudioFrameRequest;
                    break;
                case NewAudioResamplerRequest newAudioResamplerRequest:
                    ffiRequest.NewAudioResampler = newAudioResamplerRequest;
                    break;
                case RemixAndResampleRequest remixAndResampleRequest:
                    ffiRequest.RemixAndResample = remixAndResampleRequest;
                    break;
                case E2eeRequest e2EeRequest:
                    ffiRequest.E2Ee = e2EeRequest;
                    break;
                default:
                    throw new Exception($"Unknown request type: {request?.GetType().FullName ?? "null"}");
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Clear<T>(this FfiRequest ffiRequest, T request)
        {
            switch (request)
            {
                case DisposeRequest:
                    ffiRequest.Dispose = null;
                    break;
                // Room
                case ConnectRequest:
                    ffiRequest.Connect = null;
                    break;
                case DisconnectRequest:
                    ffiRequest.Disconnect = null;
                    break;
                case PublishTrackRequest:
                    ffiRequest.PublishTrack = null;
                    break;
                case UnpublishTrackRequest:
                    ffiRequest.UnpublishTrack = null;
                    break;
                case PublishDataRequest:
                    ffiRequest.PublishData = null;
                    break;
                case SetSubscribedRequest:
                    ffiRequest.SetSubscribed = null;
                    break;
                case UpdateLocalMetadataRequest:
                    ffiRequest.UpdateLocalMetadata = null;
                    break;
                case UpdateLocalNameRequest:
                    ffiRequest.UpdateLocalName = null;
                    break;
                case GetSessionStatsRequest:
                    ffiRequest.GetSessionStats = null;
                    break;
                // Track
                case CreateVideoTrackRequest:
                    ffiRequest.CreateVideoTrack = null;
                    break;
                case CreateAudioTrackRequest:
                    ffiRequest.CreateAudioTrack = null;
                    break;
                case GetStatsRequest:
                    ffiRequest.GetStats = null;
                    break;
                // Video
                case AllocVideoBufferRequest:
                    ffiRequest.AllocVideoBuffer = null;
                    break;
                case NewVideoStreamRequest:
                    ffiRequest.NewVideoStream = null;
                    break;
                case NewVideoSourceRequest:
                    ffiRequest.NewVideoSource = null;
                    break;
                case CaptureVideoFrameRequest:
                    ffiRequest.CaptureVideoFrame = null;
                    break;
                case ToI420Request:
                    ffiRequest.ToI420 = null;
                    break;
                case ToArgbRequest:
                    ffiRequest.ToArgb = null;
                    break;
                // Audio
                case AllocAudioBufferRequest:
                    ffiRequest.AllocAudioBuffer = null;
                    break;
                case NewAudioStreamRequest:
                    ffiRequest.NewAudioStream = null;
                    break;
                case NewAudioSourceRequest:
                    ffiRequest.NewAudioSource = null;
                    break;
                case CaptureAudioFrameRequest:
                    ffiRequest.CaptureAudioFrame = null;
                    break;
                case NewAudioResamplerRequest:
                    ffiRequest.NewAudioResampler = null;
                    break;
                case RemixAndResampleRequest:
                    ffiRequest.RemixAndResample = null;
                    break;
                case E2eeRequest:
                    ffiRequest.E2Ee = null;
                    break;
                default:
                    throw new Exception($"Unknown request type: {request?.GetType().FullName ?? "null"}");
            }
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void EnsureClean(this FfiRequest request)
        {
            // list of messages is taken from: livekit-ffi/protocol/ffi.proto
            // https://github.com/livekit/rust-sdks/blob/cf34856e78892a639c4d3c1d6a27e9aba0a4a8ff/livekit-ffi/protocol/ffi.proto#L4

            if (
                request.Dispose != null
                ||

                // Room
                request.Connect != null
                || request.Disconnect != null
                || request.PublishTrack != null
                || request.UnpublishTrack != null
                || request.PublishData != null
                || request.SetSubscribed != null
                || request.UpdateLocalMetadata != null
                || request.UpdateLocalName != null
                || request.GetSessionStats != null
                ||

                // Track
                request.CreateVideoTrack != null
                || request.CreateAudioTrack != null
                || request.GetStats != null
                ||

                // Video
                request.AllocVideoBuffer != null
                || request.NewVideoStream != null
                || request.NewVideoSource != null
                || request.CaptureVideoFrame != null
                || request.ToI420 != null
                || request.ToArgb != null
                ||

                // Audio
                request.AllocAudioBuffer != null
                || request.NewAudioStream != null
                || request.NewAudioSource != null
                || request.CaptureAudioFrame != null
                || request.NewAudioResampler != null
                || request.RemixAndResample != null
                || request.E2Ee != null
            )
            {
                throw new InvalidOperationException("Request is not cleared");
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void EnsureClean(this FfiResponse response)
        {
            // list of messages is taken from: livekit-ffi/protocol/ffi.proto
            // https://github.com/livekit/rust-sdks/blob/cf34856e78892a639c4d3c1d6a27e9aba0a4a8ff/livekit-ffi/protocol/ffi.proto#L4

            if (
                response.Dispose != null
                ||

                // Room
                response.Connect != null
                || response.Disconnect != null
                || response.PublishTrack != null
                || response.UnpublishTrack != null
                || response.PublishData != null
                || response.SetSubscribed != null
                || response.UpdateLocalMetadata != null
                || response.UpdateLocalName != null
                || response.GetSessionStats != null
                ||

                // Track
                response.CreateVideoTrack != null
                || response.CreateAudioTrack != null
                || response.GetStats != null
                ||

                // Video
                response.AllocVideoBuffer != null
                || response.NewVideoStream != null
                || response.NewVideoSource != null
                || response.CaptureVideoFrame != null
                || response.ToI420 != null
                || response.ToArgb != null
                ||

                // Audio
                response.AllocAudioBuffer != null
                || response.NewAudioStream != null
                || response.NewAudioSource != null
                || response.CaptureAudioFrame != null
                || response.NewAudioResampler != null
                || response.RemixAndResample != null
                || response.E2Ee != null
            )
            {
                throw new InvalidOperationException("Response is not cleared");
            }
        }
    }
}