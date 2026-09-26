using ClientPlugin.Rendering;
using HarmonyLib;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Render12.Core.CommandLists;
using Keen.VRage.Render12.Core.Systems;
using Keen.VRage.Render12.PostProcessStage;
using Keen.VRage.Render12.Resources.BindableTextures;
using Keen.VRage.Render12.Resources.Views;
using Rectangle = System.Drawing.Rectangle;

namespace ClientPlugin.Patches;

// HdrScene is the HDR twin of the scene image the engine composes before its UI. TonemapPatch writes it; the two other
// things the engine puts into that image are imported here, at paper white (HdrPipeline.ImportScene):
//
// [no tonemap] With post-processing off, and in the TransparentOnly and ConstantGray debug views, ApplyToneMapping skips
// ToneMappingJob and copies its HDR input straight into the 8-bit target. That HDR input is imported, not the 8-bit copy,
// so values above SDR white stay above it. Scoped to the one ApplyToneMapping call: its prefix clears the flag,
// TonemapPatch sets it, its postfix imports while it is still clear.
//
// [debug stage] ProcessPostUpscaleDebugView is where the engine draws debug output into its scene image, and all it
// writes there is debug output: debug views and texture grids (DebugPassJob.ConsumeDebugOutput), cube maps (DrawCubeMap).
// Its other writes go to temporaries -- GBuffer and simple debug outputs, the cluster view, DrawTexture's copies -- and
// never match the scene image. Its draws into the scene image go through CopyJob.DoWork, whose viewport is the rectangle
// drawn (null: all of it), so each is imported over exactly that rectangle and the rest of HdrScene keeps its HDR. The
// scene image is the toneMappingOutput it gets; DrawCubeMap draws into its finalLDRBuffer, the same texture unless
// resolution scaling without FSR upscales afterwards, which overwrites that draw in the engine too.
//
// What is not seen is not imported, and HdrScene stays as it is: the probe cube map's intensity display draws through
// DisplayHDRIntensity.DoWork, a 37-byte method the ReadyToRun image inlines into DrawCubeMap, so no patch sees it.
[HarmonyPatch]
internal static class SceneImportPatch
{
    private static bool _tonemapped;                              // TonemapPatch ran in the current ApplyToneMapping
    private static ResizableRWRenderTargetTexture _debugTarget;   // the scene image while ProcessPostUpscaleDebugView runs

    public static void MarkTonemapped() => _tonemapped = true;

    [HarmonyPatch(typeof(SceneDrawSystem), nameof(SceneDrawSystem.ApplyToneMapping))]
    [HarmonyPrefix]
    private static void ToneMappingBegin() => _tonemapped = false;

    [HarmonyPatch(typeof(SceneDrawSystem), nameof(SceneDrawSystem.ApplyToneMapping))]
    [HarmonyPostfix]
    private static void ToneMappingEnd(DirectCommandList commandList, ResizableRWRenderTargetTexture toneMappingInput,
                                       ResizableRWRenderTargetTexture toneMappingOutput)
    {
        if (_tonemapped || !HdrPipeline.Ready)
            return;

        var resolution = toneMappingOutput.Resolution;
        HdrPipeline.EnsureScene(commandList, resolution);
        HdrPipeline.ImportScene(commandList, toneMappingInput, Vector2I.Zero, resolution);
    }

    [HarmonyPatch(typeof(SceneDrawSystem), nameof(SceneDrawSystem.ProcessPostUpscaleDebugView))]
    [HarmonyPrefix]
    private static void DebugStageBegin(ResizableRWRenderTargetTexture toneMappingOutput) => _debugTarget = toneMappingOutput;

    [HarmonyPatch(typeof(SceneDrawSystem), nameof(SceneDrawSystem.ProcessPostUpscaleDebugView))]
    [HarmonyPostfix]
    private static void DebugStageEnd() => _debugTarget = null;

    [HarmonyPatch(typeof(CopyJob), nameof(CopyJob.DoWork))]
    [HarmonyPostfix]
    private static void DebugDraw(DirectCommandList commandList, IRenderTargetView destination, Rectangle? viewport)
    {
        var target = _debugTarget;
        if (target == null || !ReferenceEquals(destination, target) || !HdrPipeline.Ready || HdrPipeline.HdrScene == null)
            return;

        var rect = viewport ?? new Rectangle(0, 0, target.Resolution.X, target.Resolution.Y);
        HdrPipeline.ImportScene(commandList, target, new Vector2I(rect.X, rect.Y), new Vector2I(rect.Width, rect.Height));
    }
}
