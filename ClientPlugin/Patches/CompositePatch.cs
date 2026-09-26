using ClientPlugin.Rendering;
using HarmonyLib;
using Keen.VRage.Render12.Core;
using Keen.VRage.Render12.Core.CommandLists;
using Keen.VRage.Render12.Resources.Views;

namespace ClientPlugin.Patches;

// The pre-present CopyResource (backbuffer <- FinalLDR): the composite was finished at the frame end (FrameEndPatch),
// written into Composite; here we only swap the copy source from FinalLDR (8-bit) to Composite (FP16), so the original
// copy becomes backbuffer <- Composite, formats matching. The frame end always leaves a composite of the backbuffer's
// size: this frame's, or on a hold frame the previous one -- held only when it has that size -- just as the engine
// presents the previous FinalLDRTexture while it renders into its placeholder.
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
