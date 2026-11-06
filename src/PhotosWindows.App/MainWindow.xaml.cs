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
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1400, 900));
        _navigation.Attach(ContentFrame);
        _navigation.Navigated += (_, page) => SyncSelection(page);
    }

    public ShellViewModel ViewModel { get; }

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
            case "Duplicates": _navigation.ToDuplicates(); break;
            case "Imports": _navigation.ToImports(); break;
            case "Import": ShowImportMenu(args.InvokedItemContainer); break;
            case string tag when Enum.TryParse<LibraryScope>(tag, out var scope): _navigation.ToLibrary(new LibraryNavigation(scope)); break;
        }
    }

    private void ShowImportMenu(FrameworkElement anchor)
    {
        var menu = new MenuFlyout();
        menu.Items.Add(new MenuFlyoutItem { Text = Loc.Get("Import_FromFolder"), Icon = new FontIcon { Glyph = "" }, Command = ViewModel.ImportFolderCommand });
        menu.Items.Add(new MenuFlyoutItem { Text = Loc.Get("Import_FromFiles"), Icon = new FontIcon { Glyph = "" }, Command = ViewModel.ImportFilesCommand });
        menu.ShowAt(anchor);
    }

    private void SyncSelection(Type page)
    {
        // Keep the sidebar highlight in sync when navigating back or from inside pages.
        string? tag = page == typeof(SettingsPage) ? null
            : page == typeof(AlbumsPage) ? "Albums"
            : page == typeof(DuplicatesPage) ? "Duplicates"
            : page == typeof(ImportsPage) ? "Imports"
            : page == typeof(LibraryPage) && ContentFrame.Content is LibraryPage { Target: { AlbumId: null, SearchText: null } t } ? t.Scope.ToString()
            : page == typeof(LibraryPage) ? "Albums"
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
