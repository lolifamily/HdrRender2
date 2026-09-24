using ClientPlugin.Rendering;
using HarmonyLib;
using Keen.VRage.Render12.Core;
using Keen.VRage.Render12.PostProcessStage;
using Keen.VRage.Render12.Resources.Views;

namespace ClientPlugin.Patches;

// The video player bypasses the UI's render target: VideoBatch.Draw blits each decoded frame with CopyLDRJob
// straight into ScreenBuffers.FinalLDRTexture instead of the target the UI hands it. Our present copies Composite,
// not FinalLDR, so the frame would be dropped -> black video. Route it into the UI layer so it is composited with
// the rest of the UI. UiLayer matches FinalLDR's format (R8G8B8A8_UNorm_SRgb), which is also CopyLDRJob's fixed
// RTV format, so the redirected RTV binds cleanly.
//
// Keyed on the writer, not just the destination: VideoBatch is CopyLDRJob's only caller. The engine's own copies
// into FinalLDR -- the FXAA result and the plain copy used when post-processing is off -- go through
// SceneDrawSystem's own CopyJob and must land in FinalLDR untouched; redirected, they would be cleared before the
// UI draws. On hold frames the video frame lands in a layer that isn't composited, like the rest of that frame.
[HarmonyPatch(typeof(CopyJob), nameof(CopyJob.DoWork))]
internal static class LdrBlitRedirectPatch
{
    private static void Prefix(CopyJob __instance, ref IRenderTargetView destination)
    {
        if (!ReferenceEquals(__instance, CoreSystems.CopyLDRJob) || HdrPipeline.UiLayer == null)
            return; // not the video blit, or HDR not live
        if (!ReferenceEquals(destination, CoreSystems.ScreenBuffers.FinalLDRTexture))
            return; // only take over writes into the final SDR buffer

        destination = HdrPipeline.UiLayer; // fold the video frame into the composited UI layer
    }
}
