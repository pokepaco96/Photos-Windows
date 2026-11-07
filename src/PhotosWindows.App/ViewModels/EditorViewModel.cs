using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media;
using PhotosWindows.App.Services;
using PhotosWindows.Application.Abstractions;
using PhotosWindows.Application.Editing;
using PhotosWindows.Domain;

namespace PhotosWindows.App.ViewModels;

public sealed record PresetItem(string Name, string DisplayName)
{
    public string AutomationId => "Preset_" + Name;
}

public sealed record AspectItem(string Key, string DisplayName, double? Ratio);

/// <summary>
/// Non-destructive editor. The original is decoded once at preview resolution; every change re-runs only the
/// recipe on that buffer. Saving stores the recipe in the catalog; the original file is never written.
/// </summary>
public sealed partial class EditorViewModel(EditingService editing, IAssetRepository assets, NavigationService navigation, DialogService dialogs, UiThread ui) : ObservableObject
{
    private const int PreviewSize = 1600;
    private EditSession? _session;
    private RasterImage? _source;
    private CancellationTokenSource? _renderCts;
    private bool _syncing;

    public IReadOnlyList<PresetItem> Presets { get; } = EditPresets.All.Select(p => new PresetItem(p.Name, Loc.Get("Preset_" + p.Name))).ToList();

    public IReadOnlyList<AspectItem> Aspects { get; } =
    [
        new("free", Loc.Get("Aspect_Free"), null),
        new("original", Loc.Get("Aspect_Original"), null),
        new("1:1", "1:1", 1),
        new("4:3", "4:3", 4.0 / 3),
        new("3:2", "3:2", 3.0 / 2),
        new("16:9", "16:9", 16.0 / 9),
        new("4:5", "4:5", 4.0 / 5),
    ];

    [ObservableProperty] public partial ImageSource? Preview { get; set; }
    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial bool IsComparing { get; set; }
    [ObservableProperty] public partial bool IsCropMode { get; set; }
    [ObservableProperty] public partial bool CanUndo { get; set; }
    [ObservableProperty] public partial bool CanRedo { get; set; }
    [ObservableProperty] public partial bool IsDirty { get; set; }
    [ObservableProperty] public partial string FileName { get; set; } = string.Empty;
    [ObservableProperty] public partial string ErrorMessage { get; set; } = string.Empty;
    [ObservableProperty] public partial bool HasError { get; set; }
    [ObservableProperty] public partial int PreviewPixelWidth { get; set; }
    [ObservableProperty] public partial int PreviewPixelHeight { get; set; }

    // Slider values (UI units: -100..100 or 0..100, exposure -3..3)
    [ObservableProperty] public partial double Exposure { get; set; }
    [ObservableProperty] public partial double Brightness { get; set; }
    [ObservableProperty] public partial double Contrast { get; set; }
    [ObservableProperty] public partial double Highlights { get; set; }
    [ObservableProperty] public partial double Shadows { get; set; }
    [ObservableProperty] public partial double Saturation { get; set; }
    [ObservableProperty] public partial double Vibrance { get; set; }
    [ObservableProperty] public partial double Temperature { get; set; }
    [ObservableProperty] public partial double Tint { get; set; }
    [ObservableProperty] public partial double BlackAndWhite { get; set; }
    [ObservableProperty] public partial double Vignette { get; set; }
    [ObservableProperty] public partial double Sharpness { get; set; }
    [ObservableProperty] public partial double Straighten { get; set; }
    [ObservableProperty] public partial AspectItem? SelectedAspect { get; set; }

    /// <summary>Untyped mirror of <see cref="SelectedAspect"/> for the ComboBox two-way binding.</summary>
    public object? SelectedAspectItem
    {
        get => SelectedAspect;
        set => SelectedAspect = value as AspectItem;
    }

    public long AssetId => _session?.AssetId ?? 0;

    public EditRecipe Current => _session?.Current ?? EditRecipe.Identity;

    /// <summary>Raised when the crop rectangle changes so the overlay can redraw.</summary>
    public event EventHandler? CropChanged;

