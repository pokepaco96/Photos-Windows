using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media;
using PhotosWindows.App.Services;
using PhotosWindows.Application.Abstractions;
using PhotosWindows.Application.Library;
using PhotosWindows.Application.Memories;
using PhotosWindows.Application.Thumbnails;
using PhotosWindows.Domain;

namespace PhotosWindows.App.ViewModels;

/// <summary>One memory card: title, dates, the explanation of why it is shown, and its cover.</summary>
public sealed partial class MemoryCardViewModel(MemoryCandidate memory, MemoryDescriber describer) : ObservableObject
{
    public MemoryCandidate Memory { get; } = memory;
    public string Key => Memory.Key;
    public string Title { get; } = describer.Title(memory);
    public string Subtitle { get; } = describer.Subtitle(memory);
    public string Why { get; } = describer.Why(memory);
    public string KindName { get; } = describer.KindName(memory.Kind);
    public string AutomationName => $"{Title}, {Subtitle}";

    [ObservableProperty] public partial ImageSource? Cover { get; set; }
}

/// <summary>A row in Options: a kind or range the user excluded, or a memory they hid. Removing it restores it.</summary>
public sealed record MemoryDecisionViewModel(string Text, long? ExclusionId, string? HiddenKey);

public sealed record MemoryKindOption(MemoryKind Kind, string Name)
{
    public override string ToString() => Name;
}

