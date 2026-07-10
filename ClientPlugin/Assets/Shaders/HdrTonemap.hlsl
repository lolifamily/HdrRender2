// HDROutput2 - scene tonemap takeover: BT.2390 EETF (PQ space).
// Ported from the core section of SE1 HdrTonemap.hlsl.
// Pipeline: exposure -> add engine bloom -> EETF. Grain / dirt / color filters / soft-clip omitted.
//
// Bindings must match the plugin's hand-built root signature (all space0):
//   b0 = HdrConstants, t0 = scene HDR src, t1 = eye-adaptation exposure, t2 = engine bloom,
//   u0 = FP16 HDR output (HdrScene), u1 = 8-bit SDR output (engine FinalLDR, no UI)
//
// Dual output: EETF-shaped HDR goes to u0 for present/EXR; a BT.2446 SDR of the SAME
// EETF result goes to u1, refilling the engine's FinalLDR so its native screenshot /
// thumbnail / downsample path keeps working (we bypassed the engine's own tonemap).
//
// LBuffer semantics: SE's scene buffer is "SDR-tonemap-ready" (1.0 = SDR display white),
// NOT absolute-nits. Treat it as direct scRGB (1.0 = 80 nits); do NOT inverse-tonemap by
// multiplying paper_white (that would over-brighten mid illumination ~2.5x).
// paper_white is only used in the UI composite path.

cbuffer HdrConstants : register(b0)
{
    float paper_white;   // scRGB, SDR white anchor (UI composite)
    float peak;          // scRGB, display peak
    float source_peak;   // scRGB, scene peak (EETF source ceiling)
    float black_lift;    // shadow lift
    float bloom_mult;    // engine Post_.BloomMult (default 0.02); scales bloom before EETF
};

Texture2D source_tex            : register(t0);
Texture2D<float2> avg_luminance : register(t1);
Texture2D bloom_tex             : register(t2);   // engine bloom (low-res cascade, needs bilinear upsample)
RWTexture2D<float4> destination     : register(u0);   // FP16 HDR (HdrScene)
RWTexture2D<float4> ldr_destination : register(u1);   // 8-bit SDR (engine FinalLDR)

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

// BT.2446 Method A SDR tonemap (matches SE1 CPU SaveSdrPng bit-for-bit).
// Below knee: linear pass-through preserves midtones. Above knee: Reinhard shoulder
// asymptotic to 1.0. Overbright pixels desaturate toward luma (Hunt effect). Then sRGB.
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
    float3 n = max(hdr, 0.0) / pw;                        // normalize so paper_white -> 1.0
    float luma = max(get_relative_luminance(n), 1e-6);
    float overbright = saturate((luma - 1.0) / luma);    // desatThreshold = 1.0
    n = lerp(n, luma.xxx, overbright);
    float3 tm = float3(tonemap_knee(n.r), tonemap_knee(n.g), tonemap_knee(n.b));
    return float3(linear_to_srgb(tm.r), linear_to_srgb(tm.g), linear_to_srgb(tm.b));
}

[numthreads(8, 8, 1)]
void cs_main(uint3 dtid : SV_DispatchThreadID)
{
    uint2 texel = dtid.xy;

    // Exposure: borrow engine eye-adaptation only (avg_luminance.g stores log2 exposure).
    float3 source = source_tex[texel].xyz;
    float exposure = exp2(avg_luminance[uint2(0, 0)].g);
    float3 color = exposure * source;

    // Bloom: the engine already baked exposure into its prefilter, so add it un-exposed here,
    // BEFORE the EETF, so bloom highlights get shaped into the HDR range instead of clipping.
    // Bloom is a low-res cascade -> bilinear upsample via UV. Off/menu path binds a 1x1 black
    // texture (SceneDrawSystem), so this reads 0 and is a no-op. Lens-dirt term omitted.
    float2 dst_size;
    destination.GetDimensions(dst_size.x, dst_size.y);
    float2 bloom_uv = (texel + 0.5) / dst_size;
    color += bloom_tex.SampleLevel(LinearSampler, bloom_uv, 0).xyz * bloom_mult;

    // BT.2390 EETF in PQ space. When source <= display the curve collapses to a linear hard cap.
    float lum = max(get_relative_luminance(color), 1e-6);

    float display_peak_pq = linear_to_pq(peak);
    float source_peak_pq  = max(linear_to_pq(source_peak), 1e-6);
    float max_lum_norm    = saturate(display_peak_pq / source_peak_pq);
    float ks              = clamp(1.5 * max_lum_norm - 0.5, 0.0, 1.0);

    float lum_pq = linear_to_pq(lum);
    float e      = saturate(lum_pq / source_peak_pq);

    // Black lift in PQ space (BT.2390 Annex 3).
    float one_minus_e    = 1.0 - e;
    float one_minus_e_sq = one_minus_e * one_minus_e;
    e = e + black_lift * one_minus_e_sq * one_minus_e_sq;

    // Cubic Hermite shoulder (C1 continuous at KS, tangent 0 at peak).
    float e_out;
    if (e < ks)
    {
        e_out = e;
    }
    else
    {
        float t  = (e - ks) / max(1.0 - ks, 1e-6);
        float t2 = t * t;
        float t3 = t2 * t;
        e_out =  (2.0 * t3 - 3.0 * t2 + 1.0) * ks
              +  (t3 - 2.0 * t2 + t) * (1.0 - ks)
              +  (-2.0 * t3 + 3.0 * t2) * max_lum_norm;
    }

    float mapped_lum_pq = e_out * source_peak_pq;
    float mapped_lum    = pq_to_linear(mapped_lum_pq);
    float3 hdr          = color * (mapped_lum / lum);
    hdr = max(hdr, 0.0);

    destination[texel]     = float4(hdr, 1.0);                       // FP16 HDR -> present/EXR
    ldr_destination[texel] = float4(hdr_to_sdr(hdr, paper_white), 1.0); // SDR -> engine FinalLDR (no UI)
}
