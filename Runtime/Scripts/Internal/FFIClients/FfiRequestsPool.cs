using System;
using System.Runtime.CompilerServices;
using LiveKit.Proto;
using UnityEngine.Pool;

namespace LiveKit.Internal.FFIClients//
{
    public static class FfiRequestsPool
    {
        public static IObjectPool<FfiRequest> NewPool()
        {
            return new ObjectPool<FfiRequest>(
                () => new FfiRequest(),
                actionOnRelease: request => request.EnsureClear()
            );
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void EnsureClear(this FfiRequest request)
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
    }
}