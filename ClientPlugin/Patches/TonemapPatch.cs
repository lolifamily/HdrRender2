using System;
using ClientPlugin.Rendering;
using HarmonyLib;
using Keen.VRage.Render12.Core;
using Keen.VRage.Render12.Core.CommandLists;
using Keen.VRage.Render12.PostProcessStage;
using Keen.VRage.Render12.Resources.Views;
using Keen.VRage.Render12.Utils;

namespace ClientPlugin.Patches;

// Tonemap takeover: replace the engine's Hable with the plugin's own EETF compute, output to HdrPipeline.HdrScene (FP16).
// The engine's ldrDst (FinalLDR) is bypassed; the composite pass consumes HdrScene and writes it into the backbuffer.
// Crash self-disable: a Finalizer catches exceptions on this method's path -> persists DisabledAfterCrash (as in SE1).
[HarmonyPatch(typeof(ToneMappingJob), "DoWork")]
internal static class TonemapPatch
{
    private static bool Prefix(ComputeCommandList commandList, ITexture2DView hdrSrc, ITexture2DView exposure,
                       ITexture2DView bloom, IRWTexture2DView ldrDst, bool writeAlphaLuminance)
    {
        HdrPipeline.EnsureInitialized();
        if (!HdrPipeline.Ready)
            return true; // not ready: fall back to the engine's original tonemap

        HdrPipeline.EnsureBuffer(ldrDst.Resolution);

        var cfg = Config.Current;
        var constants = new HdrConstants
        {
            PaperWhite = cfg.PaperWhite / 80f,     // nits -> scRGB (1.0 = 80 nits)
            Peak = cfg.PeakBrightness / 80f,
            SourcePeak = cfg.SourcePeak / 80f,
            BlackLift = cfg.BlackLift,
            BloomMult = CoreSystems.Settings.PostProcess.BloomMult  // engine bloom strength (default 0.02)
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
        HdrPipeline.MarkScene(); // mark this frame as having an HDR scene; present uses HdrScene as the scene
        return false; // skip the engine's original tonemap (EETF already produced HDR, and SDR was refilled into FinalLDR)
    }
}
