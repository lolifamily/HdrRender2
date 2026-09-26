using System;
using ClientPlugin.Rendering;
using HarmonyLib;
using Keen.VRage.Render.Data;
using Keen.VRage.Render12.Core;
using Keen.VRage.Render12.Core.CommandLists;
using Keen.VRage.Render12.PostProcessStage;
using Keen.VRage.Render12.Resources.Views;
using Keen.VRage.Render12.Utils;

namespace ClientPlugin.Patches;

// Tonemap takeover: replace the engine's Hable with the plugin's own EETF compute, output to HdrPipeline.HdrScene (FP16).
// The engine's ldrDst gets an SDR preview of the same frame (its screenshot/thumbnail path and FXAA read it);
// the frame-end composite (FrameEndPatch) puts the UI layer over HdrScene for the backbuffer.
[HarmonyPatch(typeof(ToneMappingJob), nameof(ToneMappingJob.DoWork))]
internal static class TonemapPatch
{
    // The engine's own tonemap parameters for this frame: the struct it converts into the shaders' Post_
    // (SettingsGroup.CreateFrameSettings), including the bindless index of the bloom dirt texture.
    private static readonly StructGPUDataConvertor<PostProcessSettings.GPUImprint> PostConvertor = new();

    // The engine's Hable curve (ToneMapping/Filters.hlsli) and its slope.
    private const float A = 0.15f, B = 0.50f, C = 0.10f, D = 0.20f, E = 0.02f, F = 0.30f;

    private static float Hable(float x) => (x * (A * x + C * B) + D * E) / (x * (A * x + B) + D * F) - E / F;

    private static float HableSlope(float x)
    {
        var num = x * (A * x + C * B) + D * E;
        var den = x * (A * x + B) + D * F;
        return ((2f * A * x + C * B) * den - num * (2f * A * x + B)) / (den * den);
    }

    // Slope at black of the engine's Hable curve over its value at the white point:
    // the fraction of SDR white one exposed LBuffer unit maps to in the engine's shadows and midtones. 0.383 at
    // the stock WhitePoint of 11.2, and the same for SmoothHable, Hable(x) / Hable(x + WhitePoint).
    private static float SdrGainAt(float whitePoint) => B * (C * F - E) / (D * F * F) / Hable(Math.Max(whitePoint, 1e-3f));

    // The vanilla curve on one channel, as the shader's vanilla_color() runs it (before its saturate), and its slope.
    private static float Vanilla(float x, float w, bool smooth) => Hable(x) / Hable(smooth ? x + w : w);

    private static float VanillaSlope(float x, float w, bool smooth)
    {
        if (!smooth)
            return HableSlope(x) / Hable(w);
        var hw = Hable(x + w);
        return (HableSlope(x) * hw - Hable(x) * HableSlope(x + w)) / (hw * hw);
    }

    // Vanilla midtones: the shader keeps the vanilla curve up to where it reaches `share` of paper white and goes on
    // along its tangent there. Returns that point, the curve's value and its slope in the shader's units (m =
    // sdrGain * max(R,G,B), 1.0 = paper white). Off (share 0) it is 0, 0, 1: the tangent is m itself. SourcePeak
    // stays where HighlightRange puts it; the flatter tangent just reaches it further up.
    private static (float End, float Level, float Slope) MidtonesAt(float share, float whitePoint, bool smooth, float sdrGain)
    {
        if (share <= 0f)
            return (0f, 0f, 1f);

        // Bisect Vanilla(x) = share. Hable(x) / Hable(w) reaches 1 at w; SmoothHable only nears 1 far above it.
        var w = Math.Max(whitePoint, 1e-3f);
        float lo = 0f, hi = w;
        while (Vanilla(hi, w, smooth) < share && hi < 1e6f)
            hi *= 2f;
        for (var i = 0; i < 32; i++)
        {
            var mid = 0.5f * (lo + hi);
            if (Vanilla(mid, w, smooth) < share)
                lo = mid;
            else
                hi = mid;
        }

        var x = 0.5f * (lo + hi);
        return (sdrGain * x, Vanilla(x, w, smooth), VanillaSlope(x, w, smooth) / sdrGain);
    }

