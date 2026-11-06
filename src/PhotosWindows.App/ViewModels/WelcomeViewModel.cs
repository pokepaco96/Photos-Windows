using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotosWindows.App.Services;
using PhotosWindows.Application.Library;

namespace PhotosWindows.App.ViewModels;

public sealed record RecentLibrary(string Path, string Name, bool IsAvailable)
{
    public string StatusText => IsAvailable ? Path : Loc.Format("Welcome_RecentUnavailable", Path);
}

/// <summary>First run and "no library open": explains what a library is and lets the user create or open one.</summary>
public sealed partial class WelcomeViewModel(LibraryManager libraries, NavigationService navigation, DialogService dialogs, WindowContext window, ISettingsStore settings) : ObservableObject
{
    public ObservableCollection<RecentLibrary> Recent { get; } = [];

    [ObservableProperty] public partial bool HasRecent { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }

    public void Load()
    {
        Recent.Clear();
        foreach (var path in libraries.GetRecentLibraries())
        {
            Recent.Add(new RecentLibrary(path, System.IO.Path.GetFileNameWithoutExtension(path), Directory.Exists(path)));
        }

        HasRecent = Recent.Count > 0;
    }

    [RelayCommand]
    private async Task CreateAsync()
    {
        var name = await dialogs.PromptAsync(Loc.Get("Welcome_CreateTitle"), Loc.Get("Welcome_NamePlaceholder"), Loc.Get("Welcome_DefaultName"), Loc.Get("Welcome_ChooseLocation"));
        if (name is null) return;
        var parent = await window.PickFolderAsync();
        if (parent is null) return;
        await RunAsync(() => libraries.CreateAsync(parent, name, CancellationToken.None));
    }

    [RelayCommand]
    private async Task OpenAsync()
    {
        var folder = await window.PickFolderAsync();
        if (folder is null) return;
        if (!LibraryManager.LooksLikeLibrary(folder))
        {
            await dialogs.ShowMessageAsync(Loc.Get("Welcome_NotLibraryTitle"), Loc.Get("Welcome_NotLibraryMessage"));
            return;
        }

        await RunAsync(() => libraries.OpenAsync(folder, CancellationToken.None));
    }

    [RelayCommand]
    private Task OpenRecentAsync(RecentLibrary? recent) =>
        recent is null ? Task.CompletedTask : RunAsync(() => libraries.OpenAsync(recent.Path, CancellationToken.None));

    [RelayCommand]
    private void Forget(RecentLibrary? recent)
    {
        if (recent is null) return;
        libraries.ForgetRecent(recent.Path);
        Load();
    }

    private async Task RunAsync(Func<Task> open)
    {
        IsBusy = true;
        try
        {
            await open();
            settings.Save(settings.Load() with { FirstRunCompleted = true });
            navigation.ToLibrary(LibraryNavigation.AllPhotos);
            navigation.ClearHistory();
        }
        catch (LibraryOpenException ex)
        {
            await dialogs.ShowMessageAsync(Loc.Get("Library_Unavailable"), ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            await dialogs.ShowErrorAsync(Loc.Get("Library_Unavailable"), ex);
        }
        finally
        {
            IsBusy = false;
            Load();
        }
    }
}
