using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PhotosWindows.App.Services;
using PhotosWindows.App.ViewModels;
using PhotosWindows.App.Views;
using PhotosWindows.Domain;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace PhotosWindows.App;

public sealed partial class MainWindow : Window
{
    private readonly NavigationService _navigation;

    public MainWindow()
    {
        ViewModel = App.Services.GetRequiredService<ShellViewModel>();
        _navigation = App.Services.GetRequiredService<NavigationService>();
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        if (Content is FrameworkElement root)
        {
            // The system draws the caption buttons with the OS theme; follow the app theme instead (UI run #10).
            root.ActualThemeChanged += (sender, _) => ApplyCaptionButtonColors(sender.ActualTheme);
            ApplyCaptionButtonColors(root.ActualTheme);
        }

        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        FitToDisplay();
        _navigation.Attach(ContentFrame);
        _navigation.Navigated += (_, e) => SyncSelection(e.Page, e.Parameter);
    }

    public ShellViewModel ViewModel { get; }

    private void ApplyCaptionButtonColors(ElementTheme theme)
    {
        var bar = AppWindow.TitleBar;
        var dark = theme == ElementTheme.Dark;
        byte fg = dark ? (byte)255 : (byte)0;
        bar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        bar.ButtonForegroundColor = Windows.UI.Color.FromArgb(255, fg, fg, fg);
        bar.ButtonHoverForegroundColor = Windows.UI.Color.FromArgb(255, fg, fg, fg);
        bar.ButtonPressedForegroundColor = Windows.UI.Color.FromArgb(255, fg, fg, fg);
        bar.ButtonInactiveForegroundColor = dark ? Windows.UI.Color.FromArgb(255, 0x9A, 0x9A, 0x9A) : Windows.UI.Color.FromArgb(255, 0x6E, 0x6E, 0x6E);
        bar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(0x18, fg, fg, fg);
        bar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(0x10, fg, fg, fg);
    }

    /// <summary>Initial size: 1400×900, or 90 % of the work area on smaller displays, centred.</summary>
    private void FitToDisplay()
    {
        var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
        var width = Math.Min(1400, (int)(area.Width * 0.9));
        var height = Math.Min(900, (int)(area.Height * 0.9));
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
    }

    public async Task InitializeAsync()
    {
        App.Services.GetRequiredService<ThemeService>().Apply();
        await ViewModel.StartAsync();
    }

    private void OnItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.IsSettingsInvoked)
        {
            _navigation.ToSettings();
            return;
        }

        switch ((args.InvokedItemContainer as NavigationViewItem)?.Tag as string)
        {
            case "Albums": _navigation.ToAlbums(); break;
            case "People": _navigation.ToPeople(); break;
            case "Places": _navigation.ToPlaces(); break;
            case "Memories": _navigation.ToMemories(); break;
            case "Devices": _navigation.ToDevices(); break;
            case "Backup": _navigation.ToBackup(); break;
            case "Duplicates": _navigation.ToDuplicates(); break;
            case "Imports": _navigation.ToImports(); break;
            case "Import": ShowImportMenu(args.InvokedItemContainer); break;
            case string tag when Enum.TryParse<LibraryScope>(tag, out var scope): _navigation.ToLibrary(new LibraryNavigation(scope)); break;
        }
    }

    private void ShowImportMenu(FrameworkElement anchor)
    {
        var menu = new MenuFlyout();
        var folder = new MenuFlyoutItem { Text = Loc.Get("Import_FromFolder"), Icon = new FontIcon { Glyph = "" }, Command = ViewModel.ImportFolderCommand };
        var files = new MenuFlyoutItem { Text = Loc.Get("Import_FromFiles"), Icon = new FontIcon { Glyph = "" }, Command = ViewModel.ImportFilesCommand };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(folder, "Import_FromFolder");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(files, "Import_FromFiles");
        menu.Items.Add(folder);
        menu.Items.Add(files);
        menu.ShowAt(anchor);
    }

    private void SyncSelection(Type page, object? parameter)
    {
        // Keep the sidebar highlight in sync when navigating back or from inside pages.
        string? tag = page == typeof(SettingsPage) ? null
            : page == typeof(AlbumsPage) ? "Albums"
            : page == typeof(PeoplePage) ? "People"
            : page == typeof(PlacesPage) ? "Places"
            : page == typeof(MemoriesPage) || page == typeof(SlideshowPage) ? "Memories"
            : page == typeof(DevicesPage) ? "Devices"
            : page == typeof(BackupPage) ? "Backup"
            : page == typeof(DuplicatesPage) ? "Duplicates"
            : page == typeof(ImportsPage) ? "Imports"
            // Use the navigation parameter: the page's own state is not set yet when Navigated fires.
            : page == typeof(LibraryPage) ? (parameter as LibraryNavigation ?? LibraryNavigation.AllPhotos) switch
            {
                { SearchText: not null } => null, // search results: no sidebar section
                { Ids: not null } => "Memories", // a memory opened as a grid
                { PersonId: not null } => "People",
                { AlbumId: not null } => "Albums",
                var t => t.Scope.ToString(),
            }
            : null;
        if (page == typeof(SettingsPage))
        {
            NavView.SelectedItem = NavView.SettingsItem;
            return;
        }

        if (tag is null) return;
        NavView.SelectedItem = NavView.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => i.Tag as string == tag);
    }

    private void OnBackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args) => _navigation.GoBack();

    private void OnSearchSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args) => ViewModel.Search(args.QueryText);

    private void OnSearchAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        SearchBox.Focus(FocusState.Keyboard);
    }

    private void OnImportAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ViewModel.ImportFolderCommand.Execute(null);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (ViewModel.HasLibrary && e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = Loc.Get("Import_DropCaption");
        }
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var deferral = e.GetDeferral();
        IReadOnlyList<IStorageItem> items;
        try
        {
            items = await e.DataView.GetStorageItemsAsync();
        }
        finally
        {
            deferral.Complete();
        }

        await ViewModel.ImportDroppedAsync(items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList());
    }
}
