using HarmonyLib;
using Keen.VRage.Render12.Resources.PipelineStates;
using Vortice.Direct3D12;

namespace ClientPlugin.Patches;

// Fix the bug where UI vector elements (menu panels/rects) lose all alpha on the separate UiLayer.
//
// SE2's UI vector elements are drawn by VectorRenderer with BlendState.Slug. Slug's alpha blend is
//   SrcBlendAlpha = Zero, DestBlendAlpha = One  ->  new_a = 0*src + 1*dst = dst (always the destination's original value)
// i.e. [the alpha channel never gets coverage written]. The engine doesn't care -- it draws the UI directly onto the
// opaque FinalLDR and presents, and nobody reads the alpha channel. But this plugin redirects the UI to a [transparent]
// UiLayer and relies on alpha for a premultiplied over to occlude the HDR scene; so Slug's semi-transparent dark panels
// get alpha=0, the composite degrades to scene + rgb*pw (additive), and the panels are fully transparent in the in-game
// menu with text unreadable over the scene. (sprites/text use PremultipliedAlpha, so their alpha is fine.)
//
// Fix: on GetD3D(Slug) return, change the alpha factors to One/InverseSourceAlpha so alpha accumulates coverage:
//   new_a = src_a + dst_a*(1-src_a). RGB untouched (SrcAlpha/InvSrcAlpha over a transparent background = color*alpha,
// i.e. premultiplied, matching the composite's scene*(1-ui.a)+ui.rgb*pw exactly). Only Slug's one blend changes, nothing else;
// no side effect on the engine's native FinalLDR rendering (alpha still goes unread there).
//
// Timing: VectorRenderer.InitializeAsync awaits Parallel.MoveTo(OneSecond), delaying PSO creation by 1 second;
// the plugin loads after Engine.Build and installs this patch before PSO creation, so GetD3D(Slug) already returns the modified alpha when called.
[HarmonyPatch(typeof(BlendStateManager), nameof(BlendStateManager.GetD3D))]
internal static class SlugAlphaBlendPatch
{
    private static void Postfix(BlendState state, ref BlendDescription __result)
    {
        if (state != BlendState.Slug) return;
        __result.RenderTarget[0].SourceBlendAlpha = Blend.One;
        __result.RenderTarget[0].DestinationBlendAlpha = Blend.InverseSourceAlpha;
    }
}
