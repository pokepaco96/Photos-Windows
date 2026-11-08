using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PhotosWindows.App.Services;
using PhotosWindows.Application.Organization;
using PhotosWindows.Application.Thumbnails;
using PhotosWindows.Domain;

namespace PhotosWindows.App.ViewModels;

/// <summary>A folder or album card in the albums browser.</summary>
public sealed partial class AlbumCardViewModel : ObservableObject
{
    public AlbumCardViewModel(Album album)
    {
        Album = album;
        Name = album.Name;
        Subtitle = album.Kind == AlbumKind.Smart ? Loc.Get("Albums_Smart") : Loc.Format("Albums_ItemCount", album.ItemCount);
        Glyph = album.Kind == AlbumKind.Smart ? "" : "";
    }

    public AlbumCardViewModel(AlbumFolder folder, int childCount)
    {
        Folder = folder;
        Name = folder.Name;
        Subtitle = Loc.Format("Albums_FolderCount", childCount);
        Glyph = "";
    }

    public Album? Album { get; }
    public AlbumFolder? Folder { get; }
    public string Name { get; }
    public string Subtitle { get; }
    public string Glyph { get; }
    public bool IsFolder => Folder is not null;

    [ObservableProperty] public partial ImageSource? Cover { get; set; }
    [ObservableProperty] public partial bool HasCover { get; set; }
}

public sealed partial class AlbumsViewModel(OrganizationService organization, NavigationService navigation, DialogService dialogs, ThumbnailService thumbnails, ImageLoader images) : ObservableObject
{
    private IReadOnlyList<AlbumFolder> _folders = [];

    public ObservableCollection<AlbumCardViewModel> Items { get; } = [];
    public ObservableCollection<AlbumFolder> Breadcrumb { get; } = [];

    [ObservableProperty] public partial long? CurrentFolderId { get; set; }
    [ObservableProperty] public partial bool IsEmpty { get; set; }
    [ObservableProperty] public partial string Title { get; set; } = Loc.Get("Albums_Title");
    [ObservableProperty] public partial bool IsInFolder { get; set; }

    public async Task LoadAsync(long? folderId = null)
    {
        CurrentFolderId = folderId;
        IsInFolder = folderId is not null;
        _folders = await organization.GetFoldersAsync(CancellationToken.None);
        var albums = await organization.GetAlbumsAsync(CancellationToken.None);
        Items.Clear();
        foreach (var f in _folders.Where(f => f.ParentId == folderId))
        {
            var children = _folders.Count(x => x.ParentId == f.Id) + albums.Count(a => a.FolderId == f.Id);
            Items.Add(new AlbumCardViewModel(f, children));
        }

        foreach (var a in albums.Where(a => a.FolderId == folderId).OrderBy(a => a.Kind).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var card = new AlbumCardViewModel(a);
            Items.Add(card);
            _ = LoadCoverAsync(card);
        }

        Breadcrumb.Clear();
        var cursor = folderId;
        var path = new List<AlbumFolder>();
        while (cursor is { } id && _folders.FirstOrDefault(f => f.Id == id) is { } folder)
        {
            path.Insert(0, folder);
            cursor = folder.ParentId;
        }

        foreach (var p in path) Breadcrumb.Add(p);
        Title = path.Count == 0 ? Loc.Get("Albums_Title") : path[^1].Name;
        IsEmpty = Items.Count == 0;
    }

    private async Task LoadCoverAsync(AlbumCardViewModel card)
    {
        if (card.Album?.CoverAssetId is not { } cover) return;
        var path = await thumbnails.GetOrCreateAsync(cover, ThumbnailSize.Small, CancellationToken.None);
        if (path is null) return;
        card.Cover = await images.FromFileAsync(path, 400, CancellationToken.None);
        card.HasCover = card.Cover is not null;
    }

    public async Task OpenAsync(AlbumCardViewModel card)
    {
        if (card.Folder is { } folder) await LoadAsync(folder.Id);
        else if (card.Album is { } album) navigation.ToLibrary(new LibraryNavigation(LibraryScope.All, AlbumId: album.Id));
    }

    [RelayCommand]
    private Task UpAsync() => LoadAsync(_folders.FirstOrDefault(f => f.Id == CurrentFolderId)?.ParentId);

    [RelayCommand]
    private async Task NewAlbumAsync()
    {
        var name = await dialogs.PromptAsync(Loc.Get("Albums_NewAlbum"), Loc.Get("Album_NewName"));
        if (name is null) return;
        await organization.CreateAlbumAsync(name, CurrentFolderId, null, CancellationToken.None);
        await LoadAsync(CurrentFolderId);
    }

    [RelayCommand]
    private async Task NewFolderAsync()
    {
        var name = await dialogs.PromptAsync(Loc.Get("Albums_NewFolder"), Loc.Get("Albums_FolderName"));
        if (name is null) return;
        await organization.CreateFolderAsync(name, CurrentFolderId, CancellationToken.None);
        await LoadAsync(CurrentFolderId);
    }

