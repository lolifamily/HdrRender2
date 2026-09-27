using System;
using System.Collections.Generic;
using System.IO;
using Keen.VRage.Core.Render;
using Keen.VRage.Core.Render.Data;
using Keen.VRage.Library.Diagnostics;
using Keen.VRage.Library.Filesystem;
using Keen.VRage.Library.Threading;
using Keen.VRage.Render12.Core;
using Keen.VRage.Render12.PostProcessStage;
using Keen.VRage.Render12.Resources.RootSignature;
using Keen.VRage.Render12.Resources.Shaders;
using Keen.VRage.Render12.Resources.Views;
using Keen.VRage.Render12.Utils;
using Vortice.Direct3D12;
using Vortice.DXGI;
using RParam = Keen.VRage.Render12.Resources.RootSignature.RootParameter;

namespace ClientPlugin.Rendering;

// Partial eye adaptation. EyeAdaptationJob draws the engine's exposure state with a ScreenQuadJob; that pass is swapped
// for one built from Shaders/PostProcess/EyeAdaptation/Partial.hlsl: the engine's shader, with the exposure it adapts
// toward bent. Everything that applies or compensates the exposure reads that state and stays as it is.
//
// The engine builds the pass like its own shaders: the plugin's shader folder is registered as a shader project, and
// the includes it lacks resolve from the engine's project.
internal static class PartialEyeAdaptation
{
    // The plugin's id (HdrOutput2.xml), as its shader project's.
    private static readonly Guid Project = new("109CBF17-F8C8-49C6-AC39-8EA58910A631");
    private static readonly ShaderFileHandle Shader = new(Project, "PostProcess/EyeAdaptation/Partial.hlsl");

    // The job the passes are swapped into, and its own pass, which an adaptation of 1 puts back.
    private static EyeAdaptationJob _owner;
    private static ScreenQuadJob _vanilla;

    // Per adaptation, its pass: null while it builds, the engine's own if the build failed. No pass is disposed while its
    // job lives: ScreenQuadJob.Dispose hands the object back to the engine's pool, to be borrowed again.
    private static readonly Dictionary<float, ScreenQuadJob> Passes = [];

    // The adaptation reaches the shader as a define: DynamicExposure binds this pass's root signature itself, with no
    // slot to spare. The prefix marks a define the engine does not precompile, so the shader's header need not list its
    // values.
    private const string AdaptationDefine = ShaderManager.NON_PRECOMPILED_DEFINE_PREFIX + "ADAPTATION";

    // A new table rather than an insert: shader builds in flight keep reading the old one.
    public static void Register(string shaderFolder)
    {
        var readers = CoreSystems.ShaderFileReaders;
        readers._fileReadersByGuid = new Dictionary<Guid, IFileReader>(readers._fileReadersByGuid)
        {
            [Project] = new ShaderFolder(shaderFolder)
        };
    }

    // Once per tonemapped frame (TonemapPatch), after this frame's DynamicExposure: the pass set here draws from the next.
    // With eye adaptation off the engine draws its constant exposure instead, and there is no pass to pick or build.
    public static void Update()
    {
        if (!CoreSystems.Settings.PostProcess.EyeAdaptation)
            return;

        var job = CoreSystems.SceneDrawSystem._eyeAdaptationJob;
        var current = job._eyeAdaptationJob;
        if (current == null)
            return; // the engine's own pass is still being built

        if (job != _owner)
        {
            (_owner, _vanilla) = (job, current);
            Passes.Clear();
        }

        // Rounded to the slider's steps: each value is a build of its own.
        var adaptation = MathF.Round(Config.Current.EyeAdaptation, 2);
        job._eyeAdaptationJob = adaptation >= 1f ? _vanilla : Pass(adaptation) ?? current;
    }

    // The pass for an adaptation, null while it builds.
    private static ScreenQuadJob Pass(float adaptation)
    {
        if (Passes.TryGetValue(adaptation, out var pass))
            return pass;

        Passes[adaptation] = null;
        Build(adaptation).SkipWait();
        return null;
    }

    // Started from Update on the render thread, and resumed there after each await: the entry it fills is only ever
    // touched on that thread. Bound to the render lifetime explicitly, as the engine's own render tasks outside a type
    // with a lifetime adapter do (DirectStorage.LoadTextureDDS): the adapter table only covers the engine's indexed
    // assemblies. Never throws: a failed build is logged and leaves the engine's own pass in its entry.
    private static async Task Build(float adaptation)
    {
        await Task.SetLifetime(CoreSystems.RenderLifetime);
        string error;
        try
        {
            var pass = await ScreenQuadJob.CreateAsync("HdrEyeAdaptation", RootSignature(),
                new ShaderDescription(Shader, [new ShaderDefine(AdaptationDefine, adaptation)]), [Format.R32G32_Float]);
            if (pass._pso.IsValid())
            {
                Passes[adaptation] = pass;
                return;
            }

            pass.Dispose();
            error = "no pipeline state";
        }
        catch (Exception e)
        {
            error = e.Message;
        }

        Log.Default.WriteLine(LogSeverity.Warning,
            $"[HdrOutput2] eye adaptation {adaptation} unavailable, the engine's stays: {error}");
        Passes[adaptation] = _vanilla;
    }

    // EyeAdaptationJob.InitializeAsync's, which the root signature manager hands back.
    private static RootSignature RootSignature() => CoreSystems.RootSignatures.GetRootSignature(
        "eyeAdaptationRootSignature", RootSignatureFlags.None,
        RParam.CreateCBV<IConstantBufferView>(0, 1, ShaderVisibility.Pixel),
        RParam.CreateCBV<IConstantBufferView>(1, 1, ShaderVisibility.Pixel),
        RParam.CreateCBV<IConstantBufferView>(1, 0, ShaderVisibility.Pixel),
        RParam.CreateSRV<IStructuredBufferView>(0, 0, ShaderVisibility.Pixel),
        RParam.CreateSRV<ITexture2DView>(1, 0, ShaderVisibility.Pixel));

    // A folder on disk as a shader project. The shader system only asks whether a file exists and opens it
    // (ShaderFileReaderManager); the rest of the interface is never called.
    private sealed class ShaderFolder(string root) : IFileReader
    {
        public bool FileExists(string path) => File.Exists(Path.Combine(root, path));

        public bool DirectoryExists(string path) => Directory.Exists(Path.Combine(root, path));

        public Stream OpenRead(string file, FileShare share = FileShare.Read, AdvancedFileOptions options = 0) =>
            File.Open(Path.Combine(root, file), FileMode.Open, FileAccess.Read, share);

        public bool TryOpenReadSafeHandle(string file, out AccessHandle handle, FileShare share = FileShare.Read,
                                          AdvancedFileOptions options = 0)
        {
            handle = null;
            return false;
        }

        public IEnumerable<string> EnumerateFiles(string path, bool includeHiddenEntries = false) =>
            throw new NotSupportedException();

        public IEnumerable<string> EnumerateDirectories(string path, bool includeHiddenEntries = false) =>
            throw new NotSupportedException();

        public FileSystemEntryInfo GetInfo(string path, PathType type) => throw new NotSupportedException();
    }
}
