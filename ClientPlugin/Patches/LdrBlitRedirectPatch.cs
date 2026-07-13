using ClientPlugin.Rendering;
using HarmonyLib;
using Keen.VRage.Render12.Core;
using Keen.VRage.Render12.PostProcessStage;
using Keen.VRage.Render12.Resources.Views;

namespace ClientPlugin.Patches;

// Catch SDR content that gets blitted straight into ScreenBuffers.FinalLDRTexture, bypassing our HDR
// composite, and route it into the UI layer so it flows through the composite like everything else.
//
// The plugin's job is to bring *every* final SDR write into the HDR composite: the scene (TonemapPatch),
// the UI (UiLayerPatch), and anything else that lands in FinalLDR. A CopyJob.DoWork with destination ==
// FinalLDRTexture is exactly such a stray write -- it never reached HdrScene or UiLayer, so our present
// (which copies Composite, not FinalLDR) would drop it -> black screen. Today the only such write is the
// video player (VideoBatch blits the decoded frame via CopyLDRJob), which is what surfaced this; but we
// deliberately key on the *destination*, not on "is this video", because the fix is the same for any SDR
// source that bypasses us, and that matches what the plugin is fundamentally for.
//
// This keys on stable public infrastructure -- the CopyJob.DoWork signature and the FinalLDRTexture
// resource -- not on any method's internal IL, so an engine rework of the video path can't silently
// black-screen us. Scope is tight: every other engine CopyJob targets its own intermediate buffer (scene,
// GI, water, depth, screenshot downsample -- all different formats), never FinalLDR, so nothing else is
// touched; and the engine's own FinalLDR fills (tonemap / composite) go through compute dispatch, not
// CopyJob, so they're unaffected too. UiLayer matches FinalLDR's type and format (R8G8B8A8_UNorm_SRgb),
// which is also CopyLDRJob's fixed RTV format, so the redirected RTV binds cleanly.
[HarmonyPatch(typeof(CopyJob), nameof(CopyJob.DoWork))]
internal static class LdrBlitRedirectPatch
{
    private static void Prefix(ref IRenderTargetView destination)
    {
        if (!HdrPipeline.Ready || HdrPipeline.UiLayer == null)
            return; // HDR not live: leave the engine's SDR path untouched
        if (!ReferenceEquals(destination, CoreSystems.ScreenBuffers.FinalLDRTexture))
            return; // only take over writes into the final SDR buffer

        destination = HdrPipeline.UiLayer; // fold this SDR blit into the composited UI layer
    }
}