public sealed partial class MemoriesViewModel(
    MemoriesService memories,
    IAssetRepository assets,
    ThumbnailService thumbnails,
    ImageLoader images,
    ISettingsStore settings,
    NavigationService navigation,
    DialogService dialogs) : ObservableObject
{
    private readonly MemoryDescriber _describer = new(Loc.Get, CultureInfo.CurrentCulture);
    private string? _lastHiddenKey;
    private int _loadVersion;
    private bool _initializing;

    public ObservableCollection<MemoryCardViewModel> Cards { get; } = [];
    public ObservableCollection<MemoryDecisionViewModel> Decisions { get; } = [];
    public IReadOnlyList<MemoryKindOption> Kinds { get; private set; } = [];

    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial bool IsEmpty { get; set; }
    [ObservableProperty] public partial string EmptyTitle { get; set; } = string.Empty;
    [ObservableProperty] public partial string EmptyMessage { get; set; } = string.Empty;
    [ObservableProperty] public partial string Summary { get; set; } = string.Empty;
    [ObservableProperty] public partial bool HasError { get; set; }
    [ObservableProperty] public partial string ErrorMessage { get; set; } = string.Empty;
    [ObservableProperty] public partial bool CanUndo { get; set; }
    [ObservableProperty] public partial bool ShowOptions { get; set; }
    [ObservableProperty] public partial bool HasDecisions { get; set; }
    [ObservableProperty] public partial bool NoDecisions { get; set; } = true;
    [ObservableProperty] public partial bool PreferFavorites { get; set; } = true;
    [ObservableProperty] public partial MemoryKindOption? SelectedKind { get; set; }
    [ObservableProperty] public partial DateTimeOffset? RangeFrom { get; set; }
    [ObservableProperty] public partial DateTimeOffset? RangeTo { get; set; }
    [ObservableProperty] public partial string Notice { get; set; } = string.Empty;
    [ObservableProperty] public partial bool HasNotice { get; set; }

    partial void OnPreferFavoritesChanged(bool value)
    {
        if (_initializing) return;
        settings.Save(settings.Load() with { MemoriesPreferFavorites = value });
        _ = LoadAsync(false);
    }

    /// <summary>Loads the memories. <paramref name="regenerate"/> recomputes them instead of using today's cached result.</summary>
    public async Task LoadAsync(bool regenerate)
    {
        var version = ++_loadVersion;
        if (Kinds.Count == 0)
        {
            _initializing = true;
            Kinds = Enum.GetValues<MemoryKind>().Select(k => new MemoryKindOption(k, _describer.KindName(k))).ToList();
            OnPropertyChanged(nameof(Kinds));
            SelectedKind = Kinds[0];
            PreferFavorites = settings.Load().MemoriesPreferFavorites;
            _initializing = false;
        }

        IsLoading = true;
        HasError = false;
        try
        {
            var result = await memories.GetAsync(regenerate, CancellationToken.None);
            if (version != _loadVersion) return;
            Cards.Clear();
            foreach (var m in result.Memories) Cards.Add(new MemoryCardViewModel(m, _describer));
            foreach (var card in Cards) _ = LoadCoverAsync(card);
            IsEmpty = Cards.Count == 0;
            Summary = IsEmpty ? string.Empty : Loc.Format(result.PeopleUsed ? "Memories_SummaryPeople" : "Memories_Summary", Cards.Count, result.ItemsConsidered);
            if (IsEmpty)
            {
                // Photos exist but none can be opened: say so instead of the generic "nothing yet".
                var disconnected = result.ItemsConsidered == 0 && await assets.CountAsync(new AssetQuery(), CancellationToken.None) > 0;
                (EmptyTitle, EmptyMessage) = disconnected
                    ? (Loc.Get("Memories_DisconnectedTitle"), Loc.Get("Memories_DisconnectedMessage"))
                    : (Loc.Get("Memories_EmptyTitle"), Loc.Get("Memories_EmptyMessage"));
            }

            await LoadDecisionsAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Data.Common.DbException)
        {
            if (version != _loadVersion) return;
            Cards.Clear();
            IsEmpty = false;
            HasError = true;
            ErrorMessage = ex.Message;
        }
        finally
        {
            if (version == _loadVersion) IsLoading = false;
        }
    }

    private async Task LoadDecisionsAsync()
    {
        Decisions.Clear();
        foreach (var e in await memories.GetExclusionsAsync(CancellationToken.None))
        {
            var text = e.Kind is { } kind
                ? Loc.Format("Memories_Decision_Kind", _describer.KindName(kind))
                : Loc.Format("Memories_Decision_Range", _describer.DateRange(e.From!.Value, e.To!.Value));
            Decisions.Add(new MemoryDecisionViewModel(text, e.Id, null));
        }

        foreach (var h in await memories.GetHiddenAsync(CancellationToken.None))
        {
            Decisions.Add(new MemoryDecisionViewModel(Loc.Format("Memories_Decision_Hidden", _describer.KindName(h.Kind), _describer.DateRange(h.Start, h.End)), null, h.Key));
        }

        HasDecisions = Decisions.Count > 0;
        NoDecisions = !HasDecisions;
    }

    private async Task LoadCoverAsync(MemoryCardViewModel card)
    {
        var path = await thumbnails.GetOrCreateAsync(card.Memory.CoverAssetId, ThumbnailSize.Large, CancellationToken.None);
        if (path is not null) card.Cover = await images.FromFileAsync(path, 720, CancellationToken.None);
    }

    [RelayCommand]
    private Task RegenerateAsync() => LoadAsync(true);

    [RelayCommand]
    private void ToggleOptions() => ShowOptions = !ShowOptions;

    public void Open(MemoryCardViewModel card) =>
        navigation.ToLibrary(new LibraryNavigation(LibraryScope.All, Ids: card.Memory.AssetIds, Title: card.Title));

    public void Slideshow(MemoryCardViewModel card) => navigation.ToSlideshow(new SlideshowNavigation(card.Memory.AssetIds, card.Title));

    public async Task CreateAlbumAsync(MemoryCardViewModel card)
    {
        var name = await dialogs.PromptAsync(Loc.Get("Memories_AlbumTitle"), Loc.Get("Memories_AlbumPlaceholder"), card.Title);
        if (name is null) return;
        await memories.CreateAlbumAsync(card.Memory, name, CancellationToken.None);
        Notice = Loc.Format("Memories_AlbumCreated", name, card.Memory.AssetIds.Count);
        HasNotice = true;
        // Creating an album is a library change: the cache was dropped, reload keeps the page consistent.
        await LoadAsync(false);
    }

    public async Task HideAsync(MemoryCardViewModel card)
    {
        await memories.HideAsync(card.Memory, CancellationToken.None);
        _lastHiddenKey = card.Key;
        CanUndo = true;
        await LoadAsync(false);
    }

    [RelayCommand]
    private async Task UndoHideAsync()
    {
        if (_lastHiddenKey is null) return;
        await memories.UnhideAsync(_lastHiddenKey, CancellationToken.None);
        _lastHiddenKey = null;
        CanUndo = false;
        await LoadAsync(false);
    }

    [RelayCommand]
    private async Task ExcludeKindAsync()
    {
        if (SelectedKind is null) return;
        await memories.ExcludeKindAsync(SelectedKind.Kind, CancellationToken.None);
        await LoadAsync(false);
    }

    [RelayCommand]
    private async Task ExcludeRangeAsync()
    {
        if (RangeFrom is not { } from || RangeTo is not { } to) return;
        await memories.ExcludeRangeAsync(from.Date, to.Date, CancellationToken.None);
        RangeFrom = null;
        RangeTo = null;
        await LoadAsync(false);
    }

    public async Task RestoreAsync(MemoryDecisionViewModel decision)
    {
        if (decision.ExclusionId is { } id) await memories.RemoveExclusionAsync(id, CancellationToken.None);
        if (decision.HiddenKey is { } key)
        {
            await memories.UnhideAsync(key, CancellationToken.None);
            if (key == _lastHiddenKey) CanUndo = false;
        }

        await LoadAsync(false);
    }
}

