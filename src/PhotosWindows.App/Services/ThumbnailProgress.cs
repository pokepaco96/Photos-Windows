using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using PhotosWindows.App.ViewModels;
using PhotosWindows.Application.Thumbnails;

namespace PhotosWindows.App.Services;

/// <summary>
/// The background thumbnail pass as shown at the bottom of the navigation pane: a bar, how many are done and the time
/// left. Hidden when nothing is pending. Updated by <see cref="BackgroundProcessing"/> on the UI thread.
/// </summary>
public sealed partial class ThumbnailProgress : ObservableObject
{
    [ObservableProperty] public partial bool IsVisible { get; set; }
    [ObservableProperty] public partial double Percent { get; set; }
    [ObservableProperty] public partial string Text { get; set; } = string.Empty;

    public void Apply(ThumbnailProgressState state)
    {
        IsVisible = state.Visible;
        if (!state.Visible) return;
        Percent = state.Fraction * 100;
        var counts = Loc.Format("Thumbs_Progress", state.Done.ToString("N0", CultureInfo.CurrentCulture), state.Total.ToString("N0", CultureInfo.CurrentCulture));
        Text = state.Paused ? counts + " · " + Loc.Get("Thumbs_PausedForImport")
            : state.Remaining is { } left ? counts + " · " + Loc.Format("Backup_Remaining", BackupViewModel.FormatDuration(left))
            : counts;
    }
}
