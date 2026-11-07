using System.Globalization;
using FlaUI.Core.Definitions;
using FlaUI.Core.WindowsAPI;
using SkiaSharp;

namespace PhotosWindows.UIAutomation.Tests;

public sealed partial class EndToEndTests
{
    [Fact, Step(1)]
    public void S01_First_launch_shows_welcome() => Run(1, "First launch shows the welcome page", [], "app", () =>
    {
        var screen = System.Drawing.Rectangle.Empty;
        qa.Launch();
        var desktop = App.Automation.GetDesktop();
        screen = desktop.BoundingRectangle;
        qa.Extra["Screen"] = $"{screen.Width}×{screen.Height}";
        App.Find("Welcome_Title", TimeSpan.FromSeconds(30));
        App.Find("Welcome_Create");
        var window = App.MainWindow.BoundingRectangle;
        qa.Extra["Window"] = $"{window.Width}×{window.Height} at {window.X},{window.Y}";
        if (window.Right > screen.Right + 8 || window.Bottom > screen.Bottom + 8 || window.Left < screen.Left - 8)
        {
            throw new InvalidOperationException($"The main window ({window}) does not fit the screen ({screen}).");
        }

        App.Screenshot("01_welcome");
        App.DumpTree("01_welcome");
    });

    [Fact, Step(2)]
    public void S02_Create_library() => Run(2, "Create a library (name dialog + real folder picker)", ["app"], "library", () =>
    {
        App.Click("Welcome_Create");
        App.Find("PromptTextBox");
        App.SetText("PromptTextBox", qa.LibraryName);
        App.Screenshot("02_create_library_dialog");
        App.DialogButton("PrimaryButton");
        App.PickFolder(qa.Inside(qa.Libraries));
        App.Find("AssetsGrid", TimeSpan.FromSeconds(30));
        WaitLibraryIdle();
        App.Find("EmptyState");
        if (!File.Exists(Path.Combine(qa.LibraryRoot, "library.json"))) throw new InvalidOperationException("library.json was not created at " + qa.LibraryRoot);
        App.Screenshot("02_empty_library");
    });

    [Fact, Step(3)]
    public void S03_Close_and_reopen_library() => Run(3, "Close and reopen the library from the welcome page", ["library"], null, () =>
    {
        OpenSettings();
        App.Click("Settings_SwitchLibrary");
        App.Find("Welcome_Title");
        App.Screenshot("03_welcome_with_recent");
        App.Click("Welcome_OpenRecent");
        App.Find("AssetsGrid", TimeSpan.FromSeconds(30));
        WaitLibraryIdle();
        if (!App.TextOf("PageTitle").Contains("All photos", StringComparison.Ordinal)) throw new InvalidOperationException("Did not land on All photos: " + App.TextOf("PageTitle"));
    });

    [Fact, Step(4)]
    public void S04_Import_copy_to_library() => Run(4, "Import a folder with Copy to the library", ["library"], "imported-copy", () =>
    {
        ImportFolder(qa.Inside(qa.Dataset.CopyFolder), copy: true);
        var summary = ParseImportSummary(WaitActivity());
        App.Screenshot("04_import_copy_done");
        if (summary.New != QaDataset.CopyPhotos.Length || summary.Duplicates != 1 || summary.Failed != 0)
        {
            throw new InvalidOperationException($"Unexpected import result {summary}; expected {QaDataset.CopyPhotos.Length} new, 1 duplicate, 0 failed.");
        }

        Nav("Nav_All");
        App.WaitUntil(() => QaDataset.CopyPhotos.All(TileExists), "all copied photos to appear in the grid", TimeSpan.FromSeconds(30));
        var copies = Directory.EnumerateFiles(Path.Combine(qa.LibraryRoot, "originals"), "*", SearchOption.AllDirectories).Select(Path.GetFileName).ToList();
        foreach (var f in QaDataset.CopyPhotos.Append("RAW_0100.CR2"))
        {
            if (!copies.Contains(f)) throw new InvalidOperationException($"Managed copy of {f} not found in the library.");
        }

        Thread.Sleep(3000); // let background thumbnails fill in
        App.Screenshot("04_library_with_photos");
    });

