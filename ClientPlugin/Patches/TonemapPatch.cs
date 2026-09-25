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
// the composite pass consumes HdrScene and writes it into the backbuffer.
[HarmonyPatch(typeof(ToneMappingJob), nameof(ToneMappingJob.DoWork))]
internal static class TonemapPatch
{
    // The engine's own tonemap parameters for this frame: the struct it converts into the shaders' Post_
    // (SettingsGroup.CreateFrameSettings), including the bindless index of the bloom dirt texture.
    private static readonly StructGPUDataConvertor<PostProcessSettings.GPUImprint> PostConvertor = new();

    // Slope at black of the engine's Hable curve (ToneMapping/Filters.hlsli) over its value at the white point:
    // the fraction of SDR white one exposed LBuffer unit maps to in the engine's shadows and midtones. 0.383 at
    // the stock WhitePoint of 11.2, and the same for SmoothHable, Hable(x) / Hable(x + WhitePoint).
    private static float SdrGainAt(float whitePoint)
    {
        const float a = 0.15f, b = 0.50f, c = 0.10f, d = 0.20f, e = 0.02f, f = 0.30f;
        var w = Math.Max(whitePoint, 1e-3f);
        var hableW = (w * (a * w + c * b) + d * e) / (w * (a * w + b) + d * f) - e / f;
        return b * (c * f - e) / (d * f * f) / hableW;
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
            NaturalColor = cfg.NaturalColor
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
        HdrPipeline.MarkScene(); // mark this frame as having an HDR scene; the composite uses HdrScene as its scene
        return false; // skip the engine's original tonemap (EETF already produced HDR, and SDR was refilled into FinalLDR)
    }
}
