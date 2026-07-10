using System;
using System.IO;
using System.Runtime.InteropServices;
using Keen.VRage.Library.Diagnostics;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Render12.Core;
using Keen.VRage.Render12.Core.CommandLists;
using Keen.VRage.Render12.Resources.BindableTextures;
using Keen.VRage.Render12.Resources.PipelineStates;
using Keen.VRage.Render12.Resources.RootSignature;
using Keen.VRage.Render12.Resources.Views;
using Keen.VRage.Render12.Utils;
using Vortice.Direct3D12;
using Vortice.Dxc;
using RParam = Keen.VRage.Render12.Resources.RootSignature.RootParameter;

namespace ClientPlugin.Rendering;

// EETF tonemap constants; layout must match cbuffer HdrConstants in HdrTonemap.hlsl (scRGB, 1.0 = 80 nits).
[StructLayout(LayoutKind.Sequential)]
internal struct HdrConstants
{
    public float PaperWhite;
    public float Peak;
    public float SourcePeak;
    public float BlackLift;
    public float BloomMult;
}

// UI composite constants; layout must match the cbuffer in Composite.hlsl.
[StructLayout(LayoutKind.Sequential)]
internal struct CompositeConstants
{
    public float PaperWhite;
    public uint HasScene;   // 1 = sample scene_tex (in-game); 0 = menu (scene=0, t0 is just a placeholder)
    public float Pad1;
    public float Pad2;
}

// Plugin's self-built compute pipelines: read .hlsl from disk, compile with DXC, build a bare PSO
// (hybrid path: dispatch via the engine so it manages resource state), and create the FP16 buffers.
// The tonemap (EETF) and UI composite passes share the resources here.
internal static class HdrPipeline
{
    private static bool _initTried;
    private static string _assetsFolder;

    // EETF tonemap: scene HDR -> HdrScene (FP16)
    private static RootSignature _eetfRootSig;
    private static ComputePSO _compositePso;

    // UI composite: HdrScene + UiLayer -> Composite (FP16)
    private static RootSignature _compositeRootSig;

    public static ComputePSO EetfPso { get; private set; }
    public static ResizableRWRenderTargetTexture HdrScene { get; private set; }
    public static ResizableRWRenderTargetTexture UiLayer { get; private set; }
    public static ResizableRWRenderTargetTexture Composite { get; private set; }

    // HDR is available only when both pipelines are ready.
    public static bool Ready => EetfPso != null && EetfPso.IsValid()
                             && _compositePso != null && _compositePso.IsValid();

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
            _eetfRootSig = CoreSystems.RootSignatures.GetRootSignature(
                "HdrEetf", RootSignatureFlags.None,
                RParam.CreateCBV<IConstantBufferView>(0, 0, ShaderVisibility.All),
                RParam.CreateSRV<ITexture2DView>(0, 0, ShaderVisibility.All),
                RParam.CreateSRV<ITexture2DView>(1, 0, ShaderVisibility.All),
                RParam.CreateSRV<ITexture2DView>(2, 0, ShaderVisibility.All),   // t2 = engine bloom
                RParam.CreateUAV<IRWTexture2DView>(0, 0, ShaderVisibility.All),
                RParam.CreateUAV<IRWTexture2DView>(1, 0, ShaderVisibility.All));
            EetfPso = BuildPso("HdrEetf", _eetfRootSig, "HdrTonemap.hlsl");

            // u0 = FP16 HDR (Composite), u1 = 8-bit SDR with UI (engine FinalLDR)
            _compositeRootSig = CoreSystems.RootSignatures.GetRootSignature(
                "HdrComposite", RootSignatureFlags.None,
                RParam.CreateCBV<IConstantBufferView>(0, 0, ShaderVisibility.All),
                RParam.CreateSRV<ITexture2DView>(0, 0, ShaderVisibility.All),
                RParam.CreateSRV<ITexture2DView>(1, 0, ShaderVisibility.All),
                RParam.CreateUAV<IRWTexture2DView>(0, 0, ShaderVisibility.All),
                RParam.CreateUAV<IRWTexture2DView>(1, 0, ShaderVisibility.All));
            _compositePso = BuildPso("HdrComposite", _compositeRootSig, "Composite.hlsl");

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

