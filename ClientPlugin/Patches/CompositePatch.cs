using ClientPlugin.Rendering;
using HarmonyLib;
using Keen.VRage.Render12.Core;
using Keen.VRage.Render12.Core.CommandLists;
using Keen.VRage.Render12.Resources.Views;

namespace ClientPlugin.Patches;

// The pre-present CopyResource (backbuffer <- FinalLDR): compositing already happened in UiLayerPatch.Postfix
// (written into Composite); here we only swap the copy source from FinalLDR (8-bit) to Composite (FP16), so the
// original copy becomes backbuffer <- Composite, formats matching. We don't dispatch here -- the present command
// list is copy-only, and building our own list would break the engine's lifecycle.
// Always the latest Composite: on hold frames UiLayerPatch skips the composite and the previous one is presented
// again, just as the engine presents the previous FinalLDRTexture while it renders into its placeholder.
[HarmonyPatch(typeof(CopyCommandList), nameof(CopyCommandList.CopyResource))]
internal static class CompositePatch
{
    private static void Prefix(ref ICopySourceView source)
    {
        if (HdrPipeline.Composite == null || !ReferenceEquals(source, CoreSystems.ScreenBuffers.FinalLDRTexture))
            return; // only take over backbuffer <- FinalLDR
        source = HdrPipeline.Composite;
    }
}