    [RelayCommand]
    private async Task NewSmartAlbumAsync()
    {
        var editor = new SmartAlbumEditor(null);
        var dialog = new ContentDialog
        {
            Title = Loc.Get("Albums_NewSmart"),
            Content = editor.Build(),
            PrimaryButtonText = Loc.Get("Common_Create"),
            CloseButtonText = Loc.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialogs.ShowAsync(dialog) != ContentDialogResult.Primary) return;
        try
        {
            await organization.CreateSmartAlbumAsync(editor.Name, CurrentFolderId, editor.ToDefinition(), CancellationToken.None);
        }
        catch (ArgumentException ex)
        {
            await dialogs.ShowMessageAsync(Loc.Get("Albums_NewSmart"), ex.Message);
        }

        await LoadAsync(CurrentFolderId);
    }

    [RelayCommand]
    private async Task EditSmartAsync(AlbumCardViewModel? card)
    {
        if (card?.Album is not { Kind: AlbumKind.Smart } album) return;
        var editor = new SmartAlbumEditor(album);
        var dialog = new ContentDialog
        {
            Title = album.Name,
            Content = editor.Build(),
            PrimaryButtonText = Loc.Get("Common_Save"),
            CloseButtonText = Loc.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialogs.ShowAsync(dialog) != ContentDialogResult.Primary) return;
        await organization.UpdateSmartAlbumAsync(album.Id, editor.ToDefinition(), CancellationToken.None);
        if (editor.Name != album.Name && !string.IsNullOrWhiteSpace(editor.Name)) await organization.RenameAlbumAsync(album.Id, editor.Name, CancellationToken.None);
        await LoadAsync(CurrentFolderId);
    }

    [RelayCommand]
    private async Task RenameAsync(AlbumCardViewModel? card)
    {
        if (card is null) return;
        var name = await dialogs.PromptAsync(Loc.Get("Common_Rename"), Loc.Get("Album_NewName"), card.Name);
        if (name is null) return;
        if (card.Album is { } a) await organization.RenameAlbumAsync(a.Id, name, CancellationToken.None);
        else if (card.Folder is { } f) await organization.RenameFolderAsync(f.Id, name, CancellationToken.None);
        await LoadAsync(CurrentFolderId);
    }

    [RelayCommand]
    private async Task DeleteAsync(AlbumCardViewModel? card)
    {
        if (card is null) return;
        var message = card.IsFolder ? Loc.Format("Albums_DeleteFolderMessage", card.Name) : Loc.Format("Albums_DeleteAlbumMessage", card.Name);
        if (!await dialogs.ConfirmAsync(Loc.Get("Common_Delete"), message, Loc.Get("Common_Delete"), destructive: true)) return;
        if (card.Album is { } a) await organization.DeleteAlbumAsync(a.Id, CancellationToken.None);
        else if (card.Folder is { } f) await organization.DeleteFolderAsync(f.Id, CancellationToken.None);
        await LoadAsync(CurrentFolderId);
    }

    [RelayCommand]
    private async Task MoveAsync(AlbumCardViewModel? card)
    {
        if (card is null) return;
        var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 300 };
        combo.Items.Add(new ComboBoxItem { Content = Loc.Get("Albums_TopLevel"), Tag = null });
        foreach (var f in _folders.Where(f => card.Folder?.Id != f.Id)) combo.Items.Add(new ComboBoxItem { Content = f.Name, Tag = f.Id });
        combo.SelectedIndex = 0;
        var dialog = new ContentDialog
        {
            Title = Loc.Format("Albums_MoveTitle", card.Name),
            Content = combo,
            PrimaryButtonText = Loc.Get("Common_Move"),
            CloseButtonText = Loc.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialogs.ShowAsync(dialog) != ContentDialogResult.Primary) return;
        var target = (combo.SelectedItem as ComboBoxItem)?.Tag as long?;
        try
        {
            if (card.Album is { } a) await organization.MoveAlbumAsync(a.Id, target, CancellationToken.None);
            else if (card.Folder is { } f) await organization.MoveFolderAsync(f.Id, target, CancellationToken.None);
        }
        catch (InvalidOperationException ex)
        {
            await dialogs.ShowMessageAsync(Loc.Get("Common_Move"), ex.Message);
        }

        await LoadAsync(CurrentFolderId);
    }
}

/// <summary>Builds the rule editor UI for smart albums (field / operator / value rows).</summary>
public sealed class SmartAlbumEditor(Album? existing)
{
    private static readonly (SmartField Field, SmartOperator[] Operators)[] Fields =
    [
        (SmartField.Keyword, [SmartOperator.Is, SmartOperator.IsNot, SmartOperator.Contains, SmartOperator.DoesNotContain]),
        (SmartField.Favorite, [SmartOperator.Is]),
        (SmartField.MediaType, [SmartOperator.Is, SmartOperator.IsNot]),
        (SmartField.CaptureDate, [SmartOperator.After, SmartOperator.Before, SmartOperator.InLastDays]),
        (SmartField.ImportDate, [SmartOperator.InLastDays, SmartOperator.After, SmartOperator.Before]),
        (SmartField.Camera, [SmartOperator.Contains, SmartOperator.Is, SmartOperator.DoesNotContain]),
        (SmartField.Lens, [SmartOperator.Contains, SmartOperator.Is]),
        (SmartField.FileName, [SmartOperator.Contains, SmartOperator.DoesNotContain]),
        (SmartField.Title, [SmartOperator.Contains, SmartOperator.Is]),
        (SmartField.Rating, [SmartOperator.GreaterOrEqual, SmartOperator.LessOrEqual, SmartOperator.Is]),
        (SmartField.Edited, [SmartOperator.Is]),
        (SmartField.HasLocation, [SmartOperator.Is]),
        (SmartField.Format, [SmartOperator.Is, SmartOperator.IsNot]),
    ];

