using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhotosWindows.Application.Abstractions;
using PhotosWindows.Application.Export;
using PhotosWindows.Application.Organization;
using PhotosWindows.Domain;

namespace PhotosWindows.App.Services;

/// <summary>Batch actions shared by the grid and the viewer (single place for the organisation rules and confirmations).</summary>
public sealed class SelectionActions(
    OrganizationService organization,
    ExportService export,
    DialogService dialogs,
    ActivityCenter activity,
    WindowContext window)
{
    public Task SetFavoriteAsync(IReadOnlyList<long> ids, bool favorite) => organization.SetFavoriteAsync(ids, favorite, CancellationToken.None);

    public Task SetHiddenAsync(IReadOnlyList<long> ids, bool hidden) => organization.SetHiddenAsync(ids, hidden, CancellationToken.None);

    public Task RestoreAsync(IReadOnlyList<long> ids) => organization.RestoreAsync(ids, CancellationToken.None);

    public Task RemoveFromAlbumAsync(long albumId, IReadOnlyList<long> ids) => organization.RemoveFromAlbumAsync(albumId, ids, CancellationToken.None);

    public async Task<bool> MoveToTrashAsync(IReadOnlyList<long> ids)
    {
        if (ids.Count == 0) return false;
        var ok = await dialogs.ConfirmAsync(Loc.Get("Trash_ConfirmTitle"), Loc.Format("Trash_ConfirmMessage", ids.Count), Loc.Get("Trash_Confirm"));
        if (ok) await organization.MoveToTrashAsync(ids, CancellationToken.None);
        return ok;
    }

    /// <summary>Previews exactly which files will be deleted and asks for explicit confirmation.</summary>
    public async Task<bool> DeletePermanentlyAsync(IReadOnlyList<long> ids)
    {
        if (ids.Count == 0) return false;
        var plan = await organization.PlanPermanentDeletionAsync(ids, includeReferencedFiles: false, CancellationToken.None);
        var includeReferenced = new CheckBox { Content = Loc.Get("Delete_AlsoReferenced"), IsChecked = false };
        var summary = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var list = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 12, Opacity = 0.8 };

        void Refresh(DeletionPlan p)
        {
            summary.Text = Loc.Format("Delete_Summary", p.AssetIds.Count, p.PhysicalFileCount, FormatBytes(p.PhysicalBytes), p.KeptReferencedFiles)
                + Environment.NewLine + (p.FilesAreRecoverable ? Loc.Get("Delete_RecycleBin") : Loc.Get("Delete_NotRecoverable"));
            list.Text = string.Join(Environment.NewLine, p.Files.Where(f => f.WillDelete).Take(20).Select(f => f.AbsolutePath))
                + (p.PhysicalFileCount > 20 ? Environment.NewLine + Loc.Format("Delete_AndMore", p.PhysicalFileCount - 20) : string.Empty);
        }

        Refresh(plan);
        includeReferenced.Checked += async (_, _) => { plan = await organization.PlanPermanentDeletionAsync(ids, true, CancellationToken.None); Refresh(plan); };
        includeReferenced.Unchecked += async (_, _) => { plan = await organization.PlanPermanentDeletionAsync(ids, false, CancellationToken.None); Refresh(plan); };

        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(includeReferenced, "Delete_IncludeReferenced");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(summary, "Delete_Summary");
        var panel = new StackPanel { Spacing = 10, MaxWidth = 520 };
        panel.Children.Add(summary);
        if (plan.KeptReferencedFiles > 0) panel.Children.Add(includeReferenced);
        panel.Children.Add(new ScrollViewer { Content = list, MaxHeight = 180 });
        var dialog = new ContentDialog
        {
            Title = Loc.Get("Delete_Title"),
            Content = panel,
            PrimaryButtonText = Loc.Get("Delete_Confirm"),
            CloseButtonText = Loc.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialogs.ShowAsync(dialog) != ContentDialogResult.Primary) return false;
        var result = await organization.DeletePermanentlyAsync(plan, CancellationToken.None);
        if (result.Errors.Count > 0)
        {
            await dialogs.ShowMessageAsync(Loc.Get("Delete_Title"), Loc.Format("Delete_Errors", result.Errors.Count) + Environment.NewLine + string.Join(Environment.NewLine, result.Errors.Take(5)));
        }

        return true;
    }

    public async Task AddToAlbumAsync(IReadOnlyList<long> ids)
    {
        if (ids.Count == 0) return;
        var albums = (await organization.GetAlbumsAsync(CancellationToken.None)).Where(a => a.Kind == AlbumKind.Manual).ToList();
        var combo = new ComboBox { PlaceholderText = Loc.Get("Album_Choose"), HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var a in albums) combo.Items.Add(new ComboBoxItem { Content = a.Name, Tag = a });
        var newName = new TextBox { PlaceholderText = Loc.Get("Album_NewName") };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(combo, "AlbumPicker_Combo");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(newName, "AlbumPicker_NewName");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(newName, Loc.Get("Album_NewName"));
        var panel = new StackPanel { Spacing = 10, MinWidth = 320 };
        if (albums.Count > 0) panel.Children.Add(combo);
        panel.Children.Add(new TextBlock { Text = Loc.Get("Album_OrCreate") });
        panel.Children.Add(newName);
        var dialog = new ContentDialog
        {
            Title = Loc.Format("Album_AddTitle", ids.Count),
            Content = panel,
            PrimaryButtonText = Loc.Get("Common_Add"),
            CloseButtonText = Loc.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialogs.ShowAsync(dialog) != ContentDialogResult.Primary) return;
        if (!string.IsNullOrWhiteSpace(newName.Text))
        {
            await organization.CreateAlbumAsync(newName.Text, null, ids, CancellationToken.None);
        }
        else if ((combo.SelectedItem as ComboBoxItem)?.Tag is Album album)
        {
            await organization.AddToAlbumAsync(album.Id, ids, CancellationToken.None);
        }
    }

    public async Task AddKeywordsAsync(IReadOnlyList<long> ids)
    {
        if (ids.Count == 0) return;
        var text = await dialogs.PromptAsync(Loc.Format("Keywords_AddTitle", ids.Count), Loc.Get("Keywords_Placeholder"));
        if (string.IsNullOrWhiteSpace(text)) return;
        var keywords = text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        await organization.AddKeywordsAsync(ids, keywords, CancellationToken.None);
    }

    public async Task ExportAsync(IReadOnlyList<long> ids)
    {
        if (ids.Count == 0) return;
        var edited = new RadioButton { Content = Loc.Get("Export_Edited"), GroupName = "kind", IsChecked = true };
        var original = new RadioButton { Content = Loc.Get("Export_Original"), GroupName = "kind" };
        var format = new ComboBox { Header = Loc.Get("Export_Format"), SelectedIndex = 0, MinWidth = 160 };
        format.Items.Add("JPEG");
        format.Items.Add("PNG");
        format.Items.Add("WebP");
        var quality = new Slider { Header = Loc.Get("Export_Quality"), Minimum = 50, Maximum = 100, Value = 92, StepFrequency = 1 };
        var size = new ComboBox { Header = Loc.Get("Export_Size"), SelectedIndex = 0, MinWidth = 160 };
        size.Items.Add(Loc.Get("Export_SizeFull"));
        size.Items.Add("3840 px");
        size.Items.Add("2048 px");
        size.Items.Add("1024 px");
        var metadata = new CheckBox { Content = Loc.Get("Export_Metadata"), IsChecked = true };
        var descriptive = new CheckBox { Content = Loc.Get("Export_Descriptive"), IsChecked = true };
        var location = new CheckBox { Content = Loc.Get("Export_Location"), IsChecked = false };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(edited, "Export_EditedRadio");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(original, "Export_OriginalRadio");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(format, "Export_FormatCombo");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(quality, "Export_QualitySlider");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(size, "Export_SizeCombo");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(metadata, "Export_MetadataCheck");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(descriptive, "Export_DescriptiveCheck");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(location, "Export_LocationCheck");
        var panel = new StackPanel { Spacing = 8, MinWidth = 360 };
        panel.Children.Add(edited);
        panel.Children.Add(original);
        panel.Children.Add(format);
        panel.Children.Add(quality);
        panel.Children.Add(size);
        panel.Children.Add(metadata);
        panel.Children.Add(descriptive);
        panel.Children.Add(location);
        void Sync() => format.IsEnabled = quality.IsEnabled = size.IsEnabled = metadata.IsEnabled = descriptive.IsEnabled = location.IsEnabled = edited.IsChecked == true;
        edited.Checked += (_, _) => Sync();
        original.Checked += (_, _) => Sync();
        var dialog = new ContentDialog
        {
            Title = Loc.Format("Export_Title", ids.Count),
            Content = new ScrollViewer { Content = panel },
            PrimaryButtonText = Loc.Get("Export_ChooseFolder"),
            CloseButtonText = Loc.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialogs.ShowAsync(dialog) != ContentDialogResult.Primary) return;
        var folder = await window.PickFolderAsync();
        if (folder is null) return;

        var options = new ExportOptions
        {
            Kind = original.IsChecked == true ? ExportKind.Original : ExportKind.Edited,
            Format = format.SelectedIndex switch { 1 => OutputFormat.Png, 2 => OutputFormat.WebP, _ => OutputFormat.Jpeg },
            Quality = (int)quality.Value,
            MaxLongSide = size.SelectedIndex switch { 1 => 3840, 2 => 2048, 3 => 1024, _ => null },
            IncludeMetadata = metadata.IsChecked == true,
            IncludeDescriptive = descriptive.IsChecked == true,
            IncludeLocation = location.IsChecked == true,
        };

        await activity.RunAsync(Loc.Get("Export_Activity"), async (progress, ct) =>
        {
            var exportProgress = new InlineProgress<ExportProgress>(p => progress.Report(new ActivityProgress((double)p.Done / Math.Max(1, p.Total), Loc.Format("Export_Progress", p.Done, p.Total, p.Current))));
            var results = await export.ExportAsync(ids, folder, options, exportProgress, ct);
            var failed = results.Count(r => r.Error is not null);
            return new ActivityOutcome(
                failed == 0 ? Loc.Format("Export_Done", results.Count, folder) : Loc.Format("Export_DoneWithErrors", results.Count - failed, failed, results.First(r => r.Error is not null).Error),
                failed == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
                Loc.Get("Export_OpenFolder"),
                () => _ = Windows.System.Launcher.LaunchFolderPathAsync(folder));
        });
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }
}
