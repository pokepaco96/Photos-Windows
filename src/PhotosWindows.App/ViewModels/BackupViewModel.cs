using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotosWindows.App.Services;
using PhotosWindows.Application.Backup;
using PhotosWindows.Application.Library;

namespace PhotosWindows.App.ViewModels;

/// <summary>
/// Backup page. Two clearly separate things: the originals backup (plain YEAR/MONTH folders with every original
/// file) and the restorable library backup that lives in Settings.
/// </summary>
public sealed partial class BackupViewModel(
    OriginalsBackupService backup,
    ISettingsStore settings,
    WindowContext window,
    NavigationService navigation) : ObservableObject
{
    private CancellationTokenSource? _cts;
    private OriginalsPreflight? _preflight;
    private string? _unfinished;
    private string? _lastFolder;

    public ObservableCollection<string> History { get; } = [];
    public ObservableCollection<string> Problems { get; } = [];

    [ObservableProperty] public partial string Destination { get; set; } = string.Empty;
    [ObservableProperty] public partial bool HasDestination { get; set; }
    [ObservableProperty] public partial string Analysis { get; set; } = string.Empty;
    [ObservableProperty] public partial string SpaceText { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsAnalyzing { get; set; }
    [ObservableProperty] public partial bool HasWarning { get; set; }
    [ObservableProperty] public partial string Warning { get; set; } = string.Empty;
    [ObservableProperty] public partial bool HasBlocker { get; set; }
    [ObservableProperty] public partial string Blocker { get; set; } = string.Empty;
    /// <summary>Shown only when some originals are unavailable: the user must tick it to export without them.</summary>
    [ObservableProperty] public partial bool CanAcceptIncomplete { get; set; }
    [ObservableProperty] public partial bool AcceptIncomplete { get; set; }
    [ObservableProperty] public partial bool CanStart { get; set; }
    [ObservableProperty] public partial bool CanResume { get; set; }
    [ObservableProperty] public partial bool IsRunning { get; set; }
    [ObservableProperty] public partial bool IsIdle { get; set; } = true;
    [ObservableProperty] public partial double Progress { get; set; }
    [ObservableProperty] public partial string ProgressText { get; set; } = string.Empty;
    [ObservableProperty] public partial bool HasResult { get; set; }
    [ObservableProperty] public partial string ResultTitle { get; set; } = string.Empty;
    [ObservableProperty] public partial string ResultMessage { get; set; } = string.Empty;
    [ObservableProperty] public partial Microsoft.UI.Xaml.Controls.InfoBarSeverity ResultSeverity { get; set; }
    [ObservableProperty] public partial bool CanOpenFolder { get; set; }
    [ObservableProperty] public partial bool HasProblems { get; set; }
    [ObservableProperty] public partial bool HasHistory { get; set; }

    partial void OnAcceptIncompleteChanged(bool value) => UpdateCanStart();

    partial void OnIsRunningChanged(bool value)
    {
        IsIdle = !value;
        UpdateCanStart();
    }

    public async Task LoadAsync()
    {
        if (IsRunning) return;
        var s = settings.Load();
        Destination = s.OriginalsBackupDestination ?? string.Empty;
        HasDestination = Destination.Length > 0;
        LoadHistory(s);
        await AnalyzeAsync();
    }

    private void LoadHistory(AppSettings s)
    {
        History.Clear();
        foreach (var h in s.OriginalsBackupHistory)
        {
            History.Add(Loc.Format("Backup_HistoryLine", h.Utc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture), Loc.Get("Backup_Status_" + h.Status), h.Files, SelectionActions.FormatBytes(h.Bytes), Path.GetFileName(h.Folder)));
        }

        HasHistory = History.Count > 0;
    }

    [RelayCommand]
    private async Task ChooseDestinationAsync()
    {
        var folder = await window.PickFolderAsync();
        if (folder is null) return;
        Destination = folder;
        HasDestination = true;
        settings.Save(settings.Load() with { OriginalsBackupDestination = folder });
        await AnalyzeAsync();
    }

    [RelayCommand]
    private async Task AnalyzeAsync()
    {
        IsAnalyzing = true;
        try
        {
            var destination = HasDestination && Directory.Exists(Destination) ? Destination : null;
            _preflight = await backup.AnalyzeAsync(destination, CancellationToken.None);
            var p = _preflight;
            Analysis = Loc.Format("Backup_Analysis", p.PhotoCount, p.VideoCount, p.FileCount, SelectionActions.FormatBytes(p.TotalBytes));
            if (p.UndatedAssets > 0) Analysis += " " + Loc.Format("Backup_AnalysisUndated", p.UndatedAssets);
            SpaceText = !HasDestination ? Loc.Get("Backup_NoDestination")
                : destination is null ? Loc.Get("Backup_DestinationMissing")
                : p.FreeBytes is { } free ? Loc.Format("Backup_Space", SelectionActions.FormatBytes(free), SelectionActions.FormatBytes(p.RequiredBytes))
                : Loc.Get("Backup_SpaceUnknown");

            var missing = p.Unavailable.Count + p.TooLarge.Count;
            HasWarning = missing > 0;
            CanAcceptIncomplete = missing > 0;
            if (missing == 0) AcceptIncomplete = false;
            Warning = missing == 0 ? string.Empty
                : (p.Unavailable.Count > 0 ? Loc.Format("Backup_Unavailable", p.Unavailable.Count) + " " : string.Empty)
                  + (p.TooLarge.Count > 0 ? Loc.Format("Backup_TooLarge", p.TooLarge.Count) : string.Empty);
            Problems.Clear();
            foreach (var i in p.Unavailable.Concat(p.TooLarge).Take(200)) Problems.Add(i.OriginalName);
            HasProblems = Problems.Count > 0;

            Blocker = p.DestinationProblem is not null ? Loc.Get("Backup_BadDestination")
                : destination is not null && !p.EnoughSpace ? Loc.Get("Backup_NotEnoughSpace")
                : p.FileCount == 0 ? Loc.Get("Backup_NothingToCopy")
                : string.Empty;
            HasBlocker = Blocker.Length > 0;
            _unfinished = destination is null ? null : backup.FindUnfinished(destination).FirstOrDefault();
            CanResume = _unfinished is not null && !IsRunning;
            UpdateCanStart();
        }
        catch (InvalidOperationException)
        {
            // No library open: the page shows nothing to analyse.
            Analysis = string.Empty;
            CanStart = false;
        }
        finally
        {
            IsAnalyzing = false;
        }
    }

    private void UpdateCanStart() =>
        CanStart = !IsRunning && HasDestination && _preflight is { } p && Directory.Exists(Destination) && p.CanStart && (p.CanBeComplete || AcceptIncomplete);

    [RelayCommand]
    private Task StartAsync() => RunAsync((progress, ct) => backup.CreateAsync(Destination, AcceptIncomplete, progress, ct));

    [RelayCommand]
    private Task ResumeAsync() => _unfinished is null ? Task.CompletedTask : RunAsync((progress, ct) => backup.ResumeAsync(_unfinished, AcceptIncomplete, progress, ct));

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    private async Task RunAsync(Func<IProgress<OriginalsProgress>, CancellationToken, Task<OriginalsBackupResult>> work)
    {
        if (IsRunning) return;
        IsRunning = true;
        HasResult = false;
        Progress = 0;
        ProgressText = Loc.Get("Backup_Preparing");
        _cts = new CancellationTokenSource();
        var started = DateTime.UtcNow;
        var ui = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        var last = DateTime.MinValue;
        var progress = new InlineProgress<OriginalsProgress>(p =>
        {
            // The copy runs on a background thread: marshal to the UI and do not flood it.
            var now = DateTime.UtcNow;
            if (p.FilesDone < p.FilesTotal && (now - last).TotalMilliseconds < 120) return;
            last = now;
            ui.TryEnqueue(() =>
            {
                Progress = p.BytesTotal > 0 ? Math.Min(100, 100.0 * p.BytesDone / p.BytesTotal) : 0;
                var seconds = Math.Max(1, (now - started).TotalSeconds);
                ProgressText = Loc.Format("Backup_Progress", p.FilesDone, p.FilesTotal, SelectionActions.FormatBytes(p.BytesDone), SelectionActions.FormatBytes(p.BytesTotal),
                    SelectionActions.FormatBytes((long)(p.BytesDone / seconds)));
            });
        });

        try
        {
            var token = _cts.Token;
            var result = await Task.Run(() => work(progress, token));
            ShowResult(result);
        }
        catch (OriginalsBackupBlockedException)
        {
            // The library or the destination changed between the analysis and the start: analyse again and explain.
            await AnalyzeAsync();
            Show(Loc.Get("Backup_ResultBlockedTitle"), HasBlocker ? Blocker : Warning, Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            Show(Loc.Get("Backup_ResultFailedTitle"), ex.Message, Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error, null);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            IsRunning = false;
            await AnalyzeAsync();
        }
    }

    private void ShowResult(OriginalsBackupResult r)
    {
        var counts = Loc.Format("Backup_ResultCounts", r.FilesCopied + r.FilesAlreadyPresent, SelectionActions.FormatBytes(r.Bytes), r.Issues.Count, r.Folder);
        var (title, severity) = r.Status switch
        {
            OriginalsBackupStatus.Complete => (Loc.Get("Backup_ResultComplete"), Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success),
            OriginalsBackupStatus.PartialAccepted => (Loc.Get("Backup_ResultPartial"), Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning),
            OriginalsBackupStatus.Cancelled => (Loc.Get("Backup_ResultCancelled"), Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational),
            _ => (Loc.Get("Backup_ResultIncomplete"), Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error),
        };
        var message = r.Error is null ? counts : Loc.Get(r.Error.Contains("space", StringComparison.Ordinal) ? "Backup_ErrorSpace" : "Backup_ErrorDestination") + " " + counts;
        Show(title, message, severity, Directory.Exists(r.Folder) ? r.Folder : null);
        Problems.Clear();
        foreach (var i in r.Issues.Take(200)) Problems.Add($"{i.OriginalName} — {Loc.Get("Backup_Issue_" + i.Kind)}");
        HasProblems = Problems.Count > 0;

        var s = settings.Load();
        var entry = new OriginalsBackupHistoryEntry(DateTime.UtcNow, r.Folder, r.FilesCopied + r.FilesAlreadyPresent, r.Bytes, r.Status.ToString());
        s = s with { OriginalsBackupHistory = s.OriginalsBackupHistory.Prepend(entry).Take(10).ToList() };
        settings.Save(s);
        LoadHistory(s);
    }

    private void Show(string title, string message, Microsoft.UI.Xaml.Controls.InfoBarSeverity severity, string? folder)
    {
        ResultTitle = title;
        ResultMessage = message;
        ResultSeverity = severity;
        _lastFolder = folder;
        CanOpenFolder = folder is not null;
        HasResult = true;
    }

    [RelayCommand]
    private async Task OpenFolderAsync()
    {
        if (_lastFolder is not null && Directory.Exists(_lastFolder)) await Windows.System.Launcher.LaunchFolderPathAsync(_lastFolder);
    }

    [RelayCommand]
    private async Task VerifyExistingAsync()
    {
        var folder = await window.PickFolderAsync();
        if (folder is null) return;
        IsRunning = true;
        ProgressText = Loc.Get("Backup_Verifying");
        try
        {
            var v = await Task.Run(() => backup.VerifyAsync(folder, null, CancellationToken.None));
            Problems.Clear();
            foreach (var p in v.Problems.Take(200)) Problems.Add(p);
            HasProblems = Problems.Count > 0;
            Show(Loc.Get(v.IsValid ? "Backup_VerifyOk" : "Backup_VerifyBad"),
                v.Manifest is null ? Loc.Get("Backup_VerifyNotABackup") : Loc.Format("Backup_VerifyCounts", v.FilesChecked, v.Manifest.FileCount, v.Problems.Count),
                v.IsValid ? Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success : Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error, folder);
        }
        finally
        {
            IsRunning = false;
        }
    }

    [RelayCommand]
    private void OpenLibraryBackup() => navigation.ToSettings();
}
