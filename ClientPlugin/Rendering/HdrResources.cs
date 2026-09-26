using Vortice.DXGI;

namespace ClientPlugin.Rendering;

// Format and color-space constants for HDR output.
// Uses scRGB (FP16 linear) as the container: high precision, avoids hand-rolling PQ in the app, and lets the DWM do the final HDR10/SDR conversion.
internal static class HdrResources
{
    // FP16 format for the backbuffer / scene buffer
    public const Format BackbufferFormat = Format.R16G16B16A16_Float;

    // scRGB: Rec.709 primaries + linear (G1.0), full range
    public const ColorSpaceType ScRgbColorSpace = ColorSpaceType.RgbFullG10NoneP709;

    // HDR10: Rec.2020 primaries + PQ (G2084) -- used only to detect whether the display is in HDR mode
    public const ColorSpaceType Hdr10ColorSpace = ColorSpaceType.RgbFullG2084NoneP2020;
}
