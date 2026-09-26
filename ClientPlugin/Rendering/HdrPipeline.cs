using System;
using System.IO;
using System.Runtime.InteropServices;
using ClientPlugin.Patches;
using Keen.VRage.Core.Render;
using Keen.VRage.Library.Diagnostics;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Library.Threading;
using Keen.VRage.Render12.Core;
using Keen.VRage.Render12.Core.CommandLists;
using Keen.VRage.Render12.Core.Systems;
using Keen.VRage.Render12.Resources.BindableTextures;
using Keen.VRage.Render12.Resources.PipelineStates;
using Keen.VRage.Render12.Resources.RootSignature;
using Keen.VRage.Render12.Resources.Shaders;
using Keen.VRage.Render12.Resources.VertexInputs;
using Keen.VRage.Render12.Resources.Views;
using Keen.VRage.Render12.UIStage.Vectors;
using Keen.VRage.Render12.Utils;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Dxc;
using RParam = Keen.VRage.Render12.Resources.RootSignature.RootParameter;
using VertexLayout = Keen.VRage.Render12.Resources.VertexInputs.InputLayoutDescription;

namespace ClientPlugin.Rendering;

// EETF tonemap constants; layout must match cbuffer HdrConstants in HdrTonemap.hlsl (scRGB, 1.0 = 80 nits).
[StructLayout(LayoutKind.Sequential)]
internal struct HdrConstants
{
    public float PaperWhite;          // scene paper white
    public float Peak;                // display peak
    public float SourcePeak;          // EETF source ceiling, >= Peak
    public float BlackLift;           // BT.2390 b

    public float SdrGain;             // see TonemapPatch.SdrGainAt
    public float BloomMult;           // engine Post_ values from here on
    public float BloomDirtRatio;
    public float BrightDesaturation;

    public int DirtTextureId;         // index into the engine's bindless texture table
    public int EnableExposure;
    public int DisableTonemapping;
    public int NeedsAlphaLuminance;   // FXAA reads luma from the SDR output's alpha

    public float WhitePoint;          // engine Post_: its Hable curve, for the vanilla color
    public int EnableSmoothHable;
    public float NaturalColor;
    public float Padding;

    public float MidtonesEnd;         // see TonemapPatch.MidtonesAt
    public float MidtonesLevel;
    public float MidtonesSlope;
    public float Padding2;
}

// Frame composite constants; layout must match the cbuffer in Composite.hlsl.
[StructLayout(LayoutKind.Sequential)]
internal struct CompositeConstants
{
    public float PaperWhite;     // scene paper white: the SDR preview normalizes to it
    public float UiBrightness;
    public float Peak;           // display peak: where the SDR preview's shoulder ends
    public uint HasScene;        // 0 = menu: no 3D scene, t0 is a placeholder and not sampled
    public uint HasUi;           // 0 = a held frame whose UI went to the engine's placeholder: the scene alone
    public uint WriteBack;       // 1 = replace FinalLDR with the SDR of the composite
}

// Scene import constants; layout must match the cbuffer in SceneImport.hlsl.
[StructLayout(LayoutKind.Sequential)]
internal struct ImportConstants
{
    public float PaperWhite;
    public float Peak;
    public uint OriginX;         // rectangle to import, in texels, inside HdrScene
    public uint OriginY;
    public uint Width;
    public uint Height;
}

// What FinalLDR holds at the frame end. UiLayerPatch sets it on in-game frames; menus never reach it and stay Menu.
internal enum FrameKind
{
    Menu,    // no 3D scene: the engine cleared FinalLDR and drew the UI into it
    Layer,   // FinalLDR was cleared into the UI layer over this frame's HdrScene
    Hold     // the frame was drawn into the engine's placeholder; FinalLDR still holds the previous one
}