    private static bool Prefix(ComputeCommandList commandList, ITexture2DView hdrSrc, ITexture2DView exposure,
                       ITexture2DView bloom, IRWTexture2DView ldrDst, bool writeAlphaLuminance)
    {
        HdrPipeline.EnsureInitialized();
        if (!HdrPipeline.Ready)
            return true; // not ready: fall back to the engine's original tonemap

        HdrPipeline.EnsureScene(commandList, ldrDst.Resolution);

        var settings = CoreSystems.Settings.PostProcess;
        settings.Convert(PostConvertor);
        var post = PostConvertor.Result;

        var cfg = Config.Current;
        var paperWhite = cfg.ScenePaperWhite / 80f;   // nits -> scRGB (1.0 = 80 nits)
        var peak = cfg.PeakBrightness / 80f;
        var sdrGain = SdrGainAt(post.WhitePoint);
        var midtones = MidtonesAt(cfg.VanillaMidtones, post.WhitePoint, post.EnableSmoothHable != 0, sdrGain);
        var constants = new HdrConstants
        {
            PaperWhite = paperWhite,
            Peak = peak,
            // Scene content HighlightRange stops above the exposure reference reaches peak: sdrGain * 2^range
            // scene-normalized, times paper white for scRGB. Never below peak - BT.2390 would hard-clip there
            // and waste the headroom above it.
            SourcePeak = Math.Max(sdrGain * MathF.Pow(2f, cfg.HighlightRange) * paperWhite, peak),
            BlackLift = cfg.BlackLift,

            SdrGain = sdrGain,
            BloomMult = post.BloomMult,
            BloomDirtRatio = post.BloomDirtRatio,
            BrightDesaturation = post.BrightDesaturation,

            DirtTextureId = post.DirtTextureId,
            EnableExposure = post.EnableExposure,
            DisableTonemapping = settings.ToneMapping ? 0 : 1,
            NeedsAlphaLuminance = writeAlphaLuminance ? 1 : 0,

            WhitePoint = post.WhitePoint,
            EnableSmoothHable = post.EnableSmoothHable,
            NaturalColor = cfg.NaturalColor,

            MidtonesEnd = midtones.End,
            MidtonesLevel = midtones.Level,
            MidtonesSlope = midtones.Slope
        };
        using var cbv = CoreSystems.BindableBuffers.CreateTransientConstantBuffer("HdrConstants", in constants);

        var dst = HdrPipeline.HdrScene.GetRWTexture2DView(0);
        var rpb = new RootParameterBuilder(commandList);
        rpb.AddCBV(cbv);
        rpb.AddSRV(hdrSrc);
        rpb.AddSRV(exposure);
        rpb.AddSRV(bloom);     // t2 = engine bloom (low-res cascade, bilinearly upsampled and added back in the shader)
        rpb.AddUAV(dst);       // u0 = HdrScene (FP16 HDR)
        rpb.AddUAV(ldrDst);    // u1 = engine's original tonemap target (normally FinalLDRTexture); writes UI-less SDR, feeds the engine's UI-less screenshot/thumbnail path

        var res = ldrDst.Resolution;
        var tgx = (int)Math.Ceiling(res.X / 8f);
        var tgy = (int)Math.Ceiling(res.Y / 8f);
        commandList.Dispatch(HdrPipeline.EetfPso, tgx, tgy, 1);
        commandList.ClearBindings();
        SceneImportPatch.MarkTonemapped(); // this ApplyToneMapping wrote HdrScene; there is no HDR input to import
        return false; // skip the engine's original tonemap (EETF already produced HDR, and SDR was refilled into FinalLDR)
    }
}
