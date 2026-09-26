// HDROutput2 - scene tonemap takeover: the engine's scene color, one BT.2390 display mapping (PQ space).
// Ported from SE1 HdrTonemap.hlsl.
//
// Bindings must match the plugin's hand-built root signature:
//   b0 = HdrConstants, t0 = scene HDR src, t1 = eye-adaptation exposure, t2 = engine bloom,
//   u0 = FP16 HDR output (HdrScene), u1 = 8-bit SDR output (engine FinalLDR, no UI),
//   space6 = the engine's bindless texture table (bloom dirt), bound by the engine at dispatch.
//
// Two units meet in this shader:
//   scene-normalized  1.0 = scene paper white, the scene's SDR reference white
//   scRGB             1.0 = 80 nits; paper_white, peak and source_peak are scRGB
// The LBuffer is not SDR-referred: exposure puts the average scene near 1, and the engine's Hable curve maps that
// through a toe of slope sdr_gain of SDR white. Scaling by it keeps the engine's shadows and midtones and anchors
// them to paper white; highlights then go up to the display peak instead of into Hable's shoulder.
//
// Dual output: HDR (scRGB) to u0 for present/EXR; an SDR preview of the same frame to u1, refilling the engine's
// FinalLDR so its native screenshot / thumbnail / downsample path and FXAA keep working.

cbuffer HdrConstants : register(b0)
{
    float paper_white;
    float peak;
    float source_peak;          // >= peak, see TonemapPatch
    float black_lift;

    float sdr_gain;             // see TonemapPatch.SdrGainAt
    float bloom_mult;           // engine Post_ values from here on
    float bloom_dirt_ratio;
    float bright_desaturation;

    int   dirt_texture_id;
    int   enable_exposure;
    int   disable_tonemapping;
    int   needs_alpha_luminance;

    float white_point;          // the engine's Hable white point
    int   enable_smooth_hable;
    float natural_color;        // see map_to_display
    float padding;

    float midtones_end;         // see midtones
    float midtones_level;
    float midtones_slope;
    float padding2;
};

Texture2D source_tex            : register(t0);
Texture2D<float2> avg_luminance : register(t1);
Texture2D bloom_tex             : register(t2);   // engine bloom (low-res cascade, needs bilinear upsample)
RWTexture2D<float4> destination           : register(u0);   // FP16 HDR (HdrScene)
RWTexture2D<unorm float4> ldr_destination : register(u1);   // 8-bit SDR (engine FinalLDR), declared unorm like the engine's own

// The engine's bindless texture table (Common/Resources/Managed.hlsli).
Texture2D<float4> managed_textures[8192] : register(t0, space6);

// Engine static sampler auto-appended to every root signature (AllSamplers.hlsli: LinearSampler @ s2).
SamplerState LinearSampler : register(s2);

// SMPTE ST 2084 (PQ): scRGB 1.0 = 80 nits; PQ 1.0 = 10000 nits; so 125 scRGB = PQ 1.0.
static const float pq_m1 = 0.1593017578125;
static const float pq_m2 = 78.84375;
static const float pq_c1 = 0.8359375;
static const float pq_c2 = 18.8515625;
static const float pq_c3 = 18.6875;

float linear_to_pq(float l)
{
    float y    = max(l / 125.0, 0.0);
    float y_m1 = pow(y, pq_m1);
    return pow((pq_c1 + pq_c2 * y_m1) / (1.0 + pq_c3 * y_m1), pq_m2);
}

float pq_to_linear(float pq)
{
    float v_m2 = pow(max(pq, 0.0), 1.0 / pq_m2);
    float num  = max(v_m2 - pq_c1, 0.0);
    float den  = max(pq_c2 - pq_c3 * v_m2, 1e-6);
    float y    = pow(num / den, 1.0 / pq_m1);
    return y * 125.0;
}

float get_relative_luminance(float3 rgb)
{
    return dot(rgb, float3(0.2126, 0.7152, 0.0722));
}

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

// SDR as a gamma 2.2 monitor shows it: the engine encodes its SDR output with the piecewise sRGB curve, most monitors
// decode that with a plain 2.2, whose toe darkens the deepest tones. The HDR output takes that decode for the SDR
// range; above SDR white (1.0) there is no SDR look to keep. Composite.hlsl decodes the UI the same way,
// SceneImport.hlsl the imported scene, and the composite's SDR write-back encodes with 1 / 2.2 to undo it.
float3 sdr_on_gamma22(float3 x)
{
    return select(x <= 1.0, pow(rgb_to_srgb(x), 2.2), x);
}

// BT.2390 EETF, scRGB in and out. KS is derived from the display / source peak ratio; below it the curve is
// identity, above it a Hermite shoulder (C1 at KS, flat at the end) lands exactly on peak at source_peak. The
// black level lift comes after the shoulder, as E3 in BT.2390.
float eetf(float l)
{
    float source_pq = max(linear_to_pq(source_peak), 1e-6);
    float max_lum   = saturate(linear_to_pq(peak) / source_pq);
    float ks        = saturate(1.5 * max_lum - 0.5);

    float e = saturate(linear_to_pq(l) / source_pq);
    if (e > ks)
    {
        float t  = (e - ks) / max(1.0 - ks, 1e-6);
        float t2 = t * t;
        float t3 = t2 * t;
        e =  (2.0 * t3 - 3.0 * t2 + 1.0) * ks
          +  (t3 - 2.0 * t2 + t) * (1.0 - ks)
          +  (-2.0 * t3 + 3.0 * t2) * max_lum;
    }

    float one_minus_e    = 1.0 - e;
    float one_minus_e_sq = one_minus_e * one_minus_e;
    e += black_lift * one_minus_e_sq * one_minus_e_sq;

    return pq_to_linear(e * source_pq);
}

