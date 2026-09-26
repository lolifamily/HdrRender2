using System;
using System.Reflection;
using ClientPlugin.Settings;
using ClientPlugin.Tools;
using HarmonyLib;
using JetBrains.Annotations;
using Keen.VRage.Core.Plugins;
using Keen.VRage.Library.Diagnostics;
using Vortice.DXGI;

// Define assembly version when compiled by Pulsar
#if !DEV_BUILD
[assembly: AssemblyVersion("2.1.0.0")]
[assembly: AssemblyFileVersion("2.1.0.0")]
#endif

namespace ClientPlugin;

public class Plugin : IPlugin
{
    internal const string Name = "HdrOutput2";

    // Set by DetermineHdrMode() at ctor time; surfaced in the settings screen via Config.Title / Config.DisplayInfo.
    internal static string StatusTitle { get; private set; } = "HDR Output 2";
    internal static string StatusInfo { get; private set; }

    // The data directory will be provided by a proper SDK in the future.
    // This static function is currently injected by Pulsar, which will
    // remain compatible, even after the SDK's release.
#pragma warning disable CS0649 // This field is assigned by Pulsar
    // ReSharper disable once InconsistentNaming
    private static Func<string, string, string> GetConfigPath;
#pragma warning restore CS0649
    public static string DataDir { get; private set; }

    public Plugin()
    {
        // Resolve DataDir now — Pulsar injects GetConfigPath before constructing us.
        // Must precede Config.Current, whose load reads DataDir via ConfigStorage.
        DataDir = GetConfigPath(Name, null);
        _ = Config.Current;

        Log.Default.WriteLine($"[{Name}] Loaded plugin.");

        if (Config.Current.DisabledAfterCrash)
        {
            StatusTitle = "HDR Output 2 — Disabled (last launch crashed)";
            StatusInfo = "Auto-disabled after a crash during plugin init.\nUncheck DisabledAfterCrash in the config to retry on next launch.";
            Log.Default.WriteLine(LogSeverity.Warning, $"[{Name}] HDR was disabled after a previous crash. Uncheck DisabledAfterCrash in the config to retry.");
            return;
        }

        // Probe the display + honor ForceEnable BEFORE PatchAll. Not HDR and not forced -> apply
        // nothing, engine stays native SDR (SE1 semantics: inactive == zero patches == zero risk).
        if (!DetermineHdrMode())
        {
            Log.Default.WriteLine($"[{Name}] HDR inactive (display not HDR, ForceEnable off) -- no patches applied. {StatusInfo.Replace('\n', ' ')}");
            return;
        }

#if DEBUG
        Harmony.DEBUG = true;
#endif
        var harmony = new Harmony(Name);
        harmony.PatchAll(Assembly.GetExecutingAssembly());
        Log.Default.WriteLine($"[{Name}] Applied patches");
    }

    // Decide whether HDR engages — runs in the ctor, BEFORE PatchAll, once and for good (SE1 semantics:
    // HDR display -> Active; not HDR but ForceEnable -> Forced; otherwise Inactive -> zero patches).
    //
    // We probe the PRIMARY display only. This early -- before the game's window/swapchain exist -- we can't
    // know which display the game will actually open on: the persisted display config lives behind FileSystem's
    // AppData partition, which LibraryEngineComponent doesn't mount until after the plugin ctor. So we check the
    // primary monitor: correct for the single-display / primary case; anyone on a non-primary HDR display just
    // flips ForceEnable. Everything stays output(display)-level -- the GPU never enters the decision. No user32
    // (crashes native Linux); pure DXGI is DXVK-safe.
    private static bool DetermineHdrMode()
    {
        var displayIsHdr = false;
        StatusInfo = "No HDR display detected";
        try
        {
            displayIsHdr = ProbePrimaryDisplayHdr();
        }
        catch (Exception e)
        {
            Log.Default.WriteLine(LogSeverity.Warning, $"[{Name}] display probe failed: {e.Message}");
        }

        if (displayIsHdr)
        {
            StatusTitle = "HDR Output 2 — Active";
            return true;
        }
        if (Config.Current.ForceEnable)
        {
            StatusTitle = "HDR Output 2 — Forced";
            return true;
        }
        StatusTitle = "HDR Output 2 — Inactive";
        return false;
    }

    // Find the primary display (desktop-coordinate origin) across all DXGI adapters and report whether it's in
    // HDR mode. Pure output fields; DXVK-safe on Linux; no user32. Fills StatusInfo for the settings screen.
    private static bool ProbePrimaryDisplayHdr()
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint ai = 0; factory.EnumAdapters1(ai, out var adapter).Success && adapter != null; ai++)
        {
            using var _ = adapter;
            if (TryProbePrimaryOutput(adapter, out var hdr))
                return hdr;
        }

        StatusInfo = "No display detected";
        return false;
    }

    // Scan one adapter's outputs for the primary display (desktop origin). Returns true -- and sets hdr +
    // StatusInfo -- once found; false if this adapter carries no primary output.
    private static bool TryProbePrimaryOutput(IDXGIAdapter1 adapter, out bool hdr)
    {
        hdr = false;
        for (uint oi = 0; adapter.EnumOutputs(oi, out var output).Success && output != null; oi++)
        {
            using var _ = output;
            using var output6 = output.QueryInterface<IDXGIOutput6>();
            var d = output6.Description1;
            if (d.DesktopCoordinates.Left != 0 || d.DesktopCoordinates.Top != 0)
                continue; // only the primary display (desktop origin) drives the decision

            hdr = d.ColorSpace == Rendering.HdrResources.Hdr10ColorSpace;
            StatusInfo = $"Peak: {d.MaxLuminance:F0} nits | Full frame: {d.MaxFullFrameLuminance:F0} nits\n" +
                         $"Depth: {d.BitsPerColor}-bit | Black: {d.MinLuminance:F3} nits\n" +
                         $"Mode: {(hdr ? "HDR" : "SDR")} ({d.DeviceName})";
            return true;
        }

        return false;
    }

    // Invoked by Pulsar via reflection when the user clicks the plugin's config button.
    [UsedImplicitly]
    private void OpenConfigDialog()
    {
        var sharedUi = GameAccess.GetSharedUI();
        if (sharedUi == null)
        {
            Log.Default.WriteLine(LogSeverity.Warning, $"[{Name}] SharedUIComponent not available");
            return;
        }

        var generator = new SettingsGenerator();
        var viewModel = new SettingsScreenViewModel(
            generator.Title,
            generator.PopulateContent,
            () => ConfigStorage.Save(Config.Current));

        sharedUi.CreateScreen<SettingsScreen>(viewModel, showCursor: true);
    }

    // Invoked by Pulsar via reflection with the local path of the asset named AssetFolder in the XML.
    [UsedImplicitly]
    private void LoadAssets(string assetFolder)
    {
        Rendering.HdrPipeline.SetAssetsFolder(assetFolder);
        Log.Default.WriteLine($"[{Name}] Assets folder: {assetFolder}");
    }
}
