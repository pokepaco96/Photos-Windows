using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PhotosWindows.App.Services;
using PhotosWindows.Application.Abstractions;
using PhotosWindows.Application.Library;
using PhotosWindows.Domain;

namespace PhotosWindows.App.ViewModels;

public sealed partial class ShellViewModel : ObservableObject
{
    private readonly LibraryManager _libraries;
    private readonly ISettingsStore _settings;
    private readonly NavigationService _navigation;
    private readonly DialogService _dialogs;
    private readonly ImportCoordinator _import;
    private readonly UiThread _ui;
    private readonly ILogger<ShellViewModel> _logger;

    public ShellViewModel(LibraryManager libraries, ILibraryContext context, ISettingsStore settings, NavigationService navigation, DialogService dialogs,
        ImportCoordinator import, ActivityCenter activity, UiThread ui, ILogger<ShellViewModel> logger)
    {
        _libraries = libraries;
        _settings = settings;
        _navigation = navigation;
        _dialogs = dialogs;
        _import = import;
        _ui = ui;
        _logger = logger;
        Activity = activity;
        context.LibraryChanged += (_, library) => _ui.Post(() =>
        {
            HasLibrary = library is not null;
            LibraryName = library?.Name ?? string.Empty;
        });
    }

    public ActivityCenter Activity { get; }

    [ObservableProperty] public partial bool HasLibrary { get; set; }
    [ObservableProperty] public partial string LibraryName { get; set; } = string.Empty;

    /// <summary>Opens the last library if it is still available; otherwise shows the welcome page with an explanation.</summary>
    public async Task StartAsync()
    {
        var last = _settings.Load().LastLibraryPath;
        if (string.IsNullOrEmpty(last))
        {
            _navigation.ToWelcome();
            return;
        }

        try
        {
            await _libraries.OpenAsync(last, CancellationToken.None);
            _navigation.ToLibrary(LibraryNavigation.AllPhotos);
            _navigation.ClearHistory();
        }
        catch (LibraryOpenException ex)
        {
            _logger.LogWarning(ex, "Last library could not be opened");
            _navigation.ToWelcome();
            await _dialogs.ShowMessageAsync(Loc.Get("Library_Unavailable"), ex.Message);
        }
    }

    public void Search(string text)
    {
        if (!HasLibrary || string.IsNullOrWhiteSpace(text)) return;
        _navigation.ToLibrary(new LibraryNavigation(LibraryScope.All, SearchText: text.Trim()));
    }

    [RelayCommand]
    private Task ImportFolderAsync() => HasLibrary ? _import.ImportFolderAsync() : Task.CompletedTask;

    [RelayCommand]
    private Task ImportFilesAsync() => HasLibrary ? _import.ImportFilesAsync() : Task.CompletedTask;

    public Task ImportDroppedAsync(IReadOnlyList<string> paths) => HasLibrary && paths.Count > 0 ? _import.ImportAsync(paths) : Task.CompletedTask;
}
