using System;
using System.IO;
using System.Threading.Tasks;
using Keen.VRage.Library.Diagnostics;
using Keen.VRage.Library.Filesystem;
using Keen.VRage.Render12.Core;
using Keen.VRage.Render12.Resources.BindableTextures;

namespace ClientPlugin.Rendering;

// HDR EXR export (layered on top of the engine's native SDR save).
// The SDR image is already handled by the GPU (shader BT.2446 refills FinalLDR) + the engine's native
// SaveScreenshotAsync, including scaling/cropping/encoding; this does one thing only: on the render thread,
// read back the FP16 HDR source (Composite = with UI / HdrScene = without UI) into a byte[], then, off the GPU map,
// hand it to a background thread to encode the .exr, without blocking rendering or touching the engine's readback buffer / TaskCompletionSource.
internal static class HdrScreenshot
{
    // Called on the render thread: read back source -> byte[] (off the GPU map), then hand off to background encoding.
    public static void CaptureExr(ResizableRWRenderTargetTexture source, FileHandleWritable exrFile)
    {
        if (source == null)
            return;

        var footprint = CoreSystems.DeviceContext.CreateSubresourceFootprint(source.D3DResource.Description, 0);
        int rowPitch = (int)footprint.RowPitch, w = (int)footprint.Width, h = (int)footprint.Height;
        var readback = CoreSystems.BindableBuffers.CreateReadbackBuffer("HdrExr", 1, rowPitch * h);
        using (var cl = CoreSystems.FrameDispatcher.CreateDirectCommandList("HdrExr"))
            cl.CopySubresource(readback, source, 0);
        CoreSystems.FrameDispatcher.FlushAllQueuesAndWaitCpu();

        byte[] pixels;
        var token = readback.Map();
        try { pixels = token.Data[..(rowPitch * h)].ToArray(); }
        finally { token.Dispose(); readback.Dispose(); }

        // Background-encode and write to disk without blocking rendering. The EXR is an extra artifact; failure doesn't affect the engine's SDR image.
        Task.Run(() => WriteExr(exrFile, pixels, rowPitch, w, h));
    }

    private static unsafe void WriteExr(FileHandleWritable exrFile, byte[] pixels, int rowPitch, int w, int h)
    {
        try
        {
            exrFile.CreateDirectories();
            using var fs = exrFile.Open(FileMode.Create, FileAccess.Write, FileShare.Write);
            fixed (byte* ptr = pixels)
                ExrWriter.Write(fs, (IntPtr)ptr, rowPitch, w, h);
            Log.Default.WriteLine($"[HdrOutput2] EXR saved: {exrFile.Path}");
        }
        catch (Exception e)
        {
            Log.Default.WriteLine(LogSeverity.Error, $"[HdrOutput2] EXR write failed: {e}");
        }
    }
}
