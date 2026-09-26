// HDROutput2 - scene import: content the engine puts into its scene image outside our tonemap, carried into HdrScene.
// See SceneImportPatch for the two writers:
//   - ApplyToneMapping without a tonemap: the source is the engine's HDR input, so values above SDR white stay above it.
//   - the debug stage: the source is the engine's scene image, read through its sRGB view, over the drawn rectangle.
// Either way linear in, its SDR range as a gamma 2.2 monitor shows it (as HdrTonemap.hlsl writes HdrScene), times paper
// white, clamped to [0, peak]. The R11G11B10 input keeps NaN and +Inf: sdr_on_gamma22 passes both through, clamp() maps
// NaN to 0, as max() returns the non-NaN operand, and +Inf to peak.
//
// Bindings (all space0): b0 = ImportConstants, t0 = source, u0 = HdrScene.

cbuffer ImportConstants : register(b0)
{
    float paper_white;   // scRGB
    float peak;          // scRGB, display peak
    uint2 origin;        // rectangle to import, in texels, inside HdrScene
    uint2 size;
};

Texture2D source : register(t0);
RWTexture2D<float4> destination : register(u0);

float linear_to_srgb(float c)
{
    return c <= 0.0031308 ? c * 12.92 : 1.055 * pow(max(c, 0.0), 1.0 / 2.4) - 0.055;
}

float3 rgb_to_srgb(float3 c)
{
    return float3(linear_to_srgb(c.r), linear_to_srgb(c.g), linear_to_srgb(c.b));
}

// See HdrTonemap.hlsl.
float3 sdr_on_gamma22(float3 x)
{
    return select(x <= 1.0, pow(rgb_to_srgb(x), 2.2), x);
}

[numthreads(8, 8, 1)]
void cs_main(uint3 dtid : SV_DispatchThreadID)
{
    if (any(dtid.xy >= size))
        return;

    uint2 texel = origin + dtid.xy;
    destination[texel] = float4(clamp(sdr_on_gamma22(source[texel].rgb) * paper_white, 0.0, peak), 1.0);
}