    // Create/recreate the screen-resolution FP16 scene buffer (EETF output).
    public static void EnsureBuffer(Vector2I resolution)
    {
        if (HdrScene != null && HdrScene.Resolution == resolution) return;
        HdrScene?.Dispose();
        HdrScene = CoreSystems.BindableTextures.CreateRWResizableRenderTargetTexture(
            "HdrScene", HdrResources.BackbufferFormat, resolution);
    }

    // Create/recreate the separate UI layer (8-bit sRGB, same format as the engine's FinalLDR, so the UI PSO lookup doesn't crash).
    public static void EnsureUiBuffer(Vector2I resolution)
    {
        if (UiLayer != null && UiLayer.Resolution == resolution) return;
        UiLayer?.Dispose();
        UiLayer = CoreSystems.BindableTextures.CreateRWResizableRenderTargetTexture(
            "HdrUiLayer", HdrResources.UiLayerFormat, resolution, uavFormat: HdrResources.UiLayerUavFormat);
    }

    // Create/recreate the composite output buffer (FP16, copied to the backbuffer).
    public static void EnsureComposite(Vector2I resolution)
    {
        if (Composite != null && Composite.Resolution == resolution) return;
        Composite?.Dispose();
        Composite = CoreSystems.BindableTextures.CreateRWResizableRenderTargetTexture(
            "HdrComposite", HdrResources.BackbufferFormat, resolution);
    }

    // Whether tonemap was taken over this frame (decides whether present uses HdrScene or FinalLDR as the scene).
    private static volatile bool _sceneThisFrame;
    public static void MarkScene() => _sceneThisFrame = true;
    public static bool TakeSceneFlag() { var v = _sceneThisFrame; _sceneThisFrame = false; return v; }

    // Whether the UI Postfix composited into Composite this frame (present uses this to decide whether to swap the copy source).
    private static volatile bool _compositedThisFrame;
    public static void MarkComposited() => _compositedThisFrame = true;
    public static bool TakeComposited() { var v = _compositedThisFrame; _compositedThisFrame = false; return v; }

    // Composite: scene + UI (8-bit, premultiplied over) x paper_white -> Composite (FP16, u0) + SDR (ldrOut, u1).
    // scene may be HdrScene (in-game EETF, FP16) or FinalLDR (main-menu SDR, 8-bit); both sampled as Texture2D<float4>.
    // ldrOut = NonSRGB UAV of the engine's FinalLDRTexture: writes SDR with UI, feeds the engine's native with-UI screenshot path.
    public static void DispatchComposite(ComputeCommandList commandList, ITexture2DView scene, IRWTexture2DView ldrOut, bool hasScene)
    {
        var constants = new CompositeConstants { PaperWhite = Config.Current.PaperWhite / 80f, HasScene = hasScene ? 1u : 0u };
        using var cbv = CoreSystems.BindableBuffers.CreateTransientConstantBuffer("CompositeConstants", in constants);
        var rpb = new RootParameterBuilder(commandList);
        rpb.AddCBV(cbv);
        rpb.AddSRV(scene);
        rpb.AddSRV(UiLayer);
        rpb.AddUAV(Composite.GetRWTexture2DView(0));
        rpb.AddUAV(ldrOut);
        var res = Composite.Resolution;
        var tgx = (int)Math.Ceiling(res.X / 8f);
        var tgy = (int)Math.Ceiling(res.Y / 8f);
        commandList.Dispatch(_compositePso, tgx, tgy, 1);
        commandList.ClearBindings();
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
