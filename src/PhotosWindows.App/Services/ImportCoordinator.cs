using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhotosWindows.Application.Import;
using PhotosWindows.Application.Library;
using PhotosWindows.Domain;

namespace PhotosWindows.App.Services;

/// <summary>Import flow: choose sources → choose mode → run in the activity bar → summary with link to the report.</summary>
public sealed class ImportCoordinator(
    ImportService import,
    ActivityCenter activity,
    DialogService dialogs,
    WindowContext window,
    ISettingsStore settings,
    BackgroundProcessing background,
    NavigationService navigation)
{
    public event EventHandler? ImportFinished;

    /// <summary>
    /// Raised (worker thread) while an import runs, when new assets have been committed since the last notice.
    /// Spaced out (2 s, growing to 15 s) so large imports show up progressively without reloading the grid per file.
    /// </summary>
    public event EventHandler? LibraryGrew;

    public async Task ImportFolderAsync()
    {
        var folder = await window.PickFolderAsync();
        if (folder is not null) await ImportAsync([folder]);
    }

    public async Task ImportFilesAsync()
    {
        var extensions = MediaFormats.All.SelectMany(f => f.Extensions).Distinct().ToList();
        var files = await window.PickFilesAsync(extensions);
        if (files.Count > 0) await ImportAsync(files);
    }

    /// <summary>Asks for the import options and starts the import. Used by pickers and drag &amp; drop.</summary>
    public async Task ImportAsync(IReadOnlyList<string> sources)
    {
        if (import.IsRunning || activity.IsBusy)
        {
            await dialogs.ShowMessageAsync(Loc.Get("Import_Title"), Loc.Get("Import_AlreadyRunning"));
            return;
        }

        var current = settings.Load();
        var addInPlace = new RadioButton { Content = Loc.Get("Import_ModeAdd"), GroupName = "mode", IsChecked = current.DefaultImportMode == ImportMode.AddInPlace };
        var copy = new RadioButton { Content = Loc.Get("Import_ModeCopy"), GroupName = "mode", IsChecked = current.DefaultImportMode == ImportMode.CopyToLibrary };
        var duplicates = new CheckBox { Content = Loc.Get("Import_IncludeDuplicates"), IsChecked = current.ImportDuplicates };
        var recursive = new CheckBox { Content = Loc.Get("Import_Subfolders"), IsChecked = true };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(addInPlace, "Import_ModeAddRadio");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(copy, "Import_ModeCopyRadio");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(duplicates, "Import_DuplicatesCheck");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(recursive, "Import_SubfoldersCheck");
        var panel = new StackPanel { Spacing = 8, MaxWidth = 460 };
        panel.Children.Add(new TextBlock { Text = sources.Count == 1 ? sources[0] : Loc.Format("Import_SourcesCount", sources.Count), TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });
        panel.Children.Add(addInPlace);
        panel.Children.Add(new TextBlock { Text = Loc.Get("Import_ModeAddHelp"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(28, -4, 0, 4), Opacity = 0.7 });
        panel.Children.Add(copy);
        panel.Children.Add(new TextBlock { Text = Loc.Get("Import_ModeCopyHelp"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(28, -4, 0, 4), Opacity = 0.7 });
        panel.Children.Add(recursive);
        panel.Children.Add(duplicates);
        panel.Children.Add(new TextBlock { Text = Loc.Get("Import_OriginalsSafe"), TextWrapping = TextWrapping.Wrap, Opacity = 0.7 });

        var dialog = new ContentDialog
        {
            Title = Loc.Get("Import_Title"),
            Content = panel,
            PrimaryButtonText = Loc.Get("Import_Start"),
            CloseButtonText = Loc.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialogs.ShowAsync(dialog) != ContentDialogResult.Primary) return;

        var mode = copy.IsChecked == true ? ImportMode.CopyToLibrary : ImportMode.AddInPlace;
        settings.Save(settings.Load() with { DefaultImportMode = mode, ImportDuplicates = duplicates.IsChecked == true });
        var request = new ImportRequest(sources, mode, duplicates.IsChecked == true, recursive.IsChecked == true);
        await RunAsync((progress, ct) => import.ImportAsync(request, progress, ct));
    }

    public Task ResumeAsync(long jobId) => RunAsync((progress, ct) => import.ResumeAsync(jobId, progress, ct));

    private async Task RunAsync(Func<IProgress<ImportProgress>, CancellationToken, Task<ImportResult>> run)
    {
        await activity.RunAsync(Loc.Get("Import_Title"), async (progress, ct) =>
        {
            var importProgress = new InlineProgress<ImportProgress>(p => progress.Report(Describe(p)));
            var grew = 0;
            var interval = TimeSpan.FromSeconds(2);
            var last = System.Diagnostics.Stopwatch.StartNew();
            void OnImported(object? sender, long id)
            {
                Interlocked.Exchange(ref grew, 1);
                if (last.Elapsed < interval || Interlocked.Exchange(ref grew, 0) == 0) return;
                last.Restart();
                interval = TimeSpan.FromSeconds(Math.Min(15, interval.TotalSeconds * 1.5));
                LibraryGrew?.Invoke(this, EventArgs.Empty);
            }

            import.AssetImported += OnImported;
            ImportResult result;
            try
            {
                result = await run(importProgress, ct);
            }
            finally
            {
                import.AssetImported -= OnImported;
            }

            background.Signal();
            var severity = result.Status switch
            {
                ImportJobStatus.Completed => InfoBarSeverity.Success,
                ImportJobStatus.Cancelled => InfoBarSeverity.Warning,
                _ => result.Failed > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success,
            };
            var message = result.Status == ImportJobStatus.Cancelled
                ? Loc.Format("Import_SummaryCancelled", result.Imported)
                : Loc.Format("Import_Summary", result.NewAssetIds.Count, result.Duplicates, result.Skipped, result.Failed);
            return new ActivityOutcome(message, severity, Loc.Get("Import_ViewReport"), navigation.ToImports);
        });
        ImportFinished?.Invoke(this, EventArgs.Empty);
    }

    private static ActivityProgress Describe(ImportProgress p)
    {
        switch (p.Phase)
        {
            case ImportPhase.Scanning:
                // The total is not known yet: indeterminate bar with real counters, no percentage or ETA.
                return new ActivityProgress(null, Loc.Format("Import_Scanning", p.Examined, p.Total,
                    string.IsNullOrEmpty(p.CurrentFolder) ? string.Empty : " · " + p.CurrentFolder));
            case ImportPhase.Preparing:
                return new ActivityProgress(null, Loc.Format("Import_Preparing", p.Total));
            default:
                var eta = p.Processed < p.Total && p.EstimatedRemaining is { TotalSeconds: > 0 } e
                    ? Loc.Format("Import_Eta", Math.Ceiling(e.TotalMinutes))
                    : string.Empty;
                var rate = p.Elapsed.TotalSeconds >= 2 && p.BytesProcessed > 0
                    ? Loc.Format("Import_Rate", p.BytesProcessed / 1048576.0 / p.Elapsed.TotalSeconds)
                    : string.Empty;
                return new ActivityProgress(p.Total == 0 ? null : p.Fraction,
                    Loc.Format("Import_Progress", p.Processed, p.Total, p.Imported, p.Duplicates, p.Failed + p.Skipped, rate + eta));
        }
    }
}
