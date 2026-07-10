using Vortice.DXGI;

namespace ClientPlugin.Rendering;

// Format and color-space constants for HDR output.
// Uses scRGB (FP16 linear) as the container: high precision, avoids hand-rolling PQ in the app, and lets the DWM do the final HDR10/SDR conversion.
internal static class HdrResources
{
    // FP16 format for the backbuffer / scene buffer
    public const Format BackbufferFormat = Format.R16G16B16A16_Float;

    // Separate UI layer format: kept as 8-bit sRGB matching the engine's FinalLDR, so the UI's PSO dictionary lookup doesn't crash.
    public const Format UiLayerFormat = Format.R8G8B8A8_UNorm_SRgb;

    // The UI layer's UAV format must drop sRGB -- a D3D12 hard rule: sRGB formats can't have a UAV.
    // CreateRWResizableRenderTargetTexture forcibly adds AllowUnorderedAccess to the texture;
    // if not specified explicitly, uavFormat falls back to the sRGB srvFormat -> creates an sRGB UAV -> device removed.
    // The resource is actually typeless (srvFormat.ToResourceFormat()), so it can carry sRGB RTV/SRV + UNORM UAV at once.
    // We never write UiLayer's UAV (only RTV for the engine to draw, SRV for the composite to read); this UAV only satisfies the creation constraint.
    public const Format UiLayerUavFormat = Format.R8G8B8A8_UNorm;

    // scRGB: Rec.709 primaries + linear (G1.0), full range
    public const ColorSpaceType ScRgbColorSpace = ColorSpaceType.RgbFullG10NoneP709;

    // HDR10: Rec.2020 primaries + PQ (G2084) -- used only to detect whether the display is in HDR mode
    public const ColorSpaceType Hdr10ColorSpace = ColorSpaceType.RgbFullG2084NoneP2020;
}
