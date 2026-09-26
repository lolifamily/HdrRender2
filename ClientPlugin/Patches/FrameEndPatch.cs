using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using ClientPlugin.Rendering;
using HarmonyLib;
using Keen.VRage.Render12.Core.Systems;
using Keen.VRage.Render12.EngineComponents;
using Keen.VRage.Render12.Resources.BindableTextures;

namespace ClientPlugin.Patches;

// Frame end: the composite becomes final together with FinalLDR. Render12EngineComponent.Draw calls
// TakeRequestedScreenshots(FinalLDRTexture, withoutUi: false) once per frame, in-game and in menus alike, after everything
// that draws into FinalLDR and before the frame history and the present copy (CompositePatch) read it. That call is routed
// through TakeRequestedScreenshots below, which composites first, so a with-UI screenshot and its EXR see this frame.
//
// The call site, not the method: TakeRequestedScreenshots<T> is generic, and its reference-type instantiations run one
// shared body. A prefix on it worked through the main menu and loading, then went silent once the runtime recompiled that
// body after loading (tiered compilation): Harmony still listed the patch, the engine no longer ran it, and the screen
// froze on the last composite. Patches on non-generic methods survive that recompilation, so the call is rewritten in
// the non-generic caller, Draw's local function DrawInternal.
[HarmonyPatch]
internal static class FrameEndPatch
{
    // A compiler-generated name (<Draw>g__DrawInternal|52_0); its ordinal suffix may change between game builds.
    private static MethodBase TargetMethod() =>
        AccessTools.FirstMethod(typeof(Render12EngineComponent), m => m.Name.Contains("g__DrawInternal"))
        ?? throw new InvalidOperationException("Render12EngineComponent.Draw's local function DrawInternal not found");

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var codes = new List<CodeInstruction>(instructions);
        var hook = AccessTools.Method(typeof(FrameEndPatch), nameof(TakeRequestedScreenshots));
        var replaced = 0;
        foreach (var code in codes)
        {
            if (code.operand is not MethodInfo method || !IsFrameEndCall(method))
                continue;

            code.opcode = OpCodes.Call; // same stack: the instance becomes the hook's first argument
            code.operand = hook;
            replaced++;
        }

        // Exactly one, or the frame end has moved: fail at load rather than patch the wrong call.
        return replaced == 1
            ? codes
            : throw new InvalidOperationException(
                $"DrawInternal: expected one TakeRequestedScreenshots<ResizableRWRenderTargetTexture> call, found {replaced}");
    }

    private static bool IsFrameEndCall(MethodInfo method) =>
        method.DeclaringType == typeof(ScreenshotsManager)
        && method.Name == nameof(ScreenshotsManager.TakeRequestedScreenshots)
        && method.IsGenericMethod
        && method.GetGenericArguments()[0] == typeof(ResizableRWRenderTargetTexture);

    private static void TakeRequestedScreenshots(ScreenshotsManager screenshots, ResizableRWRenderTargetTexture copySource,
                                                 bool withoutUi)
    {
        HdrPipeline.EnsureInitialized(); // the first frame end, in the main menu, builds the pipeline
        var kind = UiLayerPatch.Kind;
        UiLayerPatch.Kind = FrameKind.Menu;
        if (HdrPipeline.Ready)
            HdrPipeline.ComposeFrame(kind);

        screenshots.TakeRequestedScreenshots(copySource, withoutUi);
    }
}
