using ClientPlugin.Rendering;
using HarmonyLib;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Render12.Core;
using Keen.VRage.Render12.Core.CommandLists;
using Keen.VRage.Render12.Core.Systems;
using Keen.VRage.Render12.Resources.BindableTextures;
using Keen.VRage.Render12.Resources.Views;
using Keen.VRage.Render12.UIStage;
using Vortice.Mathematics;

namespace ClientPlugin.Patches;

// FinalLDR doubles as the UI layer. Nothing reads its scene content once DrawUI begins: the upscale, FXAA and the
// UI-less screenshot copy (SceneDrawSystem.SaveScreenshot) all come before it. So it is cleared to transparent right
// before DrawUI, and everything drawn from there -- the UI, the video player (which blits straight into FinalLDR), the
// top-most debug shapes, the debug histogram -- lands in it, to be composited over HdrScene at the frame end
// (FrameEndPatch). The prefix runs even when DrawUI returns early, so with the UI or top-most pass switched off in the
// render settings the frame still composites, as the engine still presents.
//
// Hold frames: after a camera jump, and while a UI-less screenshot waits for FSR to settle, the engine renders into
// ScreenBuffers.FinalLDRPlaceholder and keeps presenting the previous FinalLDRTexture. DrawUI then gets the placeholder:
// FinalLDR is left alone and the frame end keeps the previous composite.
//
// Menus never reach DrawUI; the engine clears FinalLDR itself before drawing its UI there.
[HarmonyPatch(typeof(SceneDrawSystem), nameof(SceneDrawSystem.DrawUI))]
internal static class UiLayerPatch
{
    // What FinalLDR holds at the frame end; set here, consumed and reset by FrameEndPatch.
    internal static FrameKind Kind;

    private static void Prefix(DirectCommandList commandList, ResizableRWRenderTargetTexture finalLDRBuffer)
    {
        if (!HdrPipeline.Ready)
            return;

        var finalLdr = CoreSystems.ScreenBuffers.FinalLDRTexture;
        if (!ReferenceEquals(finalLDRBuffer, finalLdr))
        {
            Kind = FrameKind.Hold;
            return;
        }

        commandList.ClearRenderTargetView(finalLdr, new Color(0, 0, 0, 0)); // transparent: FinalLDR's own clear value
        Kind = FrameKind.Layer;
    }
}

// Vector content (Slug) must write its coverage into the transparent layer, so while the UI draws into it VectorRenderer
// uses the UI-layer PSOs (SlugAlphaBlendPatch); the engine's own go back in the Postfix. LCD screens, rendered through
// the same VectorRenderer outside this call, keep the engine's blending. Only a layer frame needs it: a menu composites
// its UI over black, where the alpha drops out. Until the UI-layer PSOs are built (the first frame or two) vector panels
// come out see-through.
[HarmonyPatch(typeof(MainUISystem), nameof(MainUISystem.DoWork), typeof(DirectCommandList), typeof(IRenderTargetView), typeof(Vector2I))]
internal static class UiLayerVectorPatch
{
    private static void Prefix(out VectorPsos? __state)
    {
        __state = null;
        if (UiLayerPatch.Kind != FrameKind.Layer || HdrPipeline.UiVectorPsos is not { } uiPsos)
            return;

        var renderer = CoreSystems.VectorRenderer;
        __state = VectorPsos.Of(renderer);
        uiPsos.ApplyTo(renderer);
    }

    private static void Postfix(VectorPsos? __state) => __state?.ApplyTo(CoreSystems.VectorRenderer);
}
