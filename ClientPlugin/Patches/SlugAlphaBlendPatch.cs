using HarmonyLib;
using Keen.VRage.Render12.Resources.PipelineStates;
using Vortice.Direct3D12;

namespace ClientPlugin.Patches;

// A second Slug blend state for the UI layer, registered next to the engine's own.
//
// SE2's vector UI (panels, rects, vector fonts and images) is drawn by VectorRenderer with BlendState.Slug, whose alpha
// blend is SrcBlendAlpha = Zero, DestBlendAlpha = One: the alpha channel keeps the destination's value and never gets
// coverage. The engine draws the UI straight onto the opaque FinalLDR and nobody reads that alpha. We draw it onto a
// transparent UiLayer and composite it premultiplied over the HDR scene, so vector elements need their coverage in
// alpha: new_a = src_a + dst_a * (1 - src_a), i.e. One / InverseSourceAlpha. RGB is unchanged: SrcAlpha / InvSrcAlpha
// over a transparent background is color * alpha, premultiplied, exactly what the composite expects.
//
// Not by changing Slug itself: the same VectorRenderer PSOs render LCD screens into their offscreen textures, and the
// LCD material reads that texture's alpha as metalness (LCDPixel.hlsl). The screen background is a vector fill, so
// coverage alpha turns every LCD fully metallic -- no diffuse lighting, a mirror tinted by its content (roughness 0.04)
// -- where the engine leaves it at 0. So Slug stays the engine's, UiLayerSlug carries the coverage alpha, HdrPipeline
// builds VectorRenderer's PSOs with it, and UiLayerPatch uses them only while the UI draws into UiLayer.
//
// The constructor fills the dictionary before BlendStateManager is published to CoreSystems, so no PSO compile task can
// be reading it while the entry is added.
[HarmonyPatch(typeof(BlendStateManager), MethodType.Constructor)]
internal static class SlugAlphaBlendPatch
{
    // Outside the engine's enum range (19 values).
    public const BlendState UiLayerSlug = (BlendState)0xF0;

    private static void Postfix(BlendStateManager __instance)
    {
        var desc = __instance._blendDescriptions[BlendState.Slug];
        desc.RenderTarget[0].SourceBlendAlpha = Blend.One;
        desc.RenderTarget[0].DestinationBlendAlpha = Blend.InverseSourceAlpha;
        __instance._blendDescriptions.Add(UiLayerSlug, desc);
    }
}
