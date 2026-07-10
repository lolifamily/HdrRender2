using ClientPlugin.Rendering;
using HarmonyLib;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Render12.Core;
using Keen.VRage.Render12.Core.CommandLists;
using Keen.VRage.Render12.Resources.Views;
using Keen.VRage.Render12.UIStage;
using Vortice.Direct3D12;
using Vortice.Mathematics;

namespace ClientPlugin.Patches;

// Prefix: redirect the UI to a separate 8-bit sRGB layer (UiLayer). Postfix: after the UI is drawn, composite
// scene + UiLayer into Composite (FP16) on the [same engine command list] (heap bound, normal engine flow). We don't
// build our own command list -- doing so on the present copy list would break the engine's command-list lifecycle ->
// device removed. scene: HdrScene (EETF) if tonemap was taken over this frame, otherwise FinalLDR (main-menu SDR placed into the scRGB container).
[HarmonyPatch(typeof(MainUISystem), "DoWork", typeof(DirectCommandList), typeof(IRenderTargetView), typeof(Vector2I))]
internal static class UiLayerPatch
{
    private static void Prefix(DirectCommandList commandList, ref IRenderTargetView rt, Vector2I viewport)
    {
        HdrPipeline.EnsureInitialized(); // build the pipeline early so it's ready on the main menu's first frame
        if (!HdrPipeline.Ready)
            return; // couldn't build it: UI draws into the original target as usual

        HdrPipeline.EnsureUiBuffer(viewport);
        commandList.ClearRenderTargetView(HdrPipeline.UiLayer, new Color(0, 0, 0, 0)); // transparent background
        rt = HdrPipeline.UiLayer;                                                       // redirect UI to the separate layer
    }

    private static void Postfix(DirectCommandList commandList, Vector2I viewport)
    {
        if (!HdrPipeline.Ready)
            return;

        HdrPipeline.EnsureComposite(viewport);
        var hasScene = HdrPipeline.TakeSceneFlag() && HdrPipeline.HdrScene != null;
        // scene (t0): in-game = HdrScene (EETF). The menu has no 3D scene; the shader forces scene to 0 via has_scene=0,
        //             so t0 only needs a valid placeholder texture (use UiLayer), avoiding binding FinalLDRTexture as both t0 (SRV) + u1 (UAV).
        ITexture2DView scene = hasScene ? HdrPipeline.HdrScene : HdrPipeline.UiLayer;

        // ldrOut (u1): both paths refill the engine's FinalLDRTexture with UI-inclusive SDR, feeding native screenshots/thumbnails --
        //              both in-game and menu screenshot correctly, zero compromise.
        var ldrOut = CoreSystems.ScreenBuffers.FinalLDRTexture.GetRWTexture2DView(0);

        HdrPipeline.DispatchComposite(commandList, scene, ldrOut, hasScene);
        // Composite UAV -> CopySource, for the subsequent present CopyResource to read (cross-list tracked by AutoResourceState).
        commandList.ExplicitStateTransition(HdrPipeline.Composite.AutoResourceState, ResourceStates.CopySource);
        HdrPipeline.MarkComposited();
    }
}
