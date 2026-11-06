using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace PhotosWindows.App.Services;

/// <summary>Access to the main window (HWND for pickers, XamlRoot for dialogs, full screen).</summary>
public sealed class WindowContext
{
    private MainWindow? _window;

    public MainWindow Window => _window ?? throw new InvalidOperationException("Window not attached.");

    public XamlRoot XamlRoot => Window.Content.XamlRoot;

    public IntPtr Hwnd => WindowNative.GetWindowHandle(Window);

    public void Attach(MainWindow window) => _window = window;

    public bool IsFullScreen => Window.AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;

    public void SetFullScreen(bool fullScreen) =>
        Window.AppWindow.SetPresenter(fullScreen ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Default);

    public async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, Hwnd);
        StorageFolder? folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    public async Task<IReadOnlyList<string>> PickFilesAsync(IEnumerable<string> extensions)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary, ViewMode = PickerViewMode.Thumbnail };
        foreach (var ext in extensions) picker.FileTypeFilter.Add(ext);
        InitializeWithWindow.Initialize(picker, Hwnd);
        var files = await picker.PickMultipleFilesAsync();
        return files?.Select(f => f.Path).ToList() ?? [];
    }
}