// The vanilla SDR color of the pixel, as the engine's tonemap computes it (ToneMapping.hlsl, Filters.hlsli) from the
// same input: its filmic curve per channel -- SmoothHable, Hable(x) / Hable(x + WhitePoint), or Hable(x) /
// Hable(WhitePoint) -- then saturate. Its channels fade to white as they grow, the look the content was authored for.
float3 hable(float3 x)
{
    const float A = 0.15, B = 0.50, C = 0.10, D = 0.20, E = 0.02, F = 0.30;
    return (x * (A * x + C * B) + D * E) / (x * (A * x + B) + D * F) - E / F;
}

float3 vanilla_color(float3 color)
{
    float w = max(white_point, 1e-3);
    return saturate(hable(color) / hable(enable_smooth_hable ? color + w : w.xxx));
}

// Vanilla midtones: below midtones_end the vanilla curve itself, the SDR midtones as the content was graded; above
// it the curve's tangent, C1, on into the EETF (TonemapPatch.MidtonesAt). m is scene-normalized max(R,G,B),
// vanilla_max the vanilla curve at the same point. Off, the tangent (0, 0, 1) is m itself.
float midtones(float m, float vanilla_max)
{
    return m < midtones_end ? vanilla_max : midtones_level + midtones_slope * (m - midtones_end);
}

// Display mapping: one EETF, on max(R,G,B) after midtones(), so no channel can exceed peak. The color direction,
// max(R,G,B) = 1, blends by natural_color from the vanilla color's (0: the authored look, bright colors fade to white)
// to the HDR color's own (1: hue and saturation kept at any brightness, as the eye sees it). n is scene-normalized,
// in and out; vanilla is vanilla_color() of the same pixel.
float3 map_to_display(float3 n, float3 vanilla)
{
    float m = max3(n);
    float vanilla_max = max3(vanilla);
    float3 vanilla_dir = vanilla / max(vanilla_max, 1e-6);
    float3 hdr_dir = n / max(m, 1e-6);
    return eetf(midtones(m, vanilla_max) * paper_white) / paper_white * lerp(vanilla_dir, hdr_dir, natural_color);
}

// SDR preview of the HDR frame (the SE1 screenshot rule): normalized to the scene paper white, so that display
// setting drops out; below half of it nothing changes, above it an extended Reinhard shoulder on max(R,G,B), C1 at
// the knee, reaches 1.0 exactly at the display peak. Hue and saturation are kept.
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

[numthreads(8, 8, 1)]
void cs_main(uint3 dtid : SV_DispatchThreadID)
{
    uint2 texel = dtid.xy;

    float2 dst_size;
    destination.GetDimensions(dst_size.x, dst_size.y);
    float2 uv = (texel + 0.5) / dst_size;

    // 1. The engine's scene color (ToneMapping.hlsl): exposure, then bloom scaled by the lens dirt mask. Bloom is a
    //    low-res cascade, bilinearly upsampled; with bloom off the engine binds a 1x1 black texture.
    float exposure = enable_exposure ? exp2(avg_luminance[uint2(0, 0)].g) : 1.0;
    float dirt = managed_textures[dirt_texture_id].SampleLevel(LinearSampler, uv, 0).r * bloom_dirt_ratio + (1.0 - bloom_dirt_ratio);
    float3 color = exposure * source_tex[texel].xyz + bloom_tex.SampleLevel(LinearSampler, uv, 0).xyz * bloom_mult * dirt;

    // 2. Scene-normalized and display-mapped. BrightDesaturation only exists in front of the engine's curve; its
    //    variant without tone mapping (debug overrides) clips at SDR white instead.
    //    A NaN or +Inf in the scene (R11G11B10 keeps both) turns the mapping into NaN on all three channels. The
    //    engine's saturate() makes such a pixel black; max() does the same here (it returns the non-NaN operand).
    float3 n;
    if (disable_tonemapping)
        n = saturate(color);
    else
    {
        color += get_relative_luminance(color) * bright_desaturation;
        n = max(map_to_display(sdr_gain * color, vanilla_color(color)), 0.0);
    }

    // 3. HDR out, its SDR range as a gamma 2.2 monitor shows it, and the SDR preview of the same frame, encoded as the
    //    engine encodes it. FXAA reads perceptual luma from .w when asked: the engine's is the luma of its sRGB-encoded
    //    SDR image.
    float3 sdr = sdr_preview(n);
    destination[texel]     = float4(sdr_on_gamma22(n) * paper_white, 1.0);
    ldr_destination[texel] = float4(sdr, needs_alpha_luminance ? get_relative_luminance(sdr) : 1.0);
}