    [Fact, Step(5)]
    public void S05_Import_add_in_place() => Run(5, "Import a folder with Add without moving", ["imported-copy"], "imported-inplace", () =>
    {
        var before = Directory.EnumerateFiles(Path.Combine(qa.LibraryRoot, "originals"), "*", SearchOption.AllDirectories).Count();
        ImportFolder(qa.Inside(qa.Dataset.InPlaceFolder), copy: false);
        var summary = ParseImportSummary(WaitActivity());
        var expectedNew = QaDataset.InPlacePhotos.Length + (qa.Dataset.HasVideo ? 1 : 0);
        if (summary.New != expectedNew || summary.Duplicates != 1 || summary.Skipped != 3)
        {
            throw new InvalidOperationException($"Unexpected import result {summary}; expected {expectedNew} new, 1 duplicate, 3 skipped.");
        }

        var after = Directory.EnumerateFiles(Path.Combine(qa.LibraryRoot, "originals"), "*", SearchOption.AllDirectories).Count();
        if (after != before) throw new InvalidOperationException("Add without moving copied files into the library.");
        Nav("Nav_All");
        App.WaitUntil(() => QaDataset.InPlacePhotos.All(TileExists), "in-place photos in the grid", TimeSpan.FromSeconds(30));
        App.Screenshot("05_library_after_second_import");
    });

    [Fact, Step(6)]
    public void S06_Duplicates_are_reported() => Run(6, "Duplicates are reported and not imported", ["imported-inplace"], null, () =>
    {
        Nav("Nav_Imports");
        App.Find("Imports_Jobs");
        Thread.Sleep(1500);
        var duplicate = App.FindByName("duplicado de IMG_0002.jpg", TimeSpan.FromSeconds(15));
        App.Screenshot("06_import_report");
        if (TileExistsAfterNav("duplicado de IMG_0002.jpg")) throw new InvalidOperationException("The duplicate was imported.");
        Nav("Nav_Duplicates");
        App.Find("Duplicates_Title");
        Thread.Sleep(2000);
        App.Screenshot("06_duplicates_page");
        _ = duplicate;
    });

    private bool TileExistsAfterNav(string file)
    {
        Nav("Nav_All");
        Thread.Sleep(500);
        return TileExists(file);
    }

    [Fact, Step(7)]
    public void S07_Incompatible_files_are_reported() => Run(7, "Corrupt, empty and disguised files are reported", ["imported-inplace"], null, () =>
    {
        Nav("Nav_Imports");
        Thread.Sleep(1500);
        foreach (var f in new[] { "corrupto.jpg", "vacio.png", "png-disfrazado.jpg" })
        {
            App.FindByName(f, TimeSpan.FromSeconds(15));
        }

        if (App.TryFind(cf => cf.ByName("notas.txt")) is not null) throw new InvalidOperationException("A .txt file was offered for import.");
        App.Screenshot("07_unsupported_files");
        Nav("Nav_All");
        foreach (var f in new[] { "corrupto.jpg", "vacio.png", "png-disfrazado.jpg" })
        {
            if (TileExists(f)) throw new InvalidOperationException($"{f} appears in the library.");
        }
    });

    [Fact, Step(8)]
    public void S08_Open_photo() => Run(8, "Open a photo in the viewer", ["imported-copy"], "viewer", () =>
    {
        Nav("Nav_All");
        OpenInViewer("GPS_Madrid.jpg");
        App.Find("Viewer_Photo");
        App.Click("Viewer_Info");
        App.Find("Viewer_InfoPane");
        Thread.Sleep(800);
        if (App.TryFind(cf => cf.ByName("QA Labs QA Cam 1").Or(cf.ByName("QA Cam 1"))) is null) throw new InvalidOperationException("Camera details missing in the info pane.");
        App.Screenshot("08_viewer_with_info");
        App.Click("Viewer_Info");
    });

