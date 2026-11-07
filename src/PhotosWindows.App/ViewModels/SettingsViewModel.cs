using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;
using PhotosWindows.App.Services;
using PhotosWindows.Application.Abstractions;
using PhotosWindows.Application.Backup;
using PhotosWindows.Application.Library;
using PhotosWindows.Application.Maintenance;

namespace PhotosWindows.App.ViewModels;

public sealed partial class SettingsViewModel(
    LibraryManager libraries,
    ILibraryContext context,
    BackupService backup,
    LibraryMaintenanceService maintenance,
    ThemeService theme,
    ActivityCenter activity,
    DialogService dialogs,
    WindowContext window,
    NavigationService navigation,
    IAppPaths paths) : ObservableObject
{
    private VerificationReport? _lastReport;

    public ObservableCollection<InfoRow> Storage { get; } = [];

    [ObservableProperty] public partial int ThemeIndex { get; set; }
    [ObservableProperty] public partial string LibraryName { get; set; } = string.Empty;
    [ObservableProperty] public partial string LibraryPath { get; set; } = string.Empty;
    [ObservableProperty] public partial string CachePath { get; set; } = string.Empty;
    [ObservableProperty] public partial string VerificationSummary { get; set; } = string.Empty;
    [ObservableProperty] public partial bool HasMissing { get; set; }
    [ObservableProperty] public partial bool HasOrphans { get; set; }
    [ObservableProperty] public partial bool HasPartials { get; set; }
    [ObservableProperty] public partial bool? IncludeOriginalsInBackup { get; set; } = true;
    [ObservableProperty] public partial bool? VerifyHashes { get; set; } = false;

    public string Version => typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    partial void OnThemeIndexChanged(int value) => theme.Set((AppTheme)value);

    public async Task LoadAsync()
    {
        ThemeIndex = (int)theme.Current;
        if (context.Current is not { } library) return;
        LibraryName = library.Name;
        LibraryPath = library.RootPath;
        CachePath = library.CachePath;
        var report = await libraries.GetStorageReportAsync(CancellationToken.None);
        Storage.Clear();
        var c = report.Catalog;
        Storage.Add(new InfoRow(Loc.Get("Settings_Photos"), c.PhotoCount.ToString("N0", CultureInfo.CurrentCulture)));
        Storage.Add(new InfoRow(Loc.Get("Settings_Videos"), c.VideoCount.ToString("N0", CultureInfo.CurrentCulture)));
        Storage.Add(new InfoRow(Loc.Get("Settings_Albums"), c.AlbumCount.ToString("N0", CultureInfo.CurrentCulture)));
        Storage.Add(new InfoRow(Loc.Get("Settings_ManagedSize"), SelectionActions.FormatBytes(c.ManagedBytes)));
        Storage.Add(new InfoRow(Loc.Get("Settings_ReferencedSize"), SelectionActions.FormatBytes(c.ReferencedBytes)));
        Storage.Add(new InfoRow(Loc.Get("Settings_CatalogSize"), SelectionActions.FormatBytes(report.DatabaseBytes)));
        Storage.Add(new InfoRow(Loc.Get("Settings_CacheSize"), SelectionActions.FormatBytes(report.CacheBytes)));
        if (c.UnavailableFiles > 0) Storage.Add(new InfoRow(Loc.Get("Settings_Unavailable"), c.UnavailableFiles.ToString(CultureInfo.CurrentCulture)));
        if (c.PendingThumbnails > 0) Storage.Add(new InfoRow(Loc.Get("Settings_PendingThumbs"), c.PendingThumbnails.ToString(CultureInfo.CurrentCulture)));
    }

    [RelayCommand]
    private void SwitchLibrary()
    {
        libraries.Close();
        navigation.ToWelcome();
    }

    [RelayCommand]
    private Task OpenLibraryFolderAsync() => LaunchFolderAsync(LibraryPath);

    [RelayCommand]
    private Task OpenLogsFolderAsync() => LaunchFolderAsync(paths.LogsPath);

    private static async Task LaunchFolderAsync(string path)
    {
        if (Directory.Exists(path)) await Windows.System.Launcher.LaunchFolderPathAsync(path);
    }

    [RelayCommand]
    private async Task ChangeCacheAsync()
    {
        var folder = await window.PickFolderAsync();
        if (folder is null) return;
        await libraries.SetCacheLocationAsync(folder, CancellationToken.None);
        await LoadAsync();
    }

    [RelayCommand]
    private async Task ResetCacheLocationAsync()
    {
        await libraries.SetCacheLocationAsync(null, CancellationToken.None);
        await LoadAsync();
    }

    [RelayCommand]
    private async Task ClearCacheAsync()
    {
        if (!await dialogs.ConfirmAsync(Loc.Get("Settings_ClearCache"), Loc.Get("Settings_ClearCacheMessage"), Loc.Get("Settings_ClearCache"))) return;
        await libraries.ClearCacheAsync(CancellationToken.None);
        await LoadAsync();
    }

    [RelayCommand]
    private async Task CreateBackupAsync()
    {
        var folder = await window.PickFolderAsync();
        if (folder is null) return;
        var includeOriginals = IncludeOriginalsInBackup == true;
        await activity.RunAsync(Loc.Get("Backup_Activity"), async (progress, ct) =>
        {
            var p = new InlineProgress<BackupProgress>(b => progress.Report(new ActivityProgress(b.Total > 0 && b.Stage == "originals" ? (double)b.Done / b.Total : null, Loc.Get("BackupStage_" + b.Stage))));
            var path = await backup.CreateBackupAsync(folder, includeOriginals, p, ct);
            return new ActivityOutcome(Loc.Format("Backup_Done", path), InfoBarSeverity.Success, Loc.Get("Export_OpenFolder"), () => _ = LaunchFolderAsync(path));
        });
    }

    [RelayCommand]
    private async Task VerifyBackupAsync()
    {
        var folder = await window.PickFolderAsync();
        if (folder is null) return;
        var result = await backup.VerifyAsync(folder, CancellationToken.None);
        await dialogs.ShowMessageAsync(Loc.Get("Backup_VerifyTitle"), result.IsValid
            ? Loc.Format("Backup_Valid", result.Manifest!.CreatedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture), result.Manifest.AssetCount, result.Manifest.Files.Count)
            : Loc.Get("Backup_Invalid") + Environment.NewLine + string.Join(Environment.NewLine, result.Problems.Take(8)));
    }

    [RelayCommand]
    private async Task RestoreAsNewAsync()
    {
        await dialogs.ShowMessageAsync(Loc.Get("Restore_Title"), Loc.Get("Restore_PickBackup"));
        var source = await window.PickFolderAsync();
        if (source is null) return;
        await dialogs.ShowMessageAsync(Loc.Get("Restore_Title"), Loc.Get("Restore_PickDestination"));
        var destination = await window.PickFolderAsync();
        if (destination is null) return;
        var ok = await activity.RunAsync(Loc.Get("Restore_Title"), async (_, ct) =>
        {
            var info = await backup.RestoreAsNewLibraryAsync(source, destination, ct);
            return new ActivityOutcome(Loc.Format("Restore_Done", info.RootPath), InfoBarSeverity.Success);
        });
        if (ok)
        {
            navigation.ToLibrary(LibraryNavigation.AllPhotos);
            navigation.ClearHistory();
        }
    }

    [RelayCommand]
    private async Task RestoreCatalogAsync()
    {
        if (!await dialogs.ConfirmAsync(Loc.Get("Restore_CatalogTitle"), Loc.Get("Restore_CatalogMessage"), Loc.Get("Restore_Catalog"))) return;
        var source = await window.PickFolderAsync();
        if (source is null) return;
        try
        {
            await backup.RestoreCatalogInPlaceAsync(source, CancellationToken.None);
            navigation.ToLibrary(LibraryNavigation.AllPhotos);
            navigation.ClearHistory();
        }
        catch (InvalidOperationException ex)
        {
            await dialogs.ShowMessageAsync(Loc.Get("Restore_CatalogTitle"), ex.Message);
        }
    }

    [RelayCommand]
    private async Task VerifyLibraryAsync()
    {
        var hashes = VerifyHashes == true;
        await activity.RunAsync(Loc.Get("Verify_Activity"), async (progress, ct) =>
        {
            var p = new InlineProgress<VerificationProgress>(v => progress.Report(new ActivityProgress((double)v.Done / Math.Max(1, v.Total), Loc.Format("Verify_Progress", v.Done, v.Total))));
            var report = await maintenance.VerifyAsync(hashes, p, ct);
            _lastReport = report;
            var summary = report.IsHealthy
                ? Loc.Format("Verify_Healthy", report.CheckedFiles)
                : Loc.Format("Verify_Problems", report.CheckedFiles, report.MissingFiles.Count, report.ChangedFiles.Count, report.OrphanManagedFiles.Count, report.PartialFiles.Count, report.CatalogProblems.Count);
            return new ActivityOutcome(summary, report.IsHealthy ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        });
        if (_lastReport is { } r)
        {
            VerificationSummary = r.IsHealthy
                ? Loc.Format("Verify_Healthy", r.CheckedFiles)
                : Loc.Format("Verify_Problems", r.CheckedFiles, r.MissingFiles.Count, r.ChangedFiles.Count, r.OrphanManagedFiles.Count, r.PartialFiles.Count, r.CatalogProblems.Count);
            HasMissing = r.MissingFiles.Count > 0;
            HasOrphans = r.OrphanManagedFiles.Count > 0;
            HasPartials = r.PartialFiles.Count > 0;
        }
    }

    [RelayCommand]
    private async Task RelinkAsync()
    {
        if (_lastReport is null) return;
        var folder = await window.PickFolderAsync();
        if (folder is null) return;
        var relinked = await maintenance.RelinkMissingAsync(_lastReport.MissingFiles, folder, CancellationToken.None);
        await dialogs.ShowMessageAsync(Loc.Get("Verify_Relink"), Loc.Format("Verify_Relinked", relinked, _lastReport.MissingFiles.Count));
        await VerifyLibraryAsync();
    }

    [RelayCommand]
    private async Task AdoptOrphansAsync()
    {
        if (_lastReport is null) return;
        var report = _lastReport;
        await activity.RunAsync(Loc.Get("Verify_Adopt"), async (_, ct) =>
        {
            var result = await maintenance.AdoptOrphansAsync(report, null, ct);
            return new ActivityOutcome(Loc.Format("Import_Summary", result.NewAssetIds.Count, result.Duplicates, result.Skipped, result.Failed), InfoBarSeverity.Success);
        });
        HasOrphans = false;
    }

    [RelayCommand]
    private async Task CleanPartialsAsync()
    {
        if (_lastReport is null) return;
        var removed = maintenance.CleanPartialFiles(_lastReport);
        HasPartials = false;
        await dialogs.ShowMessageAsync(Loc.Get("Verify_CleanPartials"), Loc.Format("Verify_PartialsRemoved", removed));
    }

    [RelayCommand]
    private async Task RebuildIndexAsync()
    {
        await activity.RunAsync(Loc.Get("Verify_RebuildIndex"), async (_, ct) =>
        {
            await maintenance.RebuildSearchIndexAsync(ct);
            return new ActivityOutcome(Loc.Get("Verify_IndexRebuilt"), InfoBarSeverity.Success);
        });
    }
}
