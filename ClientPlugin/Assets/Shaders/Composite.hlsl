// HDROutput2 - UI composite: scene (FP16 HDR) + UI layer (8-bit sRGB, premultiplied) -> FP16.
// UI is composited over the scene and scaled to paper_white nits so SDR-authored UI reads
// correctly on an HDR display.
//
// Dual output (all space0): b0 = CompositeConstants, t0 = scene, t1 = UI,
//   u0 = FP16 HDR composite (Composite -> present/EXR),
//   u1 = 8-bit SDR of the same composite (engine FinalLDR, WITH UI) so the engine's native
//        screenshot / thumbnail / downsample path keeps working after we bypassed its tonemap.

cbuffer CompositeConstants : register(b0)
{
    float paper_white;   // scRGB, UI white target
    uint  has_scene;     // 1 = sample scene_tex (in-game); 0 = menu (no 3D, scene = 0)
    float2 _pad;
};

Texture2D scene_tex : register(t0);   // FP16 HDR scene (EETF output)
Texture2D ui_tex    : register(t1);   // 8-bit sRGB UI (SRV read auto-decodes sRGB -> linear)
RWTexture2D<float4> output     : register(u0);   // FP16 HDR (Composite)
RWTexture2D<float4> ldr_output : register(u1);   // 8-bit SDR (engine FinalLDR, with UI)

float get_relative_luminance(float3 rgb)
{
    return dot(rgb, float3(0.2126, 0.7152, 0.0722));
}

// BT.2446 Method A SDR tonemap (matches SE1 CPU SaveSdrPng bit-for-bit).
float tonemap_knee(float norm)
{
    float knee  = 0.75;
    float delta = 0.5;
    float range = 0.25; // 1 - knee
    float excess = norm - knee;
    return norm <= knee ? norm : knee + range * excess / (excess + delta);
}

float linear_to_srgb(float c)
{
    return c <= 0.0031308 ? c * 12.92 : 1.055 * pow(max(c, 0.0), 1.0 / 2.4) - 0.055;
}

// hdr: scRGB (1.0 = 80 nits); pw: paper_white in scRGB. Returns sRGB-encoded [0,1].
float3 hdr_to_sdr(float3 hdr, float pw)
{
    float3 n = max(hdr, 0.0) / pw;
    float luma = max(get_relative_luminance(n), 1e-6);
    float overbright = saturate((luma - 1.0) / luma);
    n = lerp(n, luma.xxx, overbright);
    float3 tm = float3(tonemap_knee(n.r), tonemap_knee(n.g), tonemap_knee(n.b));
    return float3(linear_to_srgb(tm.r), linear_to_srgb(tm.g), linear_to_srgb(tm.b));
}

[numthreads(8, 8, 1)]
void cs_main(uint3 dtid : SV_DispatchThreadID)
{
    uint2 texel = dtid.xy;

    // Menu has no 3D scene (scene = 0); t0 is a placeholder then, not sampled ->
    // avoids binding FinalLDR as both SRV (t0) and UAV (u1) on the same resource.
    float3 scene = has_scene != 0 ? scene_tex[texel].xyz : (float3)0.0;
    float4 ui = ui_tex[texel];   // premultiplied alpha, linear

    // Premultiplied "over", UI scaled to paper_white nits.
    float3 result = scene * (1.0 - ui.a) + ui.rgb * paper_white;

    output[texel]     = float4(result, 1.0);                          // FP16 HDR -> present/EXR
    ldr_output[texel] = float4(hdr_to_sdr(result, paper_white), 1.0); // SDR -> engine FinalLDR (with UI)
}
