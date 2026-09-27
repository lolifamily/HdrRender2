// HDROutput2 - partial eye adaptation: the engine's eye adaptation shader (PostProcess/EyeAdaptation/EyeAdaptation.hlsl),
// included as is, with the exposure it adapts toward bent. Above the engine's constant exposure (its exposure with
// adaptation off) the eye covers DEBUGDEV_ADAPTATION of the way to a brighter scene, so bright scenes stay brighter: the
// adaptation setting, passed as a define the engine does not precompile (PartialEyeAdaptation). At and below the anchor
// nothing changes: the engine's key value already adapts dark scenes only in part. The smoothing is linear in log2
// exposure, so it keeps its speeds over a smaller swing.
//
// The desired exposure written to .x is bent too. The auto headlamp reads it back against 0.15 and 1.3, both above the
// anchor, and a bent exposure stays below the anchor: it lands on the same side of both every frame.
//
// Built by the engine from the plugin's shader project (PartialEyeAdaptation). Not named EyeAdaptation.hlsl: the include
// below would find this file first.

#include <Common/Frame.hlsli>
#include <PostProcess/ToneMapping/Exposure.hlsli>

float PartialExposure(float luminance, float bias)
{
    float full   = CalculateExposure(luminance, bias);
    float anchor = CalculateExposure(Post_.ConstantLuminance, bias);
    return full + (1.0 - DEBUGDEV_ADAPTATION) * max(anchor - full, 0.0);
}

// Both headers are guarded (FRAME_H__, EXPOSURE_H__): the engine's file includes them again as no-ops, so the macro
// reaches only its body.
#define CalculateExposure PartialExposure
#include <PostProcess/EyeAdaptation/EyeAdaptation.hlsl>