/// <summary>Slideshow of a list of assets: play/pause, previous/next (wrapping) and exit, by buttons or keyboard.</summary>
public sealed partial class SlideshowViewModel(ThumbnailService thumbnails, ImageLoader images, ISettingsStore settings, NavigationService navigation) : ObservableObject
{
    private IReadOnlyList<long> _ids = [];
    private SlideshowState _state = new(0);
    private int _loadVersion;

    [ObservableProperty] public partial ImageSource? Image { get; set; }
    [ObservableProperty] public partial string Title { get; set; } = string.Empty;
    [ObservableProperty] public partial string PositionText { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsPlaying { get; set; }
    [ObservableProperty] public partial string PlayPauseLabel { get; set; } = string.Empty;
    [ObservableProperty] public partial string PlayPauseGlyph { get; set; } = "";

    public TimeSpan Interval => TimeSpan.FromSeconds(Math.Clamp(settings.Load().SlideshowSeconds, 1, 60));

    public async Task StartAsync(SlideshowNavigation target)
    {
        _ids = target.Ids;
        Title = target.Title;
        _state = new SlideshowState(_ids.Count);
        await ShowAsync();
    }

    public async Task TickAsync()
    {
        if (_state.Tick()) await ShowAsync();
    }

    [RelayCommand]
    private void TogglePlay()
    {
        _state.Toggle();
        Sync();
    }

    [RelayCommand]
    private async Task NextAsync()
    {
        _state.Next();
        await ShowAsync();
    }

    [RelayCommand]
    private async Task PreviousAsync()
    {
        _state.Previous();
        await ShowAsync();
    }

    [RelayCommand]
    private void Exit()
    {
        _state.Pause();
        Sync();
        navigation.GoBack();
    }

    private void Sync()
    {
        IsPlaying = _state.IsPlaying;
        PlayPauseLabel = Loc.Get(IsPlaying ? "Slideshow_Pause" : "Slideshow_Play");
        PlayPauseGlyph = IsPlaying ? "" : "";
        var (position, total) = _state.Position;
        PositionText = Loc.Format("Slideshow_Position", position, total);
    }

    private async Task ShowAsync()
    {
        Sync();
        if (_ids.Count == 0) return;
        var version = ++_loadVersion;
        var path = await thumbnails.GetOrCreateAsync(_ids[_state.Index], ThumbnailSize.Large, CancellationToken.None);
        if (version != _loadVersion) return;
        // A file that can no longer be decoded keeps the previous picture; the position still advances.
        if (path is not null && await images.FromFileAsync(path, 0, CancellationToken.None) is { } bitmap && version == _loadVersion) Image = bitmap;
    }
}