    public async Task LoadAsync(long assetId)
    {
        IsLoading = true;
        try
        {
            var asset = await assets.GetAsync(assetId, CancellationToken.None);
            FileName = asset?.FileName ?? string.Empty;
            _session = await editing.StartSessionAsync(assetId, CancellationToken.None);
            _source = await editing.LoadPreviewSourceAsync(assetId, PreviewSize, CancellationToken.None);
            if (_source is null)
            {
                HasError = true;
                ErrorMessage = Loc.Get("Viewer_Unavailable");
                return;
            }

            SyncFromRecipe();
            await RenderAsync();
        }
        catch (MediaDecodeException ex)
        {
            HasError = true;
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void SyncFromRecipe()
    {
        var r = Current;
        _syncing = true;
        Exposure = r.Exposure;
        Brightness = r.Brightness * 100;
        Contrast = r.Contrast * 100;
        Highlights = r.Highlights * 100;
        Shadows = r.Shadows * 100;
        Saturation = r.Saturation * 100;
        Vibrance = r.Vibrance * 100;
        Temperature = r.Temperature * 100;
        Tint = r.Tint * 100;
        BlackAndWhite = r.BlackAndWhite * 100;
        Vignette = r.Vignette * 100;
        Sharpness = r.Sharpness * 100;
        Straighten = r.StraightenDegrees;
        SelectedAspect = Aspects.FirstOrDefault(a => a.Key == r.AspectRatio) ?? Aspects[0];
        _syncing = false;
        CanUndo = _session?.CanUndo == true;
        CanRedo = _session?.CanRedo == true;
        IsDirty = _session?.IsDirty == true;
        CropChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Apply(EditRecipe next, string? gesture)
    {
        if (_session is null || _syncing) return;
        _session.Apply(next, gesture);
        CanUndo = _session.CanUndo;
        CanRedo = _session.CanRedo;
        IsDirty = _session.IsDirty;
        _ = RenderAsync();
    }

    partial void OnExposureChanged(double value) => Apply(Current with { Exposure = value }, nameof(Exposure));
    partial void OnBrightnessChanged(double value) => Apply(Current with { Brightness = value / 100 }, nameof(Brightness));
    partial void OnContrastChanged(double value) => Apply(Current with { Contrast = value / 100 }, nameof(Contrast));
    partial void OnHighlightsChanged(double value) => Apply(Current with { Highlights = value / 100 }, nameof(Highlights));
    partial void OnShadowsChanged(double value) => Apply(Current with { Shadows = value / 100 }, nameof(Shadows));
    partial void OnSaturationChanged(double value) => Apply(Current with { Saturation = value / 100 }, nameof(Saturation));
    partial void OnVibranceChanged(double value) => Apply(Current with { Vibrance = value / 100 }, nameof(Vibrance));
    partial void OnTemperatureChanged(double value) => Apply(Current with { Temperature = value / 100 }, nameof(Temperature));
    partial void OnTintChanged(double value) => Apply(Current with { Tint = value / 100 }, nameof(Tint));
    partial void OnBlackAndWhiteChanged(double value) => Apply(Current with { BlackAndWhite = value / 100 }, nameof(BlackAndWhite));
    partial void OnVignetteChanged(double value) => Apply(Current with { Vignette = value / 100 }, nameof(Vignette));
    partial void OnSharpnessChanged(double value) => Apply(Current with { Sharpness = value / 100 }, nameof(Sharpness));
    partial void OnStraightenChanged(double value) => Apply(Current with { StraightenDegrees = value }, nameof(Straighten));

    partial void OnSelectedAspectChanged(AspectItem? value)
    {
        OnPropertyChanged(nameof(SelectedAspectItem));
        if (value is null || _syncing || _source is null) return;
        var crop = Current.Crop;
        var ratio = value.Key == "original" ? OrientedAspect(Current, keepOriginal: true) : value.Ratio;
        if (ratio is { } target)
        {
            crop = FitAspect(crop, target, OrientedAspect(Current, keepOriginal: false) ?? 1);
        }

        Apply(Current with { AspectRatio = value.Key, Crop = crop }, null);
        CropChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Called by the crop overlay while dragging (normalised rectangle).</summary>
    public void SetCrop(NormalizedRect crop, bool endOfGesture)
    {
        Apply(Current with { Crop = crop.Clamp() }, "Crop");
        if (endOfGesture) _session?.EndGesture();
    }

    public void EndGesture() => _session?.EndGesture();

    /// <summary>Aspect (w/h) of the oriented image; with keepOriginal the source aspect regardless of quarter turns.</summary>
    public double? OrientedAspect(EditRecipe recipe, bool keepOriginal)
    {
        if (_source is null) return null;
        double w = _source.Width, h = _source.Height;
        if (!keepOriginal && recipe.QuarterTurns % 2 == 1) (w, h) = (h, w);
        return w / h;
    }

    /// <summary>Largest centred rectangle with the requested pixel aspect inside the current crop.</summary>
    public static NormalizedRect FitAspect(NormalizedRect crop, double targetAspect, double imageAspect)
    {
        // Convert to pixel-proportional units where the image is imageAspect × 1.
        var w = crop.Width * imageAspect;
        var h = crop.Height;
        if (w / h > targetAspect) w = h * targetAspect;
        else h = w / targetAspect;
        var nw = w / imageAspect;
        var cx = crop.X + crop.Width / 2;
        var cy = crop.Y + crop.Height / 2;
        return new NormalizedRect(cx - nw / 2, cy - h / 2, nw, h).Clamp();
    }

    private async Task RenderAsync()
    {
        if (_source is null) return;
        _renderCts?.Cancel();
        var cts = _renderCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(25, cts.Token); // coalesce rapid slider events
            var recipe = IsComparing ? EditRecipe.Identity : Current;
            if (IsCropMode && !IsComparing) recipe = recipe with { Crop = NormalizedRect.Full }; // show the whole frame while cropping
            var rendered = await editing.RenderPreviewAsync(_source, recipe, PreviewSize, cts.Token);
            if (cts.IsCancellationRequested) return;
            await ui.RunAsync(async () =>
            {
                Preview = await ImageLoader.FromBytesAsync(rendered.Encoded, 0);
                PreviewPixelWidth = rendered.Width;
                PreviewPixelHeight = rendered.Height;
                CropChanged?.Invoke(this, EventArgs.Empty);
            });
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer render
        }
    }

    public void SetComparing(bool comparing)
    {
        if (IsComparing == comparing) return;
        IsComparing = comparing;
        _ = RenderAsync();
    }

    [RelayCommand]
    private void ToggleCrop()
    {
        IsCropMode = !IsCropMode;
        _ = RenderAsync();
    }

    [RelayCommand]
    private void ApplyPreset(PresetItem? preset)
    {
        var p = EditPresets.All.FirstOrDefault(x => x.Name == preset?.Name);
        if (p.Apply is null) return;
        Apply(p.Apply(Current), null);
        SyncFromRecipe();
    }

    [RelayCommand] private void RotateLeft() { Apply(Current.RotateCounterClockwise(), null); SyncFromRecipe(); }
    [RelayCommand] private void RotateRight() { Apply(Current.RotateClockwise(), null); SyncFromRecipe(); }
    [RelayCommand] private void FlipHorizontal() { Apply(Current.ToggleFlipHorizontal(), null); SyncFromRecipe(); }
    [RelayCommand] private void FlipVertical() { Apply(Current.ToggleFlipVertical(), null); SyncFromRecipe(); }

    [RelayCommand]
    private void ResetAdjustment(string? name)
    {
        if (string.IsNullOrEmpty(name)) return;
        Apply(Current.Reset(name), null);
        SyncFromRecipe();
    }

    [RelayCommand]
    private void ResetAll()
    {
        Apply(EditRecipe.Identity, null);
        SyncFromRecipe();
    }

    [RelayCommand]
    private void Undo()
    {
        if (_session?.Undo() != true) return;
        SyncFromRecipe();
        _ = RenderAsync();
    }

    [RelayCommand]
    private void Redo()
    {
        if (_session?.Redo() != true) return;
        SyncFromRecipe();
        _ = RenderAsync();
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (_session is null) return;
        await editing.SaveAsync(_session.AssetId, _session.Current, CancellationToken.None);
        navigation.GoBack();
    }

    [RelayCommand]
    private async Task CancelAsync()
    {
        if (_session?.IsDirty == true &&
            !await dialogs.ConfirmAsync(Loc.Get("Editor_DiscardTitle"), Loc.Get("Editor_DiscardMessage"), Loc.Get("Editor_Discard"), destructive: true))
        {
            return;
        }

        navigation.GoBack();
    }

    [RelayCommand]
    private async Task RevertToOriginalAsync()
    {
        if (_session is null) return;
        if (!await dialogs.ConfirmAsync(Loc.Get("Editor_RevertTitle"), Loc.Get("Editor_RevertMessage"), Loc.Get("Editor_Revert"))) return;
        await editing.RevertToOriginalAsync(_session.AssetId, CancellationToken.None);
        navigation.GoBack();
    }
}