    [Fact, Step(9)]
    public void S09_Navigate_between_photos() => Run(9, "Navigate with Next/Previous and arrow keys", ["viewer"], null, () =>
    {
        var first = App.TextOf("Viewer_Position");
        App.Click("Viewer_Next");
        App.WaitUntil(() => App.TextOf("Viewer_Position") != first, "the position to change after Next");
        var second = App.TextOf("Viewer_Position");
        App.Find("Viewer_Photo").Click();
        AppSession.Keys(VirtualKeyShort.LEFT);
        App.WaitUntil(() => App.TextOf("Viewer_Position") == first, "Left arrow to go back");
        qa.Evidence.Log($"positions: {first} → {second} → {first}");
    });

    [Fact, Step(10)]
    public void S10_Zoom() => Run(10, "Zoom in and out in the viewer", ["viewer"], null, () =>
    {
        var zoomer = App.Find("Viewer_Zoomer");
        var scroll = zoomer.Patterns.Scroll.Pattern;
        if (scroll.HorizontallyScrollable.Value) throw new InvalidOperationException("Photo is already zoomed before zooming.");
        zoomer.DoubleClick();
        App.WaitUntil(() => scroll.HorizontallyScrollable.Value || scroll.VerticallyScrollable.Value, "the photo to zoom in", TimeSpan.FromSeconds(5));
        App.Screenshot("10_viewer_zoomed");
        zoomer.DoubleClick();
        App.WaitUntil(() => !scroll.HorizontallyScrollable.Value && !scroll.VerticallyScrollable.Value, "the photo to zoom back out", TimeSpan.FromSeconds(5));
        BackToLibrary();
    });

    [Fact, Step(11)]
    public void S11_Video_playback() => Run(11, "Open and play a video", qa.Dataset.HasVideo ? ["imported-inplace"] : ["video-dataset"], null, () =>
    {
        Nav("Nav_Videos");
        OpenInViewer("clip.mp4");
        App.Find("Viewer_Player");
        Thread.Sleep(4000);
        if (App.TryFind(cf => cf.ByName("Cannot play video")) is not null)
        {
            App.Screenshot("11_video_error");
            throw new InvalidOperationException("The app reported the video cannot be played (codec/Media Foundation missing?).");
        }

        var slider = App.TryFind(cf => cf.ByAutomationId("ProgressSlider"));
        var position = slider?.Patterns.RangeValue.PatternOrDefault?.Value.ValueOrDefault ?? 0;
        App.Screenshot("11_video_playing");
        qa.Evidence.Log($"video position after 4 s: {position}");
        if (slider is not null && position <= 0) throw new InvalidOperationException("The video position did not advance (not playing).");
        BackToLibrary();
    });

    [Fact, Step(12)]
    public void S12_Albums_and_folders() => Run(12, "Create albums and folders; add and remove photos", ["imported-copy"], "albums", () =>
    {
        Nav("Nav_Albums");
        App.Find("Albums_NewAlbumButton");
        App.Screenshot("12_albums_empty");
        App.Click("Albums_NewAlbumButton");
        App.SetText("PromptTextBox", "Viaje QA");
        App.DialogButton("PrimaryButton");
        App.FindByName("Viaje QA");
        App.Click("Albums_NewFolderButton");
        App.SetText("PromptTextBox", "Carpeta QA");
        App.DialogButton("PrimaryButton");
        App.FindByName("Carpeta QA");

        Nav("Nav_All");
        SelectTiles("IMG_0001.jpg", "IMG_0002.jpg");
        Command("Cmd_AddToAlbum");
        App.SelectComboIndex("AlbumPicker_Combo", 0);
        App.DialogButton("PrimaryButton");

        Nav("Nav_Albums");
        Thread.Sleep(1000);
        App.Screenshot("12_albums_page");
        App.MouseClick(App.FindByName("Viaje QA"));
        App.WaitUntil(() => App.TextOf("PageTitle") == "Viaje QA", "the album to open");
        WaitLibraryIdle();
        App.WaitUntil(() => TileExists("IMG_0001.jpg") && TileExists("IMG_0002.jpg"), "album content");
        App.Screenshot("12_album_content");
        SelectTiles("IMG_0002.jpg");
        Command("Cmd_RemoveFromAlbum");
        App.WaitUntil(() => !TileExists("IMG_0002.jpg"), "the photo to leave the album");
        if (!File.Exists(ManagedCopy("IMG_0002.jpg"))) throw new InvalidOperationException("Removing from album deleted the file.");
    });