// The three PSOs VectorRenderer draws Slug content with: vector fonts, single- and multi-color vector graphics.
internal readonly record struct VectorPsos(GraphicsPSO Font, GraphicsPSO General, GraphicsPSO MultiColor)
{
    public static VectorPsos Of(VectorRenderer renderer) =>
        new(renderer._fontPSO, renderer._vectorGeneralPSO, renderer._vectorMultiColorPSO);

    public void ApplyTo(VectorRenderer renderer)
    {
        renderer._fontPSO = Font;
        renderer._vectorGeneralPSO = General;
        renderer._vectorMultiColorPSO = MultiColor;
    }
}

// Plugin's self-built compute pipelines: read .hlsl from disk, compile with DXC, build a bare PSO
// (hybrid path: dispatch via the engine so it manages resource state), and create the FP16 buffers.
// The tonemap (EETF), scene import and frame composite passes share the resources here.
internal static class HdrPipeline
{
    private static bool _initTried;
    private static string _assetsFolder;

    // EETF tonemap: scene HDR -> HdrScene (FP16)
    private static RootSignature _eetfRootSig;

    // Scene import: linear content -> HdrScene
    private static RootSignature _importRootSig;
    private static ComputePSO _importPso;

    // Frame composite: HdrScene + FinalLDR (UI layer) -> Composite (FP16)
    private static RootSignature _compositeRootSig;
    private static ComputePSO _compositePso;

    // Bound as the composite's scene in menus, where it is not sampled.
    private static ResizableRWRenderTargetTexture _noScene;

    public static ComputePSO EetfPso { get; private set; }
    public static ResizableRWRenderTargetTexture HdrScene { get; private set; }
    public static ResizableRWRenderTargetTexture Composite { get; private set; }

    // HDR is available only when all pipelines are ready.
    public static bool Ready => EetfPso != null && EetfPso.IsValid()
                             && _importPso != null && _importPso.IsValid()
                             && _compositePso != null && _compositePso.IsValid();

    // VectorRenderer's PSOs built with SlugAlphaBlendPatch.UiLayerSlug, for the UI layer. Compiled by the engine's
    // PSO manager like its own; null until done, a frame or two after init.
    private static Task<GraphicsPSO>[] _uiVectorPsoTasks;
    private static VectorPsos? _uiVectorPsos;

    public static VectorPsos? UiVectorPsos
    {
        get
        {
            if (_uiVectorPsos == null && _uiVectorPsoTasks != null
                && Array.TrueForAll(_uiVectorPsoTasks, t => t.GetAwaiter().IsCompleted))
            {
                _uiVectorPsos = new VectorPsos(_uiVectorPsoTasks[0].GetAwaiter().GetResult(),
                                               _uiVectorPsoTasks[1].GetAwaiter().GetResult(),
                                               _uiVectorPsoTasks[2].GetAwaiter().GetResult());
            }
            return _uiVectorPsos;
        }
    }

    // Pulsar injects the asset folder via Plugin.LoadAssets.
    public static void SetAssetsFolder(string folder) => _assetsFolder = folder;

