using System;
using System.IO;
using ClientPlugin.Rendering;
using HarmonyLib;
using Keen.VRage.Library.Diagnostics;
using Keen.VRage.Library.Filesystem;
using Keen.VRage.Render12.Core.Systems;

namespace ClientPlugin.Patches;

// HDR EXR export -- additive, not a takeover.
//
// The tonemap (TonemapPatch) and frame-end composite (FrameEndPatch) already refilled the correct SDR into the engine's
// FinalLDRTexture, so the engine's native screenshot save can read back SDR from FinalLDR on its own: the
// downsampled thumbnail/icon is scaled by the engine's CopyJob and jpg/bmp is encoded by the engine, all for free,
// so I let them all through with return true.
//
// The only extra work here: for a full-resolution .png (a deliberate user screenshot), additionally export an .exr
// from the FP16 HDR source (Composite = with UI / HdrScene = without UI), then still return true to let the engine
// save its SDR .png. The EXR runs on a separate background thread, never touching the engine's screenDataCopy or
// TaskCompletionSource (the engine reads back and SetResults on its own), so an external screenshot awaiter still waits on the engine's SDR, unaffected.
//
// publicizer has opened up VRage.Render12, so the private nested Screenshot is directly accessible, no reflection needed.
[HarmonyPatch(typeof(ScreenshotsManager), nameof(ScreenshotsManager.WaitTillReadyAndSaveScreenshotAsync))]
internal static class ScreenshotPatch
{
    // readyFrame: the frame at which the engine's copy (recorded just before this call) has left the GPU.
    private static void Prefix(ScreenshotsManager.Screenshot screenshot, int readyFrame)
    {
        if (screenshot.DownsampleResolution != null)
            return; // thumbnail/icon: the engine's CopyJob scales from FinalLDR and saves SDR, for free
        if (!Path.GetExtension(screenshot.SaveFile.Path).Equals(".png", StringComparison.OrdinalIgnoreCase))
            return; // jpg/bmp (e.g. preview): the engine saves SDR from FinalLDR, for free; EXR only pairs with lossless png
        if (!HdrPipeline.Ready)
            return;

        // EXR source: Composite for with-UI, HdrScene for without-UI (both FP16 HDR, rendered this frame).
        var source = screenshot.DisableUi ? HdrPipeline.HdrScene : HdrPipeline.Composite;
        if (source == null)
            return; // source not ready (e.g. a very early frame): skip EXR, the engine saves SDR as usual

        // Additionally export the EXR (separate background thread), without interfering with the engine's normal SDR .png save.
        try
        {
            var exrFile = new FileHandleWritable(screenshot.SaveFile.Root,
                                                 Path.ChangeExtension(screenshot.SaveFile.Path, ".exr"));
            HdrScreenshot.CaptureExr(source, exrFile, readyFrame);
        }
        catch (Exception e)
        {
            Log.Default.WriteLine(LogSeverity.Error, $"[HdrOutput2] EXR export failed: {e}");
        }
        // Don't return false: the engine continues reading back SDR from FinalLDR, saves the .png, and SetResults on its own.
    }
}
