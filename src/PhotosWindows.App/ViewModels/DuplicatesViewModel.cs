using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media;
using PhotosWindows.App.Services;
using PhotosWindows.Application.Abstractions;
using PhotosWindows.Application.Duplicates;
using PhotosWindows.Application.Thumbnails;
using PhotosWindows.Domain;

namespace PhotosWindows.App.ViewModels;

public sealed partial class DuplicateCandidateViewModel(Asset asset, string path, DuplicateGroupViewModel group) : ObservableObject
{
    public Asset Asset { get; } = asset;
    public DuplicateGroupViewModel Group { get; } = group;
    public string Title => Asset.FileName;
    public string Details => string.Join(" · ",
        Asset.PixelWidth > 0 ? $"{Asset.DisplaySize.Width} × {Asset.DisplaySize.Height}" : "—",
        SelectionActions.FormatBytes(Asset.FileSize),
        Asset.CaptureDate.LocalDateTime.ToString("g", CultureInfo.CurrentCulture));
    public string Path { get; } = path;

    [ObservableProperty] public partial ImageSource? Thumbnail { get; set; }
}

public sealed class DuplicateGroupViewModel
{
    public DuplicateGroupViewModel(DuplicateGroup group, Func<AssetFile?, string> resolve, IReadOnlyDictionary<long, AssetFile?> files)
    {
        Group = group;
        Title = group.Kind == DuplicateKind.Exact ? Loc.Get("Duplicates_Exact") : Loc.Format("Duplicates_Similar", group.MaxDistance);
        foreach (var a in group.Assets) Candidates.Add(new DuplicateCandidateViewModel(a, resolve(files[a.Id]), this));
    }

    public DuplicateGroup Group { get; }
    public string Title { get; }
    public ObservableCollection<DuplicateCandidateViewModel> Candidates { get; } = [];
}

public sealed partial class DuplicatesViewModel(DuplicateService duplicates, IAssetRepository assets, ILibraryContext context, ThumbnailService thumbnails, ImageLoader images, DialogService dialogs) : ObservableObject
{
    public ObservableCollection<DuplicateGroupViewModel> Groups { get; } = [];

    [ObservableProperty] public partial bool IncludeSimilar { get; set; } = true;
    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial bool IsEmpty { get; set; }

    partial void OnIncludeSimilarChanged(bool value) => _ = LoadAsync();

    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var found = await duplicates.FindAsync(IncludeSimilar, DuplicateService.DefaultSimilarityThreshold, CancellationToken.None);
            Groups.Clear();
            foreach (var g in found)
            {
                var files = new Dictionary<long, AssetFile?>();
                foreach (var a in g.Assets) files[a.Id] = await assets.GetOriginalFileAsync(a.Id, CancellationToken.None);
                var vm = new DuplicateGroupViewModel(g, f => f is null || context.Current is null ? string.Empty : context.Current.Resolve(f), files);
                Groups.Add(vm);
                foreach (var c in vm.Candidates) _ = LoadThumbAsync(c);
            }

            IsEmpty = Groups.Count == 0;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LoadThumbAsync(DuplicateCandidateViewModel c)
    {
        var path = await thumbnails.GetOrCreateAsync(c.Asset.Id, ThumbnailSize.Small, CancellationToken.None);
        if (path is not null) c.Thumbnail = await images.FromFileAsync(path, 360, CancellationToken.None);
    }

    [RelayCommand]
    private async Task KeepAsync(DuplicateCandidateViewModel? keep)
    {
        if (keep is null) return;
        var others = keep.Group.Candidates.Count - 1;
        if (!await dialogs.ConfirmAsync(Loc.Get("Duplicates_KeepTitle"), Loc.Format("Duplicates_KeepMessage", keep.Title, others), Loc.Get("Duplicates_Keep"))) return;
        await duplicates.ResolveAsync(keep.Group.Group, keep.Asset.Id, CancellationToken.None);
        Groups.Remove(keep.Group);
        IsEmpty = Groups.Count == 0;
    }

    [RelayCommand]
    private async Task DismissAsync(DuplicateGroupViewModel? group)
    {
        if (group is null) return;
        await duplicates.DismissAsync(group.Group, CancellationToken.None);
        Groups.Remove(group);
        IsEmpty = Groups.Count == 0;
    }
}
