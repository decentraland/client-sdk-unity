using System;
using System.Runtime.CompilerServices;
using LiveKit.Proto;
using UnityEngine.Pool;

namespace LiveKit.Internal.FFIClients.Pools//
{
    public static class Pools
    {
        public static IObjectPool<FfiRequest> NewFfiRequestPool()
        {
            return NewClearablePool<FfiRequest>(EnsureClean);
        }
        
        public static IObjectPool<FfiResponse> NewFfiResponsePool()
        {
            return NewClearablePool<FfiResponse>(EnsureClean);
        }
        
        public static IObjectPool<T> NewClearablePool<T>(Action<T> ensureClean) where T : class, new()
        {
            return new ObjectPool<T>(
                () => new T(),
                actionOnRelease: ensureClean
            );
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void EnsureClean(this FfiRequest request)
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
        private static void EnsureClean(this FfiResponse response)
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