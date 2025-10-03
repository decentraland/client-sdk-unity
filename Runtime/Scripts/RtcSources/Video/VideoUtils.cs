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

        public static RenderTextureFormat RenderTextureFormatFrom(TextureFormat format) =>
            format switch
            {
                TextureFormat.Alpha8 => RenderTextureFormat.R8,
                TextureFormat.R8 => RenderTextureFormat.R8,
                TextureFormat.R16 => RenderTextureFormat.R16,
                TextureFormat.RG16 => RenderTextureFormat.RG16,
                TextureFormat.RG32 => RenderTextureFormat.RG32,
                TextureFormat.RGBA32 or TextureFormat.ARGB32 or TextureFormat.BGRA32 => RenderTextureFormat.ARGB32,
                TextureFormat.ARGB4444 or TextureFormat.RGBA4444 => RenderTextureFormat.ARGB4444,
                TextureFormat.RGB565 => RenderTextureFormat.RGB565,
                TextureFormat.RHalf => RenderTextureFormat.RHalf,
                TextureFormat.RGHalf => RenderTextureFormat.RGHalf,
                TextureFormat.RGBAHalf => RenderTextureFormat.ARGBHalf,
                TextureFormat.RFloat => RenderTextureFormat.RFloat,
                TextureFormat.RGFloat => RenderTextureFormat.RGFloat,
                TextureFormat.RGBAFloat => RenderTextureFormat.ARGBFloat,
                TextureFormat.RGB48 => RenderTextureFormat.ARGB64,
                TextureFormat.RGBA64 => RenderTextureFormat.ARGB64,
                // Compressed / special formats: no direct RT support
                TextureFormat.RGB9e5Float or TextureFormat.RGB24 or TextureFormat.DXT1 or TextureFormat.DXT5
                    or TextureFormat.DXT1Crunched or TextureFormat.DXT5Crunched or TextureFormat.PVRTC_RGB2
                    or TextureFormat.PVRTC_RGBA2 or TextureFormat.PVRTC_RGB4 or TextureFormat.PVRTC_RGBA4
                    or TextureFormat.ETC_RGB4 or TextureFormat.ETC2_RGB or TextureFormat.ETC2_RGBA1
                    or TextureFormat.ETC2_RGBA8 or TextureFormat.ETC_RGB4_3DS or TextureFormat.ETC_RGBA8_3DS
                    or TextureFormat.ETC_RGB4Crunched or TextureFormat.ETC2_RGBA8Crunched or TextureFormat.EAC_R
                    or TextureFormat.EAC_R_SIGNED or TextureFormat.EAC_RG or TextureFormat.EAC_RG_SIGNED
                    or TextureFormat.ASTC_4x4 or TextureFormat.ASTC_5x5 or TextureFormat.ASTC_6x6 or TextureFormat.ASTC_8x8
                    or TextureFormat.ASTC_10x10 or TextureFormat.ASTC_12x12 or TextureFormat.ASTC_HDR_4x4
                    or TextureFormat.ASTC_HDR_5x5 or TextureFormat.ASTC_HDR_6x6 or TextureFormat.ASTC_HDR_8x8
                    or TextureFormat.ASTC_HDR_10x10 or TextureFormat.ASTC_HDR_12x12 or TextureFormat.ASTC_RGBA_4x4
                    or TextureFormat.ASTC_RGBA_5x5 or TextureFormat.ASTC_RGBA_6x6 or TextureFormat.ASTC_RGBA_8x8
                    or TextureFormat.ASTC_RGBA_10x10 or TextureFormat.ASTC_RGBA_12x12 or TextureFormat.BC4
                    or TextureFormat.BC5 or TextureFormat.BC6H or TextureFormat.BC7
                    or TextureFormat.YUY2 => throw new NotSupportedException($"Format not supported: {format.ToString()}"),
                _ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
            };
        
        public static int BytesPerPixel(RenderTextureFormat format) =>
            format switch
            {
                RenderTextureFormat.ARGB32 or RenderTextureFormat.BGRA32 => // R8G8B8A8
                    4,
                RenderTextureFormat.RGB565 or RenderTextureFormat.ARGB4444 or RenderTextureFormat.R16 => // 16-bit
                    // 16-bit
                    2,
                RenderTextureFormat.R8 => 1,
                RenderTextureFormat.RHalf => // 16-bit float
                    2,
                RenderTextureFormat.RGHalf => // 2×16-bit float
                    4,
                RenderTextureFormat.ARGBHalf => // 4×16-bit float
                    8,
                RenderTextureFormat.RFloat => // 32-bit float
                    4,
                RenderTextureFormat.RGFloat => // 2×32-bit float
                    8,
                RenderTextureFormat.ARGBFloat => // 4×32-bit float
                    16,
                RenderTextureFormat.RGB111110Float => // packed 3×10-bit floats
                    4,
                RenderTextureFormat.ARGB64 => // 16 bits ×4
                    8,
                RenderTextureFormat.RG16 => // 16 bits ×2
                    4,
                RenderTextureFormat.RG32 => // 32 bits ×2
                    8,
                _ => throw new Exception($"BytesPerPixel not defined for {format}, falling back to 4 (ARGB32).")
            };
    }
}