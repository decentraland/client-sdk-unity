using System;
using LiveKit.Proto;
using UnityEngine;

namespace LiveKit.RtcSources.Video
{
    public static class VideoUtils
    {
        public static TextureFormat TextureFormatFromVideoBufferType(VideoBufferType type) =>
            // ReSharper disable once SwitchExpressionHandlesSomeKnownEnumValuesWithExceptionInDefault
            type switch
            {
                VideoBufferType.Rgba => TextureFormat.RGBA32,
                VideoBufferType.Argb => TextureFormat.ARGB32,
                VideoBufferType.Bgra => TextureFormat.BGRA32,
                VideoBufferType.Rgb24 => TextureFormat.RGB24,
                _ => throw new NotImplementedException("TODO: Add TextureFormat support for type: " + type)
            };

        public static int StrideFromVideoBufferType(VideoBufferType type) =>
            type switch
            {
                VideoBufferType.Rgba or VideoBufferType.Argb or VideoBufferType.Bgra => 4,
                VideoBufferType.Rgb24 => 3,
                _ => throw new NotImplementedException("TODO: Add stride support for type: " + type)
            };
    }
}