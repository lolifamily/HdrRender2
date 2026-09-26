// HDROutput2 - frame composite, once per frame end: scene (FP16 HDR) + UI layer (FinalLDR) -> FP16.
// UI is composited over the scene at its own brightness (ui_brightness nits), so SDR-authored UI
// reads correctly on an HDR display and can be set independently of the scene.
//
// The UI layer is the engine's own FinalLDR: in-game it is cleared to transparent right before DrawUI (UiLayerPatch),
// in menus the engine clears it itself. The UI, the video player, the top-most debug shapes and the debug histogram
// all land in it, premultiplied. It is read here through its UNORM UAV, so the sRGB bytes are decoded by hand.
//
// Bindings (all space0): b0 = CompositeConstants, t0 = scene,
//   u0 = FP16 HDR composite (Composite -> present/EXR),
//   u1 = FinalLDR: the UI layer in; with write_back the SDR of the composite out, in place, so the engine's native
//        with-UI screenshot / thumbnail path keeps working after we bypassed its tonemap.
//
// The scene is at the tonemap's resolution, below the output with resolution scaling but no FSR (the engine then
// upscales its SDR image bilinearly, PostProcessStage/Upsampling/Bilinear); do the same with the HDR scene.

cbuffer CompositeConstants : register(b0)
{
    float paper_white;     // scRGB, scene paper white: the SDR preview normalizes to it
    float ui_brightness;   // scRGB, UI white
    float peak;            // scRGB, display peak: where the SDR preview's shoulder ends
    uint  has_scene;       // 0 = menu: no 3D scene, t0 is a placeholder and not sampled
    uint  has_ui;          // 0 = a held frame whose UI went to the engine's placeholder: the scene alone
    uint  write_back;      // 1 = replace FinalLDR with the SDR of the composite
};

Texture2D scene_tex : register(t0);             // FP16 HDR scene (HdrScene)
RWTexture2D<float4> output : register(u0);      // FP16 HDR (Composite)
RWTexture2D<unorm float4> ldr : register(u1);   // FinalLDR through its UNORM view: sRGB-encoded bytes, like the engine's own

// Engine static sampler auto-appended to every root signature (AllSamplers.hlsli: LinearSampler @ s2, clamp).
SamplerState LinearSampler : register(s2);

float max3(float3 c)
{
    return max(max(c.r, c.g), c.b);
}

float linear_to_srgb(float c)
{
    return c <= 0.0031308 ? c * 12.92 : 1.055 * pow(max(c, 0.0), 1.0 / 2.4) - 0.055;
}

float3 rgb_to_srgb(float3 c)
{
    return float3(linear_to_srgb(c.r), linear_to_srgb(c.g), linear_to_srgb(c.b));
}

float srgb_to_linear(float c)
{
    return c <= 0.04045 ? c / 12.92 : pow((c + 0.055) / 1.055, 2.4);
}

float3 srgb_to_rgb(float3 c)
{
    return float3(srgb_to_linear(c.r), srgb_to_linear(c.g), srgb_to_linear(c.b));
}

// SDR preview, same rule as HdrTonemap.hlsl: normalized to the scene paper white, identity below half of it, an
// extended Reinhard shoulder on max(R,G,B) above, reaching 1.0 at the display peak. A UI set dimmer or brighter
// than the scene keeps that relation.
float3 sdr_preview(float3 n)
{
    const float knee = 0.5;
    float white = max((peak / paper_white - knee) / (1.0 - knee), 1e-3);   // shoulder input at the display peak
    float m = max3(n);
    if (m > knee)
    {
        float e = (m - knee) / (1.0 - knee);
        n *= (knee + (1.0 - knee) * e * (1.0 + e / (white * white)) / (1.0 + e)) / m;
    }
    return rgb_to_srgb(min(n, 1.0));
}

float3 load_scene(uint2 texel)
{
    float2 scene_size, out_size;
    scene_tex.GetDimensions(scene_size.x, scene_size.y);
    output.GetDimensions(out_size.x, out_size.y);
    if (all(scene_size == out_size))
        return scene_tex[texel].xyz;
    return scene_tex.SampleLevel(LinearSampler, (texel + 0.5) / out_size, 0).xyz;
}

[numthreads(8, 8, 1)]
void cs_main(uint3 dtid : SV_DispatchThreadID)
{
    uint2 texel = dtid.xy;

    float3 scene = has_scene != 0 ? load_scene(texel) : (float3)0.0;
    float4 ui = (float4)0.0;
    if (has_ui != 0)
    {
        float4 encoded = ldr[texel];
        ui = float4(srgb_to_rgb(encoded.rgb), encoded.a);   // premultiplied alpha, linear
    }

    // Premultiplied "over", UI scaled to its own brightness.
    float3 result = scene * (1.0 - ui.a) + ui.rgb * ui_brightness;

    output[texel] = float4(result, 1.0);                                   // FP16 HDR -> present/EXR
    if (write_back != 0)
        ldr[texel] = float4(sdr_preview(result / paper_white), 1.0);       // SDR -> engine FinalLDR (with UI)
}