    private readonly TextBox _name = new() { Header = Loc.Get("Smart_Name"), Text = existing?.Name ?? Loc.Get("Smart_DefaultName") };
    private readonly ComboBox _match = new() { Header = Loc.Get("Smart_Match"), Items = { Loc.Get("Smart_MatchAll"), Loc.Get("Smart_MatchAny") }, SelectedIndex = existing?.SmartDefinition?.Match == SmartMatch.Any ? 1 : 0 };
    private readonly StackPanel _rows = new() { Spacing = 6 };

    public string Name => _name.Text.Trim();

    public FrameworkElement Build()
    {
        var panel = new StackPanel { Spacing = 10, MinWidth = 520 };
        panel.Children.Add(_name);
        panel.Children.Add(_match);
        panel.Children.Add(_rows);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_name, "Smart_NameBox");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_match, "Smart_MatchCombo");
        var add = new Button { Content = Loc.Get("Smart_AddRule") };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(add, "Smart_AddRule");
        add.Click += (_, _) => AddRow(null);
        panel.Children.Add(add);
        panel.Children.Add(new TextBlock { Text = Loc.Get("Smart_Help"), TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12 });
        foreach (var rule in existing?.SmartDefinition?.Rules ?? []) AddRow(rule);
        if (_rows.Children.Count == 0) AddRow(null);
        return new ScrollViewer { Content = panel, MaxHeight = 480 };
    }

    private void AddRow(SmartRule? rule)
    {
        var field = new ComboBox { Width = 150 };
        foreach (var (f, _) in Fields) field.Items.Add(new ComboBoxItem { Content = Loc.Get("SmartField_" + f), Tag = f });
        var op = new ComboBox { Width = 150 };
        var value = new TextBox { Width = 170, PlaceholderText = Loc.Get("Smart_Value") };
        var remove = new Button { Content = new FontIcon { Glyph = "", FontSize = 12 }, VerticalAlignment = VerticalAlignment.Stretch };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(remove, Loc.Get("Smart_RemoveRule"));
        var index = _rows.Children.Count;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(field, $"Smart_Field_{index}");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(op, $"Smart_Op_{index}");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(value, $"Smart_Value_{index}");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(remove, $"Smart_Remove_{index}");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(field, Loc.Get("Smart_Match"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(value, Loc.Get("Smart_Value"));
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { field, op, value, remove } };

        void FillOperators()
        {
            op.Items.Clear();
            if ((field.SelectedItem as ComboBoxItem)?.Tag is not SmartField f) return;
            foreach (var o in Fields.First(x => x.Field == f).Operators) op.Items.Add(new ComboBoxItem { Content = Loc.Get("SmartOp_" + o), Tag = o });
            op.SelectedIndex = 0;
            value.PlaceholderText = f switch
            {
                SmartField.CaptureDate or SmartField.ImportDate => "2024-12-31 / 30",
                SmartField.Favorite or SmartField.Edited or SmartField.HasLocation => "true / false",
                SmartField.MediaType => "Photo / Video",
                SmartField.Rating => "0-5",
                _ => Loc.Get("Smart_Value"),
            };
        }

        field.SelectionChanged += (_, _) => FillOperators();
        remove.Click += (_, _) => _rows.Children.Remove(row);
        field.SelectedIndex = rule is null ? 0 : Array.FindIndex(Fields, x => x.Field == rule.Field);
        FillOperators();
        if (rule is not null)
        {
            op.SelectedIndex = Math.Max(0, Array.IndexOf(Fields.First(x => x.Field == rule.Field).Operators, rule.Operator));
            value.Text = rule.Value;
        }

        _rows.Children.Add(row);
    }

    public SmartAlbumDefinition ToDefinition()
    {
        var rules = new List<SmartRule>();
        foreach (var child in _rows.Children.OfType<StackPanel>())
        {
            var combos = child.Children.OfType<ComboBox>().ToList();
            var text = child.Children.OfType<TextBox>().First().Text.Trim();
            if ((combos[0].SelectedItem as ComboBoxItem)?.Tag is SmartField f && (combos[1].SelectedItem as ComboBoxItem)?.Tag is SmartOperator o && text.Length > 0)
            {
                rules.Add(new SmartRule(f, o, text));
            }
        }

        return new SmartAlbumDefinition(_match.SelectedIndex == 1 ? SmartMatch.Any : SmartMatch.All, rules);
    }
}
