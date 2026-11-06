using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media;
using PhotosWindows.App.Services;
using PhotosWindows.Application.Abstractions;
using PhotosWindows.Application.Thumbnails;
using PhotosWindows.Domain;

namespace PhotosWindows.App.ViewModels;

/// <summary>One grid tile. The bitmap is only loaded while the tile is realised by the virtualising panel.</summary>
public sealed partial class AssetItemViewModel(AssetSummary summary) : ObservableObject
{
    private CancellationTokenSource? _loading;

    public AssetSummary Summary { get; private set; } = summary;

    public long Id => Summary.Id;

    [ObservableProperty] public partial ImageSource? Thumbnail { get; set; }
    [ObservableProperty] public partial bool IsFavorite { get; set; } = summary.IsFavorite;
    [ObservableProperty] public partial bool ThumbnailFailed { get; set; }

    public bool IsVideo => Summary.MediaType == MediaType.Video;
    public bool IsEdited => Summary.HasEdits;
    public bool IsLive => Summary.HasLiveVideo;
    public bool IsRaw => Summary.HasRawPair;
    public bool IsUnavailable => !Summary.IsAvailable;
    public bool HasBadges => IsVideo || IsEdited || IsLive || IsRaw || IsFavorite || IsUnavailable;

    public string DurationText => Summary.Duration is { } d
        ? (d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss", CultureInfo.CurrentCulture) : d.ToString(@"m\:ss", CultureInfo.CurrentCulture))
        : string.Empty;

    /// <summary>Text read by screen readers for the tile.</summary>
    public string AutomationName
    {
        get
        {
            var parts = new List<string> { IsVideo ? Loc.Get("Tile_Video") : Loc.Get("Tile_Photo"), Summary.CaptureLocal.ToString("f", CultureInfo.CurrentCulture), Summary.FileName };
            if (IsFavorite) parts.Add(Loc.Get("Tile_Favorite"));
            if (IsEdited) parts.Add(Loc.Get("Tile_Edited"));
            if (IsUnavailable) parts.Add(Loc.Get("Tile_Unavailable"));
            return string.Join(", ", parts);
        }
    }

    public async Task LoadThumbnailAsync(ThumbnailService thumbnails, ImageLoader images, int decodeWidth)
    {
        if (Thumbnail is not null) return;
        _loading?.Cancel();
        var cts = _loading = new CancellationTokenSource();
        try
        {
            var path = thumbnails.TryGetCached(Summary, ThumbnailSize.Small)
                ?? await thumbnails.GetOrCreateAsync(Summary.Id, ThumbnailSize.Small, cts.Token);
            if (path is null)
            {
                ThumbnailFailed = true;
                return;
            }

            var bitmap = await images.FromFileAsync(path, decodeWidth, cts.Token);
            if (!cts.IsCancellationRequested && bitmap is not null) Thumbnail = bitmap;
        }
        catch (OperationCanceledException)
        {
            // Tile was recycled while loading.
        }
    }

    public void Unload()
    {
        _loading?.Cancel();
        _loading = null;
        Thumbnail = null;
    }

    public void Reset()
    {
        Unload();
        ThumbnailFailed = false;
    }
}

public sealed class AssetGroup(string title, IEnumerable<AssetItemViewModel> items) : ObservableCollection<AssetItemViewModel>(items)
{
    public string Title { get; } = title;

    public string CountText => Loc.Format("Group_Count", Count);
}

public enum GroupingMode
{
    Days = 0,
    Months = 1,
    Years = 2,
    None = 3,
}
