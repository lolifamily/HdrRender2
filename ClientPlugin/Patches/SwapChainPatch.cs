using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using ClientPlugin.Rendering;
using HarmonyLib;
using Keen.VRage.Library.Diagnostics;
using Keen.VRage.Render.Utils;
using Keen.VRage.Render12.Core.Device;
using Vortice.DXGI;

namespace ClientPlugin.Patches;

// Change SE2's swapchain backbuffer from 8-bit sRGB to FP16 (scRGB) and set the HDR colorspace.
// SE2's SwapChain is already flip-model + IDXGISwapChain3, so no destroy/recreate needed -- just change the format constant + SetColorSpace1.
[HarmonyPatch(typeof(SwapChain))]
internal static class SwapChainPatch
{
    private static readonly int SrgbFormat = (int)Format.R8G8B8A8_UNorm_SRgb;
    private static readonly int Fp16Format = (int)HdrResources.BackbufferFormat;

    // Resolved from the loaded type -- we're a post-Engine.Build patch, not a preloader, so DXGIFormatExt is
    // available. The engine wraps its sRGB backbuffer format in .NonSRGB(); matching this exact MethodInfo instead
    // of the "NonSRGB" name string is the type-anchored check that keeps the nop from firing on an unrelated call.
    private static readonly MethodInfo NonSrgbMethod =
        AccessTools.Method(typeof(DXGIFormatExt), nameof(DXGIFormatExt.NonSRGB));

    // desc.Format: R8G8B8A8_UNorm_SRgb.NonSRGB() -> FP16 (Update()'s recreate also goes through this method; one site covers both)
    [HarmonyPatch(nameof(SwapChain.CreateD3DSwapChain))]
    [HarmonyTranspiler]
    private static List<CodeInstruction> CreateD3DSwapChain_Transpiler(IEnumerable<CodeInstruction> instructions)
        => ReplaceSrgbWithFp16(instructions, nameof(SwapChain.CreateD3DSwapChain));

    // backbuffer wrapper's RTV format -> FP16
    [HarmonyPatch(nameof(SwapChain.InitializeBackBufferWrappers))]
    [HarmonyTranspiler]
    private static List<CodeInstruction> InitializeBackBufferWrappers_Transpiler(IEnumerable<CodeInstruction> instructions)
        => ReplaceSrgbWithFp16(instructions, nameof(SwapChain.InitializeBackBufferWrappers));

    // Replace the R8G8B8A8_UNorm_SRgb constant inside the method with R16G16B16A16_Float.
    // If a .NonSRGB() call immediately follows, nop it out: FP16 has no sRGB variant, so the call is redundant (and may return Unknown).
    private static List<CodeInstruction> ReplaceSrgbWithFp16(IEnumerable<CodeInstruction> instructions, string method)
    {
        var codes = new List<CodeInstruction>(instructions);
        var replaced = 0;
        for (var i = 0; i < codes.Count; i++)
        {
            if (!codes[i].LoadsConstant(SrgbFormat))
                continue;

            codes[i].opcode = OpCodes.Ldc_I4;
            codes[i].operand = Fp16Format;
            replaced++;

            if (i + 1 >= codes.Count || NonSrgbMethod == null || !codes[i + 1].Calls(NonSrgbMethod))
                continue;

            codes[i + 1].opcode = OpCodes.Nop;
            codes[i + 1].operand = null;
        }
        if (replaced == 0)
            Log.Default.WriteLine(LogSeverity.Warning,
                $"[HdrOutput2] SwapChain.{method}: no sRGB format constant found -> HDR will NOT engage (engine layout changed?)");
        else
            Log.Default.WriteLine($"[HdrOutput2] SwapChain.{method}: {replaced} format constant(s) -> FP16");
        return codes;
    }

    // Set the scRGB colorspace after swapchain creation (the SE2 engine never calls this; the plugin supplies it).
    [HarmonyPatch(nameof(SwapChain.CreateD3DSwapChain))]
    [HarmonyPostfix]
    private static void CreateD3DSwapChain_Postfix(IDXGISwapChain3 __result)
    {
        try
        {
            const ColorSpaceType cs = HdrResources.ScRgbColorSpace;
            if ((__result.CheckColorSpaceSupport(cs) & SwapChainColorSpaceSupportFlags.Present) != 0)
            {
                __result.SetColorSpace1(cs);
                Log.Default.WriteLine("[HdrOutput2] SwapChain colorspace -> scRGB (RgbFullG10NoneP709)");
            }
            else
            {
                Log.Default.WriteLine(LogSeverity.Warning, "[HdrOutput2] scRGB colorspace not supported on this swapchain");
            }
        }
        catch (Exception e)
        {
            Log.Default.WriteLine(LogSeverity.Error, $"[HdrOutput2] SetColorSpace1 failed: {e}");
        }
    }
}