    [Fact, Step(13)]
    public void S13_Favorites() => Run(13, "Add and remove favorites", ["imported-copy"], "favorites", () =>
    {
        Nav("Nav_All");
        SelectTiles("IMG_0003.jpg");
        Command("Cmd_Favorite");
        Nav("Nav_Favorites");
        App.WaitUntil(() => TileExists("IMG_0003.jpg"), "IMG_0003 in Favorites");
        App.Screenshot("13_favorites");
        SelectTiles("IMG_0003.jpg");
        Command("Cmd_Unfavorite");
        App.WaitUntil(() => !TileExists("IMG_0003.jpg"), "IMG_0003 to leave Favorites");
        App.Find("EmptyState");
        App.Screenshot("13_favorites_empty");
        // Leave one favourite for the smart album scenario.
        Nav("Nav_All");
        SelectTiles("GPS_Madrid.jpg");
        Command("Cmd_Favorite");
    });

    [Fact, Step(14)]
    public void S14_Smart_album() => Run(14, "Create a smart album (Favorite is true)", ["albums", "favorites"], null, () =>
    {
        Nav("Nav_Albums");
        App.Click("Albums_NewSmartButton");
        App.SetText("Smart_NameBox", "Favoritas QA");
        App.SelectComboIndex("Smart_Field_0", 1);
        App.SetText("Smart_Value_0", "true");
        App.Screenshot("14_smart_album_dialog");
        App.DialogButton("PrimaryButton");
        App.MouseClick(App.FindByName("Favoritas QA"));
        App.WaitUntil(() => App.TextOf("PageTitle") == "Favoritas QA", "the smart album to open");
        WaitLibraryIdle();
        App.WaitUntil(() => TileExists("GPS_Madrid.jpg"), "the favourite in the smart album");
        if (VisibleTiles().Count != 1) throw new InvalidOperationException("Smart album shows unexpected items: " + string.Join(", ", VisibleTiles()));
        App.Screenshot("14_smart_album");
    });

    [Fact, Step(15)]
    public void S15_Hide_and_unhide() => Run(15, "Hide a photo and show it again", ["imported-inplace"], null, () =>
    {
        Nav("Nav_All");
        SelectTiles("Nieve_2019.jpg");
        Command("Cmd_Hide");
        App.WaitUntil(() => !TileExists("Nieve_2019.jpg"), "the photo to disappear");
        Nav("Nav_Hidden");
        App.WaitUntil(() => TileExists("Nieve_2019.jpg"), "the photo in Hidden");
        App.Screenshot("15_hidden");
        SelectTiles("Nieve_2019.jpg");
        Command("Cmd_Unhide");
        App.WaitUntil(() => !TileExists("Nieve_2019.jpg"), "Hidden to be empty");
        Nav("Nav_All");
        App.WaitUntil(() => TileExists("Nieve_2019.jpg"), "the photo back in the library");
    });

    [Fact, Step(16)]
    public void S16_Search() => Run(16, "Search by keyword, year and type", ["imported-inplace"], null, () =>
    {
        void Search(string text)
        {
            var box = App.Find("SearchBox");
            var edit = box.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit)) ?? box;
            edit.Focus();
            edit.Patterns.Value.Pattern.SetValue(text);
            AppSession.Keys(VirtualKeyShort.RETURN);
            App.WaitUntil(() => App.Exists("PageTitle") && App.TextOf("PageTitle").Contains(text, StringComparison.Ordinal), $"search results for '{text}'");
            WaitLibraryIdle();
            Thread.Sleep(800);
        }

