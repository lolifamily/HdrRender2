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
// device removed. scene: HdrScene (EETF) if tonemap was taken over this frame, otherwise black (menus have no 3D scene).
//
// Vector content (Slug) must write its coverage into the transparent layer, so for this one call VectorRenderer draws
// with the UI-layer PSOs (SlugAlphaBlendPatch); the engine's own go back in the Postfix. LCD screens, rendered through
// the same VectorRenderer outside this call, keep the engine's blending. Until the UI-layer PSOs are built (the first
// frame or two) vector panels come out see-through.
//
// Hold frames: after a camera jump, and while a UI-less screenshot waits for FSR to settle, the engine renders into
// ScreenBuffers.FinalLDRPlaceholder and keeps presenting the previous FinalLDRTexture. Do the same: leave the UI in the
// placeholder, skip the composite, and CompositePatch keeps presenting the previous Composite. Only when there is one of
// the right size to hold; otherwise (first frames, window resize) composite as usual.
[HarmonyPatch(typeof(MainUISystem), nameof(MainUISystem.DoWork), typeof(DirectCommandList), typeof(IRenderTargetView), typeof(Vector2I))]
internal static class UiLayerPatch
{
    // What the Prefix changed, for the Postfix.
    internal struct Pass
    {
        public bool Redirected;          // drawn into UiLayer: composite it
        public VectorPsos? EnginePsos;   // VectorRenderer's own PSOs, swapped out for the UI-layer ones
    }

    private static void Prefix(DirectCommandList commandList, ref IRenderTargetView rt, Vector2I viewport, out Pass __state)
    {
        __state = default;
        HdrPipeline.EnsureInitialized(); // build the pipeline early so it's ready on the main menu's first frame
        if (!HdrPipeline.Ready)
            return; // couldn't build it: UI draws into the original target as usual

        var composite = HdrPipeline.Composite;
        if (!ReferenceEquals(rt, CoreSystems.ScreenBuffers.FinalLDRTexture) && composite != null && composite.Resolution == viewport)
            return; // hold frame: the engine draws into its placeholder, the previous composite stays on screen

        HdrPipeline.EnsureUiBuffer(viewport);
        commandList.ClearRenderTargetView(HdrPipeline.UiLayer, new Color(0, 0, 0, 0)); // transparent background
        rt = HdrPipeline.UiLayer;                                                       // redirect UI to the separate layer
        __state.Redirected = true;

        if (HdrPipeline.UiVectorPsos is { } uiPsos)
        {
            var renderer = CoreSystems.VectorRenderer;
            __state.EnginePsos = VectorPsos.Of(renderer);
            uiPsos.ApplyTo(renderer);
        }
    }

    private static void Postfix(DirectCommandList commandList, Vector2I viewport, Pass __state)
    {
        __state.EnginePsos?.ApplyTo(CoreSystems.VectorRenderer);

        var hasScene = HdrPipeline.TakeSceneFlag() && HdrPipeline.HdrScene != null; // consume it on hold frames too
        if (!__state.Redirected)
            return;

        HdrPipeline.EnsureComposite(viewport);
        // scene (t0): in-game = HdrScene (EETF). The menu has no 3D scene; the shader forces scene to 0 via has_scene=0,
        //             so t0 only needs a valid placeholder texture (use UiLayer), avoiding binding FinalLDRTexture as both t0 (SRV) + u1 (UAV).
        ITexture2DView scene = hasScene ? HdrPipeline.HdrScene : HdrPipeline.UiLayer;

        // ldrOut (u1): both paths refill the engine's FinalLDRTexture with UI-inclusive SDR, feeding native screenshots/thumbnails --
        //              both in-game and menu screenshot correctly, zero compromise. FinalLDRTexture rather than the UI's target:
        //              it mirrors what is presented, also when a first frame composites over the engine's placeholder.
        var ldrOut = CoreSystems.ScreenBuffers.FinalLDRTexture.GetRWTexture2DView(0);

        HdrPipeline.DispatchComposite(commandList, scene, ldrOut, hasScene);
        // Composite UAV -> CopySource, for the subsequent present CopyResource to read (cross-list tracked by AutoResourceState).
        commandList.ExplicitStateTransition(HdrPipeline.Composite.AutoResourceState, ResourceStates.CopySource);
    }
}