    // Lazy init (called on the first frame on the render thread): build root signatures + read and compile shaders + build PSOs.
    public static void EnsureInitialized()
    {
        if (_initTried) return;
        _initTried = true;
        try
        {
            // Layout matches the shader, all space0; RootSignatureManager auto-appends sampler/assert slots the shader can ignore.
            // u0 = FP16 HDR (HdrScene), u1 = 8-bit SDR (engine FinalLDR, refills native screenshot path)
            // Managed = the engine's bindless texture table (space6), for the bloom dirt mask; the engine binds it at
            // dispatch, as for its own ToneMapping root signature.
            _eetfRootSig = CoreSystems.RootSignatures.GetRootSignature(
                "HdrEetf", RootSignatureFlags.None,
                RParam.CreateCBV<IConstantBufferView>(0, 0, ShaderVisibility.All),
                RParam.CreateSRV<ITexture2DView>(0, 0, ShaderVisibility.All),
                RParam.CreateSRV<ITexture2DView>(1, 0, ShaderVisibility.All),
                RParam.CreateSRV<ITexture2DView>(2, 0, ShaderVisibility.All),   // t2 = engine bloom
                RParam.CreateUAV<IRWTexture2DView>(0, 0, ShaderVisibility.All),
                RParam.CreateUAV<IRWTexture2DView>(1, 0, ShaderVisibility.All),
                RParam.CreateManaged(ShaderVisibility.All));
            EetfPso = BuildPso("HdrEetf", _eetfRootSig, "HdrTonemap.hlsl");

            // t0 = linear source (the engine's HDR input, or its scene image through the sRGB view), u0 = HdrScene
            _importRootSig = CoreSystems.RootSignatures.GetRootSignature(
                "HdrImport", RootSignatureFlags.None,
                RParam.CreateCBV<IConstantBufferView>(0, 0, ShaderVisibility.All),
                RParam.CreateSRV<ITexture2DView>(0, 0, ShaderVisibility.All),
                RParam.CreateUAV<IRWTexture2DView>(0, 0, ShaderVisibility.All));
            _importPso = BuildPso("HdrImport", _importRootSig, "SceneImport.hlsl");

            // t0 = scene, u0 = FP16 HDR (Composite), u1 = FinalLDR (UI layer in, SDR with UI out)
            _compositeRootSig = CoreSystems.RootSignatures.GetRootSignature(
                "HdrComposite", RootSignatureFlags.None,
                RParam.CreateCBV<IConstantBufferView>(0, 0, ShaderVisibility.All),
                RParam.CreateSRV<ITexture2DView>(0, 0, ShaderVisibility.All),
                RParam.CreateUAV<IRWTexture2DView>(0, 0, ShaderVisibility.All),
                RParam.CreateUAV<IRWTexture2DView>(1, 0, ShaderVisibility.All));
            _compositePso = BuildPso("HdrComposite", _compositeRootSig, "Composite.hlsl");

            _noScene = CoreSystems.BindableTextures.CreateRWResizableRenderTargetTexture(
                "HdrNoScene", HdrResources.BackbufferFormat, new Vector2I(1, 1));

            BuildUiVectorPsos();

            Log.Default.WriteLine("[HdrOutput2] pipelines initialized");
        }
        catch (Exception e)
        {
            // Self-built pipeline failure (DXC compile / DXIL-root-signature contract) = HDR is definitively unavailable.
            // Record the disable flag to skip on next launch (avoids spamming the fallback log every frame); the user retries after clearing it in config.
            Log.Default.WriteLine(LogSeverity.Error, $"[HdrOutput2] pipeline init failed, disabling HDR: {e}");
            Config.Current.DisabledAfterCrash = true;
            try { ConfigStorage.Save(Config.Current); }
            catch (Exception ex) { Log.Default.WriteLine(LogSeverity.Warning, $"[HdrOutput2] failed to save crash flag: {ex.Message}"); }
        }
    }

    private static ComputePSO BuildPso(string name, RootSignature rootSig, string shaderFile)
    {
        var dxil = CompileCompute(LoadShaderSource(shaderFile), "cs_main");
        var desc = new ComputePipelineStateDescription
        {
            RootSignature = rootSig.D3DRootSignature,
            ComputeShader = dxil
        };
        var d3dPso = CoreSystems.DeviceContext.CreateD3DComputePipelineState(name, in desc);
        var pso = new ComputePSO();
        pso.Initialize(rootSig, d3dPso);
        return pso;
    }

