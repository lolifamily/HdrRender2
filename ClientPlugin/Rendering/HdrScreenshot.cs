using System;
using System.IO;
using System.Threading.Tasks;
using Keen.VRage.Library.Diagnostics;
using Keen.VRage.Library.Filesystem;
using Keen.VRage.Render12.Core;
using Keen.VRage.Render12.Resources.BindableBuffers;
using Keen.VRage.Render12.Resources.BindableTextures;

namespace ClientPlugin.Rendering;

// HDR EXR export (layered on top of the engine's native SDR save).
// The SDR image is already handled by the GPU (the shaders refill FinalLDR with an SDR preview) + the engine's native screenshot
// save, including scaling/cropping/encoding; this does one thing only: on the render thread, record a copy of the
// FP16 HDR source (Composite = with UI / HdrScene = without UI) into a readback buffer right after the engine's own
// copy, then let a background task wait for the same frame the engine waits for (readyFrame) and encode the .exr.
// No GPU flush, nothing blocks rendering, and the engine's readback buffer / TaskCompletionSource are never touched.
internal static class HdrScreenshot
{
    private static readonly TimeSpan FrameCheckInterval = TimeSpan.FromSeconds(1 / 60.0);

    // Called on the render thread, right after the engine recorded its own screenshot copy.
    public static void CaptureExr(ResizableRWRenderTargetTexture source, FileHandleWritable exrFile, int readyFrame)
    {
        if (source == null)
            return;

        var footprint = CoreSystems.DeviceContext.CreateSubresourceFootprint(source.D3DResource.Description, 0);
        int rowPitch = (int)footprint.RowPitch, w = (int)footprint.Width, h = (int)footprint.Height;
        var readback = CoreSystems.BindableBuffers.CreateReadbackBuffer("HdrExr", 1, rowPitch * h);
        using (var cl = CoreSystems.FrameDispatcher.CreateDirectCommandList("HdrExr"))
            cl.CopySubresource(readback, source, 0);

        // Background-encode and write to disk without blocking rendering. The EXR is an extra artifact; failure doesn't affect the engine's SDR image.
        Task.Run(() => SaveWhenReady(exrFile, readback, readyFrame, rowPitch, w, h));
    }

    // Same wait as the engine's own save: once FrameId reaches readyFrame, the frame that recorded the copy has left the GPU.
    private static async Task SaveWhenReady(FileHandleWritable exrFile, ReadbackBuffer readback, int readyFrame, int rowPitch, int w, int h)
    {
        try
        {
            while (CoreSystems.FrameSpan.FrameId < readyFrame)
                await Task.Delay(FrameCheckInterval);

            WriteExr(exrFile, readback, rowPitch, w, h);
            Log.Default.WriteLine($"[HdrOutput2] EXR saved: {exrFile.Path}");
        }
        catch (Exception e)
        {
            Log.Default.WriteLine(LogSeverity.Error, $"[HdrOutput2] EXR write failed: {e}");
        }
        finally
        {
            readback.Dispose();
        }
    }

    private static unsafe void WriteExr(FileHandleWritable exrFile, ReadbackBuffer readback, int rowPitch, int w, int h)
    {
        exrFile.CreateDirectories();
        using var fs = exrFile.Open(FileMode.Create, FileAccess.Write, FileShare.Write);
        var token = readback.Map();
        try
        {
            fixed (byte* ptr = token.Data)
                ExrWriter.Write(fs, (IntPtr)ptr, rowPitch, w, h);
        }
        finally
        {
            token.Dispose();
        }
    }
}
