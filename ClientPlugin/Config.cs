using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Xml.Serialization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ClientPlugin.Settings.Elements;
using JetBrains.Annotations;

namespace ClientPlugin;

// Read-only text row for runtime status (display info). Inlined here like SE1 -- it only serves
// Config.DisplayInfo, so it sits next to it instead of polluting the Settings/Elements framework dir.
[AttributeUsage(AttributeTargets.Property)]
internal class LabelAttribute : Attribute, IElement
{
    public Control BuildRow(string name, Func<object> getter, Action<object> setter)
    {
        return new TextBlock
        {
            Text = (string)(getter() ?? ""),
            Foreground = new SolidColorBrush(Color.FromRgb(0xB4, 0xB4, 0xB4)),
            FontSize = 16,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 6)
        };
    }

    public List<Type> SupportedTypes { get; } = [typeof(string)];
}

public class Config : INotifyPropertyChanged
{
    #region User interface

    [XmlIgnore]
    public string Title => Plugin.StatusTitle;

    [Label]
    [XmlIgnore]
    [UsedImplicitly]
    public string DisplayInfo => Plugin.StatusInfo ?? "";

    [Checkbox(description: "Force HDR on non-HDR displays — requires restart (you know what you're doing!)")]
    public bool ForceEnable
    {
        get;
        [UsedImplicitly]
        set => SetField(ref field, value);
    } = false;

    [Slider(400f, 4000f, 100f, description: "Display peak brightness in nits")]
    public float PeakBrightness
    {
        get;
        [UsedImplicitly]
        set => SetField(ref field, value);
    } = 1000f;

    [Slider(80f, 500f, 10f, description: "Game world brightness in nits: where SDR white lands. Highlights go above it up to peak. BT.2408: 203.")]
    public float ScenePaperWhite
    {
        get;
        [UsedImplicitly]
        set => SetField(ref field, value);
    } = 200f;

    // Serialized as <PaperWhite>: that setting only ever drove the UI composite, so
    // existing configs already hold the UI brightness there.
    [XmlElement("PaperWhite")]
    [Slider(40f, 500f, 10f, label: "UI brightness", description: "Menus and HUD in nits. Independent of paper white.")]
    public float UiBrightness
    {
        get;
        [UsedImplicitly]
        set => SetField(ref field, value);
    } = 200f;

    [Slider(4f, 8f, 0.5f, description: "Stops above average scene brightness that keep highlight detail; brighter content sits at peak. Lower it on dim displays: 4 for 400 nits.")]
    public float HighlightRange
    {
        get;
        [UsedImplicitly]
        set => SetField(ref field, value);
    } = 6f;

    [Slider(0f, 0.8f, 0.05f, description: "Midtones up to this share of paper white look as in SDR. 0 = off.")]
    public float VanillaMidtones
    {
        get;
        [UsedImplicitly]
        set => SetField(ref field, value);
    } = 0.7f;

    [Slider(0f, 1f, 0.05f, description: "0 = vanilla: bright colors fade to white. 1 = they keep their color.")]
    public float NaturalColor
    {
        get;
        [UsedImplicitly]
        set => SetField(ref field, value);
    } = 1f;

    [Slider(0f, 0.1f, 0.005f, description: "Shadow lift (raise dark detail)")]
    public float BlackLift
    {
        get;
        [UsedImplicitly]
        set => SetField(ref field, value);
    }

    [Slider(1f, 16f, 0.5f, description: "Emissive particle boost (thrusters, lasers, muzzle flashes, explosions). Diffuse smoke/dust unaffected. 1.0 = original look.")]
    public float ParticleBoost
    {
        get;
        [UsedImplicitly]
        set => SetField(ref field, value);
    } = 1f;

    [Separator("Safety")]

    [Checkbox(description: "Auto-set after a crash to disable HDR on next launch. Uncheck to re-enable.")]
    public bool DisabledAfterCrash
    {
        get;
        set => SetField(ref field, value);
    }

    #endregion

    #region Property change notification boilerplate

    public static readonly Config Default = new ();
    public static readonly Config Current = ConfigStorage.Load();

    public event PropertyChangedEventHandler PropertyChanged;

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(propertyName);
    }

    #endregion
}