    // VectorRenderer.CreatePSO's three PSOs, blended with UiLayerSlug instead of Slug, for the UI layer: FinalLDR's format.
    private static void BuildUiVectorPsos()
    {
        var layout = default(VertexLayout)
            .AddPerVertex(InputSemantic.Attrib0, Format.R32G32B32A32_Float, 0)
            .AddPerVertex(InputSemantic.Attrib1, Format.R32G32B32A32_Float, 0)
            .AddPerVertex(InputSemantic.Attrib2, Format.R32G32B32A32_Float, 0)
            .AddPerVertex(InputSemantic.Attrib3, Format.R32G32B32A32_Float, 0)
            .AddPerVertex(InputSemantic.Attrib4, Format.R8G8B8A8_UNorm, 0);
        var rootSig = CoreSystems.RootSignatures.GetRootSignature(
            "VectorRenderer", RootSignatureFlags.AllowInputAssemblerInputLayout,
            RParam.CreateCBV<IConstantBufferView>(0, 0, ShaderVisibility.All),
            RParam.CreateSRV<ITexture2DView>(0, 0, ShaderVisibility.Pixel),
            RParam.CreateSRV<ITexture2DView>(1, 0, ShaderVisibility.Pixel));

        Task<GraphicsPSO> Create(ShaderFileHandle vertex, ShaderFileHandle pixel) =>
            CoreSystems.GraphicsPSOs.CreateAsync("HdrUiLayerVector", rootSig, new GraphicsPSODescription
            {
                VertexShader = new ShaderDescription(vertex),
                PixelShader = new ShaderDescription(pixel),
                RasterizerState = RasterizerState.Slug,
                BlendState = SlugAlphaBlendPatch.UiLayerSlug,
                DepthStencilState = DepthStencilState.None,
                SampleMask = uint.MaxValue,
                PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
                RenderTargetFormats = [ScreenBuffers.LDR_FORMAT],
                DepthStencilFormat = Format.Unknown
            }, layout);

        _uiVectorPsoTasks =
        [
            Create(ShaderHandles.VectorFontVertex, ShaderHandles.VectorFontPixel),
            Create(ShaderHandles.VectorGeneralVertex, ShaderHandles.VectorGeneralPixel),
            Create(ShaderHandles.VectorMultiColorVertex, ShaderHandles.VectorMultiColorPixel)
        ];
    }

    // FP16 scene buffer (EETF output) at the tonemap's resolution. With resolution scaling but no FSR the engine
    // tonemaps below output resolution and upscales afterwards (the composite does that for HdrScene), and under
    // dynamic resolution that size changes every few frames. Like the engine's own buffers: allocate once at output
    // size and Resize within it; recreate only when the output size itself changes.
    public static void EnsureScene(CopyCommandList commandList, Vector2I resolution)
    {
        var maxResolution = CoreSystems.SwapChain.Resolution;
        if (HdrScene == null || HdrScene.MaxResolution != maxResolution)
        {
            HdrScene?.Dispose();
            HdrScene = CoreSystems.BindableTextures.CreateRWResizableRenderTargetTexture(
                "HdrScene", HdrResources.BackbufferFormat, maxResolution);
        }
        HdrScene.Resize(commandList, resolution);
    }

    // Create/recreate the composite output buffer (FP16, copied to the backbuffer).
    private static void EnsureComposite(Vector2I resolution)
    {
        if (Composite != null && Composite.Resolution == resolution) return;
        Composite?.Dispose();
        Composite = CoreSystems.BindableTextures.CreateRWResizableRenderTargetTexture(
            "HdrComposite", HdrResources.BackbufferFormat, resolution);
    }

    // Write linear content times paper white into HdrScene over a rectangle, clipped to it (SceneImportPatch): the HDR
    // input of a frame the engine did not tonemap, or debug output it drew into its scene image. The source is read at
    // the same texels: it has HdrScene's resolution.
    public static void ImportScene(ComputeCommandList commandList, ITexture2DView source, Vector2I origin, Vector2I size)
    {
        var res = HdrScene.Resolution;
        int x0 = Math.Max(origin.X, 0), y0 = Math.Max(origin.Y, 0);
        int x1 = Math.Min(origin.X + size.X, res.X), y1 = Math.Min(origin.Y + size.Y, res.Y);
        if (x1 <= x0 || y1 <= y0)
            return;

        var cfg = Config.Current;
        var constants = new ImportConstants
        {
            PaperWhite = cfg.ScenePaperWhite / 80f,   // nits -> scRGB (1.0 = 80 nits)
            Peak = cfg.PeakBrightness / 80f,
            OriginX = (uint)x0,
            OriginY = (uint)y0,
            Width = (uint)(x1 - x0),
            Height = (uint)(y1 - y0)
        };
        using var cbv = CoreSystems.BindableBuffers.CreateTransientConstantBuffer("ImportConstants", in constants);
        var rpb = new RootParameterBuilder(commandList);
        rpb.AddCBV(cbv);
        rpb.AddSRV(source);
        rpb.AddUAV(HdrScene.GetRWTexture2DView(0));
        var tgx = (int)Math.Ceiling((x1 - x0) / 8f);
        var tgy = (int)Math.Ceiling((y1 - y0) / 8f);
        commandList.Dispatch(_importPso, tgx, tgy, 1);
        commandList.ClearBindings();
    }

