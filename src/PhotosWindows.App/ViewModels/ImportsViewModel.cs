using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotosWindows.App.Services;
using PhotosWindows.Application.Abstractions;
using PhotosWindows.Domain;

namespace PhotosWindows.App.ViewModels;

public sealed record ImportJobRow(ImportJob Job)
{
    public string Title => Job.StartedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) + " · " + Loc.Get("ImportMode_" + Job.Mode);
    public string StatusText => Loc.Get("ImportStatus_" + Job.Status);
    public string Counts => Loc.Format("Imports_Counts", Job.Imported, Job.Duplicates, Job.Skipped, Job.Failed, Job.Total);
    public bool CanResume => Job.Status is ImportJobStatus.Interrupted or ImportJobStatus.Cancelled && Job.Processed < Job.Total;
}

public sealed record ImportItemRow(ImportItem Item)
{
    public string FileName => System.IO.Path.GetFileName(Item.SourcePath);
    public string Path => Item.SourcePath;
    public string StatusText => Loc.Get("ImportItem_" + Item.Status);
    public string Message => Item.Message ?? string.Empty;
}

public sealed partial class ImportsViewModel(IImportJobRepository jobs, ImportCoordinator import) : ObservableObject
{
    public ObservableCollection<ImportJobRow> Jobs { get; } = [];
    public ObservableCollection<ImportItemRow> Items { get; } = [];

    [ObservableProperty] public partial ImportJobRow? SelectedJob { get; set; }

    /// <summary>Untyped mirror of <see cref="SelectedJob"/> for the ListView two-way binding.</summary>
    public object? SelectedJobItem
    {
        get => SelectedJob;
        set => SelectedJob = value as ImportJobRow;
    }
    [ObservableProperty] public partial bool ProblemsOnly { get; set; } = true;
    [ObservableProperty] public partial bool IsEmpty { get; set; }

    partial void OnSelectedJobChanged(ImportJobRow? value)
    {
        OnPropertyChanged(nameof(SelectedJobItem));
        _ = LoadItemsAsync();
    }

    partial void OnProblemsOnlyChanged(bool value) => _ = LoadItemsAsync();

    public async Task LoadAsync()
    {
        Jobs.Clear();
        foreach (var j in await jobs.GetJobsAsync(100, CancellationToken.None)) Jobs.Add(new ImportJobRow(j));
        IsEmpty = Jobs.Count == 0;
        SelectedJob = Jobs.FirstOrDefault();
    }

    private async Task LoadItemsAsync()
    {
        Items.Clear();
        if (SelectedJob is null) return;
        var items = await jobs.GetItemsAsync(SelectedJob.Job.Id, null, CancellationToken.None);
        foreach (var i in items.Where(i => !ProblemsOnly || i.Status is ImportItemStatus.Failed or ImportItemStatus.Unsupported or ImportItemStatus.Skipped or ImportItemStatus.Duplicate))
        {
            Items.Add(new ImportItemRow(i));
        }
    }

    [RelayCommand]
    private async Task ResumeAsync(ImportJobRow? row)
    {
        if (row is null) return;
        await import.ResumeAsync(row.Job.Id);
        await LoadAsync();
    }
}
