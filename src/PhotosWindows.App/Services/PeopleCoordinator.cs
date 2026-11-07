using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PhotosWindows.Application.Abstractions;
using PhotosWindows.Application.People;
using PhotosWindows.Domain;

namespace PhotosWindows.App.Services;

/// <summary>
/// User-facing People flows shared by Settings, the People page, a person's photos and the viewer: consent, model
/// download, analysis in the activity bar (real progress, cancellable) and corrections that need a dialog.
/// </summary>
public sealed class PeopleCoordinator(PeopleService people, IFaceModelManager models, ActivityCenter activity, DialogService dialogs, WindowContext window, ImageLoader images, NavigationService navigation)
{
    public PeopleService People => people;

    public IFaceModelManager Models => models;

    /// <summary>The section is shown only when it really works: a library is open, People is on and the models are installed.</summary>
    public async Task<bool> IsAvailableAsync() => models.AllInstalled && people.ModelsReady && await people.IsEnabledAsync(CancellationToken.None);

    public static string DisplayName(Person person) => person.IsNamed ? person.Name! : Loc.Get("People_Unnamed");

    /// <summary>Explains the download (source, size, licences, what is transferred) and downloads only after confirmation.</summary>
    public async Task<bool> DownloadModelsAsync()
    {
        var details = new StackPanel { Spacing = 10, MaxWidth = 520 };
        details.Children.Add(Text(Loc.Get("People_DownloadIntro")));
        foreach (var model in models.Models)
        {
            details.Children.Add(Text(Loc.Format("People_DownloadModel", model.DisplayName, SelectionActions.FormatBytes(model.SizeBytes), model.License, model.Source.Host, model.Provenance), opacity: 0.85));
        }

        details.Children.Add(Text(Loc.Get("People_DownloadPrivacy")));
        var dialog = new ContentDialog
        {
            Title = Loc.Get("People_DownloadTitle"),
            Content = new ScrollViewer { Content = details, MaxHeight = 420 },
            PrimaryButtonText = Loc.Get("People_DownloadButton"),
            CloseButtonText = Loc.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(details, "People_DownloadDetails");
        if (await dialogs.ShowAsync(dialog) != ContentDialogResult.Primary) return false;

        return await activity.RunAsync(Loc.Get("People_DownloadActivity"), async (progress, ct) =>
        {
            await models.InstallAsync(new InlineProgress<(long Done, long Total)>(p => progress.Report(new ActivityProgress(
                p.Total == 0 ? null : (double)p.Done / p.Total,
                Loc.Format("People_DownloadProgress", SelectionActions.FormatBytes(p.Done), SelectionActions.FormatBytes(p.Total))))), ct);
            return new ActivityOutcome(Loc.Get("People_DownloadDone"), InfoBarSeverity.Success);
        });
    }

    public async Task InstallModelsFromFilesAsync()
    {
        var files = await window.PickFilesAsync([".onnx"]);
        if (files.Count == 0) return;
        var installed = await models.InstallFromFilesAsync(files, CancellationToken.None);
        await dialogs.ShowMessageAsync(Loc.Get("People_InstallFilesTitle"), installed.Count > 0
            ? Loc.Format("People_InstallFilesDone", string.Join(", ", installed))
            : Loc.Get("People_InstallFilesNone"));
    }

    /// <summary>Asks for consent with a clear explanation, then enables People for the open library.</summary>
    public async Task<bool> EnableAsync()
    {
        var body = new StackPanel { Spacing = 10, MaxWidth = 520 };
        foreach (var key in new[] { "People_ConsentWhat", "People_ConsentStored", "People_ConsentDelete", "People_ConsentNever" })
        {
            body.Children.Add(Text(Loc.Get(key)));
        }

        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(body, "People_ConsentText");
        var dialog = new ContentDialog
        {
            Title = Loc.Get("People_ConsentTitle"),
            Content = new ScrollViewer { Content = body, MaxHeight = 420 },
            PrimaryButtonText = Loc.Get("People_ConsentAccept"),
            CloseButtonText = Loc.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialogs.ShowAsync(dialog) != ContentDialogResult.Primary) return false;
        await people.EnableAsync(CancellationToken.None);
        return true;
    }

    /// <summary>Analyses photos without a result (all, or <paramref name="onlyAssets"/>) in the activity bar.</summary>
    public Task<bool> AnalyzeAsync(IReadOnlyCollection<long>? onlyAssets = null) =>
        activity.RunAsync(Loc.Get("People_AnalyzeActivity"), async (progress, ct) =>
        {
            var result = await people.AnalyzeAsync(onlyAssets, new InlineProgress<FaceAnalysisProgress>(p => progress.Report(new ActivityProgress(
                p.Total == 0 ? null : (double)p.Done / p.Total,
                Loc.Format("People_AnalyzeProgress", p.Done, p.Total, p.FacesFound)))), ct);
            var stats = await people.GetStatsAsync(CancellationToken.None);
            return new ActivityOutcome(
                Loc.Format("People_AnalyzeDone", result.Analyzed, result.FacesFound, stats.People, result.Failed),
                result.Failed > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success,
                Loc.Get("People_Open"), navigation.ToPeople);
        });

    public async Task<bool> DeleteAllAsync()
    {
        if (!await dialogs.ConfirmAsync(Loc.Get("People_DeleteAllTitle"), Loc.Get("People_DeleteAllMessage"), Loc.Get("People_DeleteAllButton"), destructive: true)) return false;
        await people.DeleteAllDataAsync(CancellationToken.None);
        return true;
    }

    public async Task<bool> RenameAsync(Person person)
    {
        var name = await dialogs.PromptAsync(Loc.Get("People_RenameTitle"), Loc.Get("People_NamePlaceholder"), person.Name, Loc.Get("Common_Save"));
        if (name is null) return false;
        await people.RenameAsync(person.Id, name, CancellationToken.None);
        return true;
    }

    /// <summary>Lets the user pick another person (by name and size) to merge <paramref name="person"/> into.</summary>
    public async Task<bool> MergeIntoAsync(Person person)
    {
        var others = (await people.GetPeopleAsync(CancellationToken.None)).Where(p => p.Id != person.Id).ToList();
        var target = await PickPersonAsync(others, Loc.Format("People_MergeIntoTitle", DisplayName(person)), Loc.Get("People_MergeButton"));
        if (target is null) return false;
        await people.MergeAsync([person.Id], target.Id, CancellationToken.None);
        return true;
    }

    public async Task<bool> DeleteAsync(Person person)
    {
        if (!await dialogs.ConfirmAsync(Loc.Get("People_DeleteTitle"), Loc.Format("People_DeleteMessage", DisplayName(person)), Loc.Get("Common_Delete"), destructive: true)) return false;
        await people.DeletePersonAsync(person.Id, CancellationToken.None);
        return true;
    }

    public async Task<Person?> PickPersonAsync(IReadOnlyList<Person> candidates, string title, string primary)
    {
        if (candidates.Count == 0)
        {
            await dialogs.ShowMessageAsync(title, Loc.Get("People_NoOtherPeople"));
            return null;
        }

        var combo = new ComboBox { MinWidth = 320, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var p in candidates)
        {
            combo.Items.Add(new ComboBoxItem { Content = $"{DisplayName(p)} · {Loc.Format("People_PhotoCount", p.AssetCount)}", Tag = p });
        }

        combo.SelectedIndex = 0;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(combo, "People_PickPerson");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(combo, title);
        var dialog = new ContentDialog { Title = title, Content = combo, PrimaryButtonText = primary, CloseButtonText = Loc.Get("Common_Cancel"), DefaultButton = ContentDialogButton.Primary };
        return await dialogs.ShowAsync(dialog) == ContentDialogResult.Primary ? (combo.SelectedItem as ComboBoxItem)?.Tag as Person : null;
    }

    /// <summary>
    /// Shows the faces of a person: the user can split selected faces into a new person ("not this person"), ignore
    /// them, or use one as the cover. Returns true when something changed.
    /// </summary>
    public async Task<bool> ManageFacesAsync(Person person)
    {
        var faces = await people.GetFacesOfPersonAsync(person.Id, CancellationToken.None);
        var grid = new GridView { SelectionMode = ListViewSelectionMode.Multiple, MaxHeight = 380, IsItemClickEnabled = false };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(grid, "Faces_Grid");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(grid, Loc.Format("People_FacesTitle", DisplayName(person)));
        foreach (var face in faces)
        {
            var image = new Image { Width = 96, Height = 96, Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill };
            var item = new GridViewItem { Content = new Border { CornerRadius = new CornerRadius(48), Child = image, Width = 96, Height = 96 }, Tag = face };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(item, $"Face_{face.Id}");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(item, Loc.Format("People_FaceName", face.Id));
            grid.Items.Add(item);
            _ = LoadFaceAsync(face.Id, image);
        }

        var cover = new Button { Content = Loc.Get("People_UseAsCover"), IsEnabled = false };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(cover, "Faces_UseAsCover");
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(Text(Loc.Get("People_FacesHelp"), opacity: 0.8));
        panel.Children.Add(grid);
        panel.Children.Add(cover);
        var dialog = new ContentDialog
        {
            Title = Loc.Format("People_FacesTitle", DisplayName(person)),
            Content = panel,
            PrimaryButtonText = Loc.Get("People_NotThisPerson"),
            SecondaryButtonText = Loc.Get("People_IgnoreFaces"),
            CloseButtonText = Loc.Get("Common_Close"),
            DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = false,
            IsSecondaryButtonEnabled = false,
        };
        grid.SelectionChanged += (_, _) =>
        {
            var count = grid.SelectedItems.Count;
            dialog.IsPrimaryButtonEnabled = count > 0 && count < faces.Count;
            dialog.IsSecondaryButtonEnabled = count > 0;
            cover.IsEnabled = count == 1;
        };
        var changed = false;
        cover.Click += async (_, _) =>
        {
            if ((grid.SelectedItem as GridViewItem)?.Tag is Face f)
            {
                await people.SetCoverAsync(person.Id, f.Id, CancellationToken.None);
                changed = true;
                dialog.Hide();
            }
        };

        var result = await dialogs.ShowAsync(dialog);
        var selected = grid.SelectedItems.OfType<GridViewItem>().Select(i => ((Face)i.Tag).Id).ToList();
        if (result == ContentDialogResult.Primary && selected.Count > 0)
        {
            await people.MoveFacesAsync(selected, null, CancellationToken.None);
            return true;
        }

        if (result == ContentDialogResult.Secondary && selected.Count > 0)
        {
            await people.IgnoreFacesAsync(selected, CancellationToken.None);
            return true;
        }

        return changed;
    }

    public async Task<ImageSource?> LoadFaceImageAsync(long faceId, int size = 192)
    {
        var path = await people.GetFaceThumbnailAsync(faceId, CancellationToken.None);
        return path is null ? null : await images.FromFileAsync(path, size, CancellationToken.None);
    }

    private async Task LoadFaceAsync(long faceId, Image target)
    {
        var source = await LoadFaceImageAsync(faceId, 96);
        if (source is not null) target.Source = source;
    }

    private static TextBlock Text(string text, double opacity = 1) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = opacity, IsTextSelectionEnabled = true };
}

/// <summary>Format helpers for People statistics.</summary>
public static class PeopleText
{
    public static string Stats(FaceAnalysisStats s) => Loc.Format("People_Stats",
        s.Analyzed.ToString("N0", CultureInfo.CurrentCulture),
        s.Photos.ToString("N0", CultureInfo.CurrentCulture),
        s.Faces.ToString("N0", CultureInfo.CurrentCulture),
        s.People.ToString("N0", CultureInfo.CurrentCulture),
        s.Ungrouped.ToString("N0", CultureInfo.CurrentCulture),
        s.Pending.ToString("N0", CultureInfo.CurrentCulture),
        s.Failed.ToString("N0", CultureInfo.CurrentCulture),
        s.Excluded.ToString("N0", CultureInfo.CurrentCulture));
}