        Search("beach");
        var beach = VisibleTiles();
        App.Screenshot("16_search_beach");
        if (!beach.Contains("IMG_0001.jpg") || !beach.Contains("Playa 2021.jpg") || beach.Contains("IMG_0002.jpg")) throw new InvalidOperationException("Keyword search returned: " + string.Join(", ", beach));
        Search("2019");
        var year = VisibleTiles();
        if (year.Count != 1 || year[0] != "Nieve_2019.jpg") throw new InvalidOperationException("Year search returned: " + string.Join(", ", year));
        Search("zzznothing");
        App.Find("EmptyState");
        App.Screenshot("16_search_no_results");
        if (qa.Dataset.HasVideo)
        {
            Search("video");
            var videos = VisibleTiles();
            if (videos.Count != 1 || videos[0] != "clip.mp4") throw new InvalidOperationException("Type search returned: " + string.Join(", ", videos));
        }
    });

    [Fact, Step(17)]
    public void S17_Edit_photo() => Run(17, "Edit a photo (exposure + Mono style)", ["viewer"], "edited", () =>
    {
        Nav("Nav_All");
        OpenInViewer("IMG_0001.jpg");
        App.Click("Viewer_Edit");
        App.Find("Edit_Exposure", TimeSpan.FromSeconds(30));
        Thread.Sleep(1500);
        App.Screenshot("17_editor_open");
        App.SetSlider("Edit_Exposure", 1.0);
        App.Click("Preset_Mono");
        Thread.Sleep(1500);
        App.Screenshot("17_editor_mono_exposure");
        if (Math.Abs(App.SliderValue("Edit_Exposure") - 1.0) > 0.01) throw new InvalidOperationException("Exposure slider did not keep its value.");
        if (Math.Abs(App.SliderValue("Edit_BlackAndWhite") - 100) > 0.5) throw new InvalidOperationException("Mono style did not set black & white.");
    });

    [Fact, Step(18)]
    public void S18_Undo_redo() => Run(18, "Undo and redo in the editor (buttons and Ctrl+Z / Ctrl+Y)", ["edited"], "edited-saved", () =>
    {
        App.Click("Editor_Undo");
        App.WaitUntil(() => Math.Abs(App.SliderValue("Edit_BlackAndWhite")) < 0.5, "undo to remove the Mono style");
        App.Find("Edit_Exposure").Focus();
        AppSession.Keys(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_Z);
        App.WaitUntil(() => Math.Abs(App.SliderValue("Edit_Exposure")) < 0.01, "Ctrl+Z to undo the exposure change");
        AppSession.Keys(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_Y);
        App.WaitUntil(() => Math.Abs(App.SliderValue("Edit_Exposure") - 1.0) < 0.01, "Ctrl+Y to redo the exposure change");
        App.Click("Editor_Redo");
        App.WaitUntil(() => Math.Abs(App.SliderValue("Edit_BlackAndWhite") - 100) < 0.5, "Redo to restore the Mono style");
        App.Click("Editor_Done");
        App.WaitUntil(() => App.Exists("Viewer_Compare"), "the viewer to show the edited photo (compare button)", TimeSpan.FromSeconds(20));
        Thread.Sleep(2000);
        App.Screenshot("18_viewer_edited");
        BackToLibrary();
    });

    [Fact, Step(19)]
    public void S19_Edit_persists_after_restart() => Run(19, "Restart the app: library reopens and edits persist; original unchanged", ["edited-saved"], "restarted", () =>
    {
        var copy = ManagedCopy("IMG_0001.jpg");
        qa.CloseApp();
        qa.Launch();
        App.Find("AssetsGrid", TimeSpan.FromSeconds(40));
        WaitLibraryIdle();
        OpenInViewer("IMG_0001.jpg");
        App.Find("Viewer_Compare");
        App.Click("Viewer_Edit");
        App.Find("Edit_Exposure", TimeSpan.FromSeconds(30));
        Thread.Sleep(1000);
        if (Math.Abs(App.SliderValue("Edit_Exposure") - 1.0) > 0.01 || Math.Abs(App.SliderValue("Edit_BlackAndWhite") - 100) > 0.5)
        {
            throw new InvalidOperationException("Edits did not persist after restart.");
        }

        App.Click("Editor_Cancel");
        App.Find("Viewer_Compare");
        BackToLibrary();
        var source = qa.Dataset.Hashes[Path.Combine("copy", "IMG_0001.jpg")];
        if (QaDataset.Sha256(copy) != source) throw new InvalidOperationException("The managed copy of the original was modified by editing.");
    });

    [Fact, Step(20)]
    public void S20_Export_original() => Run(20, "Export the original (byte-identical)", ["imported-copy"], null, () =>
    {
        var target = qa.Inside(Path.Combine(qa.Exports, "originales"));
        Nav("Nav_All");
        SelectTiles("IMG_0002.jpg");
        Command("Cmd_Export");
        App.SetChecked("Export_OriginalRadio", true);
        App.Screenshot("20_export_dialog");
        App.DialogButton("PrimaryButton");
        App.PickFolder(target);
        WaitActivity();
        var file = Path.Combine(target, "IMG_0002.jpg");
        if (!File.Exists(file)) throw new InvalidOperationException("Exported original not found.");
        if (QaDataset.Sha256(file) != qa.Dataset.Hashes[Path.Combine("copy", "IMG_0002.jpg")]) throw new InvalidOperationException("Exported original differs from the source.");
        DismissActivity();
    });

    [Fact, Step(21)]
    public void S21_Export_edited() => Run(21, "Export the edited copy", ["edited-saved"], null, () =>
    {
        var target = qa.Inside(Path.Combine(qa.Exports, "editadas"));
        Nav("Nav_All");
        SelectTiles("IMG_0001.jpg");
        Command("Cmd_Export");
        App.SetChecked("Export_EditedRadio", true);
        App.DialogButton("PrimaryButton");
        App.PickFolder(target);
        WaitActivity();
        var file = Path.Combine(target, "IMG_0001.jpg");
        if (!File.Exists(file)) throw new InvalidOperationException("Exported edited copy not found.");
        using var bmp = SKBitmap.Decode(file) ?? throw new InvalidOperationException("Exported file is not a valid image.");
        var p = bmp.GetPixel(bmp.Width / 2, bmp.Height / 2);
        if (Math.Abs(p.Red - p.Green) > 4 || Math.Abs(p.Green - p.Blue) > 4) throw new InvalidOperationException($"Exported edited copy is not black & white (centre pixel {p}).");
        File.Copy(file, Path.Combine(qa.Evidence.Root, "screenshots", "21_exported_edited_IMG_0001.jpg"), overwrite: true);
        DismissActivity();
    });

    [Fact, Step(22)]
    public void S22_Backup() => Run(22, "Create a verified backup", ["imported-copy"], "backup", () =>
    {
        OpenSettings();
        App.Screenshot("22_settings");
        App.SetChecked("Settings_IncludeOriginals", true);
        App.Click("Settings_CreateBackup");
        App.PickFolder(qa.Inside(qa.Backups));
        var text = WaitActivity();
        if (!text.Contains("verified", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Backup did not finish: " + text);
        var backup = Directory.EnumerateDirectories(qa.Backups, "PhotosWindows-Backup-*").SingleOrDefault() ?? throw new InvalidOperationException("Backup folder not found.");
        if (!File.Exists(Path.Combine(backup, "backup.json"))) throw new InvalidOperationException("backup.json missing.");
        qa.Extra["Backup"] = Path.GetFileName(backup);
        App.Screenshot("22_backup_done");
        DismissActivity();
    });

    [Fact, Step(23)]
    public void S23_Delete_and_recover() => Run(23, "Delete to Recently deleted, recover, and delete permanently", ["imported-copy"], null, () =>
    {
        Nav("Nav_All");
        SelectTiles("Dibujo.png");
        Command("Cmd_Delete");
        App.Screenshot("23_delete_confirmation");
        App.DialogButton("PrimaryButton");
        App.WaitUntil(() => !TileExists("Dibujo.png"), "the photo to leave the library");
        Nav("Nav_Trash");
        App.WaitUntil(() => TileExists("Dibujo.png"), "the photo in Recently deleted");
        App.Screenshot("23_recently_deleted");
        SelectTiles("Dibujo.png");
        Command("Cmd_Restore");
        App.WaitUntil(() => !TileExists("Dibujo.png"), "Recently deleted to be empty");
        Nav("Nav_All");
        App.WaitUntil(() => TileExists("Dibujo.png"), "the recovered photo");

        // Permanent deletion inside the synthetic library only.
        var managed = ManagedCopy("IMG_0003.jpg");
        SelectTiles("IMG_0003.jpg");
        Command("Cmd_Delete");
        App.DialogButton("PrimaryButton");
        Nav("Nav_Trash");
        SelectTiles("IMG_0003.jpg");
        Command("Cmd_DeletePermanently");
        var summary = App.TextOf("Delete_Summary");
        App.Screenshot("23_delete_permanently_plan");
        if (!summary.Contains("1 file(s)", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected deletion plan: " + summary);
        App.DialogButton("PrimaryButton");
        App.WaitUntil(() => !File.Exists(managed), "the managed copy to be deleted", TimeSpan.FromSeconds(15));
        if (!File.Exists(Path.Combine(qa.Dataset.CopyFolder, "IMG_0003.jpg"))) throw new InvalidOperationException("The import source was deleted!");
    });

    [Fact, Step(24)]
    public void S24_Theme() => Run(24, "Switch light and dark theme", ["library"], null, () =>
    {
        void Theme(string name)
        {
            OpenSettings();
            var radio = App.WaitFor(() => App.TryFind(cf => cf.ByName(name).And(cf.ByControlType(ControlType.RadioButton))), $"the {name} theme option");
            radio.Patterns.SelectionItem.Pattern.Select();
            Thread.Sleep(1000);
        }

        Theme("Dark");
        App.Screenshot("24_settings_dark");
        Nav("Nav_All");
        Thread.Sleep(1500);
        App.Screenshot("24_library_dark");
        OpenInViewer("GPS_Madrid.jpg");
        App.Screenshot("24_viewer_dark");
        BackToLibrary();
        Theme("Light");
        App.Screenshot("24_settings_light");
        Nav("Nav_All");
        Thread.Sleep(1500);
        App.Screenshot("24_library_light");
        Theme("System");
    });

    [Fact, Step(25)]
    public void S25_Keyboard_navigation() => Run(25, "Keyboard navigation (arrows, Enter, Escape, Ctrl+F)", ["imported-copy"], null, () =>
    {
        Nav("Nav_All");
        var first = VisibleTiles()[0];
        SelectTiles(first);
        Tile(first).Focus();
        AppSession.Keys(VirtualKeyShort.RIGHT);
        AppSession.Keys(VirtualKeyShort.RETURN);
        App.WaitUntil(() => App.Exists("Viewer_FileName"), "Enter to open the viewer");
        var opened = App.TextOf("Viewer_FileName");
        if (opened == first) throw new InvalidOperationException("Arrow key did not move the selection before Enter.");
        AppSession.Keys(VirtualKeyShort.RIGHT);
        AppSession.Keys(VirtualKeyShort.ESCAPE);
        App.WaitUntil(() => App.Exists("AssetsGrid"), "Escape to go back to the grid");
        AppSession.Keys(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_F);
        var focused = App.Automation.FocusedElement();
        qa.Evidence.Log($"focused after Ctrl+F: {focused.ControlType} '{focused.Name}' id={focused.Properties.AutomationId.ValueOrDefault}");
        if (focused.ControlType != ControlType.Edit) throw new InvalidOperationException("Ctrl+F did not focus the search box.");
        AppSession.Keys(VirtualKeyShort.ESCAPE);
    });

    [Fact, Step(26)]
    public void S26_Restore_backup() => Run(26, "Restore the backup as a new library", ["backup"], null, () =>
    {
        var backup = Directory.EnumerateDirectories(qa.Backups, "PhotosWindows-Backup-*").Single();
        var target = qa.Inside(Path.Combine(qa.Libraries, "restauradas"));
        OpenSettings();
        App.Click("Settings_RestoreNew");
        App.DialogButton("CloseButton");
        App.PickFolder(backup);
        App.DialogButton("CloseButton");
        App.PickFolder(target);
        var text = WaitActivity();
        if (!text.Contains("Restored", StringComparison.Ordinal)) throw new InvalidOperationException("Restore did not finish: " + text);
        App.Find("AssetsGrid", TimeSpan.FromSeconds(30));
        WaitLibraryIdle();
        App.WaitUntil(() => TileExists("IMG_0001.jpg"), "photos in the restored library");
        OpenSettings();
        if (!App.TextOf("Settings_LibraryPath").Contains("(restored)", StringComparison.Ordinal)) throw new InvalidOperationException("The restored library is not the one open.");
        App.Screenshot("26_restored_library_settings");
        DismissActivity();
    });

    [Fact, Step(27)]
    public void S27_Visual_and_accessibility_pass() => Run(27, "Visual pass of every page + accessible names", ["imported-copy"], null, () =>
    {
        foreach (var (nav, name) in new[] { ("Nav_All", "all"), ("Nav_Videos", "videos"), ("Nav_Favorites", "favorites"), ("Nav_RecentlyAdded", "recent"), ("Nav_Edited", "edited"), ("Nav_Located", "located"), ("Nav_Albums", "albums"), ("Nav_Duplicates", "duplicates"), ("Nav_Hidden", "hidden"), ("Nav_Trash", "trash"), ("Nav_Imports", "imports") })
        {
            Nav(nav);
            Thread.Sleep(1200);
            App.Screenshot("27_page_" + name);
        }

        OpenSettings();
        App.Screenshot("27_page_settings");
        App.DumpTree("27_settings_tree");
        // Every button must expose an accessible name for screen readers.
        var unnamed = App.FindAll(cf => cf.ByControlType(ControlType.Button))
            .Where(b => !b.Properties.IsOffscreen.ValueOrDefault && string.IsNullOrWhiteSpace(b.Properties.Name.ValueOrDefault))
            .Select(b => b.Properties.AutomationId.ValueOrDefault ?? "?")
            .ToList();
        Nav("Nav_All");
        unnamed.AddRange(App.FindAll(cf => cf.ByControlType(ControlType.Button))
            .Where(b => !b.Properties.IsOffscreen.ValueOrDefault && string.IsNullOrWhiteSpace(b.Properties.Name.ValueOrDefault))
            .Select(b => b.Properties.AutomationId.ValueOrDefault ?? "?"));
        if (unnamed.Count > 0) throw new InvalidOperationException("Buttons without an accessible name: " + string.Join(", ", unnamed.Distinct()));
    });

    [Fact, Step(28)]
    public void S28_Originals_untouched_and_clean_exit() => Run(28, "Originals unchanged (SHA-256) and clean exit", ["app"], null, () =>
    {
        qa.CloseApp();
        var changed = qa.Dataset.ChangedOriginals();
        if (changed.Count > 0) throw new InvalidOperationException("Original files changed: " + string.Join(", ", changed));
        qa.Evidence.Log($"All {qa.Dataset.Hashes.Count} dataset files unchanged (SHA-256).");
    });
}