    // Frame end (FrameEndPatch): the engine's FinalLDR is final, about to be read by its screenshots and presented, and
    // the composite becomes final with it: the UI layer over HdrScene, or over black in menus. A held frame keeps the
    // previous composite, as the engine keeps presenting the previous FinalLDR -- but only one of the backbuffer's size
    // can be held. Otherwise (first frames, a resize during a hold) the frame's own HdrScene is shown, without the UI,
    // which went to the placeholder: never an SDR frame in place of the HDR one.
    public static void ComposeFrame(FrameKind kind)
    {
        var resolution = CoreSystems.SwapChain.Resolution;
        if (kind == FrameKind.Hold && Composite != null && Composite.Resolution == resolution)
            return;

        EnsureComposite(resolution);
        var hasScene = kind != FrameKind.Menu && HdrScene != null;
        var cfg = Config.Current;
        var constants = new CompositeConstants
        {
            PaperWhite = cfg.ScenePaperWhite / 80f,   // nits -> scRGB (1.0 = 80 nits)
            UiBrightness = cfg.UiBrightness / 80f,
            Peak = cfg.PeakBrightness / 80f,
            HasScene = hasScene ? 1u : 0u,
            HasUi = kind != FrameKind.Hold ? 1u : 0u,
            WriteBack = kind == FrameKind.Layer ? 1u : 0u   // menus keep the engine's own FinalLDR
        };

        using var commandList = CoreSystems.FrameDispatcher.CreateDirectCommandList("HdrComposite");
        using var cbv = CoreSystems.BindableBuffers.CreateTransientConstantBuffer("CompositeConstants", in constants);
        var rpb = new RootParameterBuilder(commandList);
        rpb.AddCBV(cbv);
        rpb.AddSRV(hasScene ? HdrScene : _noScene);
        rpb.AddUAV(Composite.GetRWTexture2DView(0));
        rpb.AddUAV(CoreSystems.ScreenBuffers.FinalLDRTexture.GetRWTexture2DView(0));
        var tgx = (int)Math.Ceiling(resolution.X / 8f);
        var tgy = (int)Math.Ceiling(resolution.Y / 8f);
        commandList.Dispatch(_compositePso, tgx, tgy, 1);
        commandList.ClearBindings();
        // Composite UAV -> CopySource, for the present CopyResource to read (cross-list tracked by AutoResourceState).
        commandList.ExplicitStateTransition(Composite.AutoResourceState, ResourceStates.CopySource);
    }

    // Pulsar injects the asset folder via LoadAssets; shaders are read from <assetFolder>/Shaders (same as SE1).
    private static string LoadShaderSource(string file)
    {
        return string.IsNullOrEmpty(_assetsFolder)
            ? throw new InvalidOperationException("Assets folder not set (LoadAssets was not invoked)")
            : File.ReadAllText(Path.Combine(_assetsFolder, "Shaders", file));
    }

    private static byte[] CompileCompute(string source, string entryPoint)
    {
        var options = new DxcCompilerOptions { ShaderModel = DxcShaderModel.Model6_1 };
        var result = DxcCompiler.Compile(
            DxcShaderStage.Compute, source, entryPoint, options,
            null, [], null, ["-HV 2021"]);
        return result.GetStatus().Failure
            ? throw new InvalidOperationException($"{entryPoint} shader compile failed: " + result.GetErrors())
            : result.GetResult().ToArray();
    }
}
