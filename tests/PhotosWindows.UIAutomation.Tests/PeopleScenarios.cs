using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.WindowsAPI;

namespace PhotosWindows.UIAutomation.Tests;

/// <summary>
/// People through the real interface: model download, consent, analysis (cancel and resume), naming, opening a person,
/// splitting and merging, excluding a photo, persistence after restart and deleting all face data.
/// Uses public-domain NASA portraits (see <see cref="QaDataset.PeoplePhotos"/>); blocked when they cannot be downloaded.
/// </summary>
public sealed partial class EndToEndTests
{
    private const string Lee = "Mark Lee";
    private const string Cernan = "Eugene Cernan";

    private IReadOnlyList<AutomationElement> PersonCards() =>
        App.FindAll(cf => cf.ByControlType(ControlType.ListItem))
            .Where(e => (e.Properties.AutomationId.ValueOrDefault ?? string.Empty).StartsWith("PersonCard_", StringComparison.Ordinal))
            .ToList();

    private IReadOnlyList<string> PersonCardNames() => PersonCards().Select(e => e.Properties.Name.ValueOrDefault ?? string.Empty).ToList();

    /// <summary>Names of every text element in the app's windows (dialogs included).</summary>
    private string AllVisibleText() =>
        string.Join(" | ", App.FindAll(cf => cf.ByControlType(ControlType.Text)).Select(e => e.Properties.Name.ValueOrDefault ?? string.Empty));

    private void OpenPeople()
    {
        Nav("Nav_People");
        App.WaitUntil(() => App.TextOf("PageTitle") is "People" or "Personas", "the People page");
        Thread.Sleep(800);
    }

    [Fact, Step(29)]
    public void S29_Download_face_models() => Run(29, "People: download the face models after reading what is downloaded", ["library"], "face-models", () =>
    {
        // Before anything is set up the section is not offered.
        if (App.Exists("Nav_People")) throw new InvalidOperationException("The People section is shown although it is not set up.");
        OpenSettings();
        App.Find("Settings_People");
        App.Screenshot("29_settings_people_off");
        App.Click("Settings_DownloadFaceModels");
        App.Find("People_DownloadIntro", TimeSpan.FromSeconds(10));
        var text = AllVisibleText();
        qa.Evidence.Log("download dialog: " + text);
        foreach (var expected in new[] { "huggingface.co", "MIT", "Apache-2.0", "No photo, face, name" })
        {
            if (!text.Contains(expected, StringComparison.Ordinal)) throw new InvalidOperationException($"The download dialog does not say '{expected}'.");
        }

        App.Screenshot("29_download_dialog");
        App.DialogButton("PrimaryButton");
        var result = WaitActivity(TimeSpan.FromMinutes(5));
        if (!result.Contains("verified", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Model download did not finish: " + result);
        DismissActivity();
        OpenSettings();
        App.Find("Settings_RemoveFaceModels");
        qa.Evidence.Log("models: " + App.TextOf("Settings_PeopleModels"));
        if (App.Exists("Nav_People")) throw new InvalidOperationException("People appeared before the user turned it on.");
    });

    [Fact, Step(30)]
    public void S30_Turn_on_people_with_consent() => Run(30, "People: turn on for the library after the consent explanation", ["face-models"], "people-on", () =>
    {
        OpenSettings();
        App.Click("Settings_EnablePeople");
        App.Find("People_ConsentWhat", TimeSpan.FromSeconds(10));
        foreach (var part in new[] { "People_ConsentStored", "People_ConsentDelete", "People_ConsentNever" }) App.Find(part);
        qa.Evidence.Log("consent: " + string.Join(" | ", new[] { "People_ConsentWhat", "People_ConsentStored", "People_ConsentDelete", "People_ConsentNever" }.Select(App.TextOf)));
        App.Screenshot("30_consent_dialog");
        App.DialogButton("PrimaryButton");
        // "Analyse now?": analyse later, from the People page.
        App.DialogButton("CloseButton");
        App.WaitUntil(() => App.Exists("Nav_People"), "the People section in the sidebar", TimeSpan.FromSeconds(10));
        App.Find("Settings_DeletePeopleData");
        App.Screenshot("30_settings_people_on");
    });

    [Fact, Step(31)]
    public void S31_Analyse_cancel_and_resume() => Run(31, "People: analyse (cancel, then resume) and group the same people", ["people-on", "people-dataset"], "people-analysed", () =>
    {
        ImportFolder(qa.Inside(qa.Dataset.PeopleFolder), copy: true);
        var import = ParseImportSummary(WaitActivity());
        if (import.New != QaDataset.PeoplePhotos.Length) throw new InvalidOperationException($"Unexpected import {import}.");
        DismissActivity();

        OpenPeople();
        App.Screenshot("31_people_not_analysed");
        App.Click("People_Analyze");
        // Cancel as soon as the activity shows it can be cancelled; the run may already be over on a fast machine.
        var cancelled = false;
        if (App.TryFind(cf => cf.ByAutomationId("Common_CancelButton")) is { } cancel)
        {
            App.Invoke(cancel);
            cancelled = true;
        }

        var first = WaitActivity(TimeSpan.FromMinutes(3));
        qa.Evidence.Log($"first run ({(cancelled ? "cancel clicked" : "finished before cancel")}): {first}");
        DismissActivity();
        OpenPeople();
        if (App.Exists("People_Analyze"))
        {
            App.Click("People_Analyze");
            qa.Evidence.Log("resumed: " + WaitActivity(TimeSpan.FromMinutes(3)));
            DismissActivity();
            OpenPeople();
        }

        if (App.Exists("People_Analyze")) throw new InvalidOperationException("Photos are still pending after resuming.");
        var stats = App.TextOf("People_Stats");
        qa.Evidence.Log("stats: " + stats);
        App.WaitUntil(() => PersonCards().Count == 2, "two groups of people", TimeSpan.FromSeconds(10));
        var names = PersonCardNames();
        qa.Evidence.Log("cards: " + string.Join(" | ", names));
        if (!names.All(n => n.StartsWith("Unidentified person", StringComparison.Ordinal) || n.StartsWith("Persona sin identificar", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("A group has a name nobody gave it: " + string.Join(", ", names));
        }

        var ungrouped = App.FindAll(cf => cf.ByControlType(ControlType.ListItem)).Count(e => (e.Properties.AutomationId.ValueOrDefault ?? string.Empty).StartsWith("UngroupedFace_", StringComparison.Ordinal));
        if (ungrouped != 2) throw new InvalidOperationException($"Expected 2 ungrouped faces (Stafford, Thornton), found {ungrouped}.");
        Thread.Sleep(1500);
        App.Screenshot("31_people_grouped");
    });

    /// <summary>Opens the group that contains <paramref name="file"/> and names it.</summary>
    private void NameGroupContaining(string file, string name)
    {
        foreach (var index in Enumerable.Range(0, PersonCards().Count))
        {
            OpenPeople();
            var card = PersonCards()[index];
            if (!(card.Properties.Name.ValueOrDefault ?? string.Empty).StartsWith("Unidentified", StringComparison.Ordinal)
                && !(card.Properties.Name.ValueOrDefault ?? string.Empty).StartsWith("Persona sin", StringComparison.Ordinal)) continue;
            App.MouseClick(card);
            App.Find("Person_Rename", TimeSpan.FromSeconds(15));
            WaitLibraryIdle();
            if (!TileExists(file)) continue;
            App.Click("Person_Rename");
            App.SetText("PromptTextBox", name);
            App.DialogButton("PrimaryButton");
            App.WaitUntil(() => App.TextOf("PageTitle") == name, $"the person page titled {name}");
            return;
        }

        throw new InvalidOperationException($"No unnamed group contains {file}.");
    }

    [Fact, Step(32)]
    public void S32_Name_people() => Run(32, "People: name both groups", ["people-analysed"], "people-named", () =>
    {
        NameGroupContaining("lee_1993.jpg", Lee);
        NameGroupContaining("cernan_1964.jpg", Cernan);
        OpenPeople();
        App.Find("PersonCard_" + Lee);
        App.Find("PersonCard_" + Cernan);
        Thread.Sleep(1000);
        App.Screenshot("32_people_named");
    });

    [Fact, Step(33)]
    public void S33_Open_a_person() => Run(33, "People: a person's photos and the viewer show the name", ["people-named"], null, () =>
    {
        OpenPeople();
        App.MouseClick(App.Find("PersonCard_" + Lee));
        App.WaitUntil(() => App.TextOf("PageTitle") == Lee, "the person page");
        WaitLibraryIdle();
        var tiles = VisibleTiles();
        if (!tiles.Order().SequenceEqual(["lee_1984.jpg", "lee_1993.jpg"])) throw new InvalidOperationException("Person photos: " + string.Join(", ", tiles));
        App.Screenshot("33_person_photos");
        OpenInViewer("lee_1993.jpg");
        App.Click("Viewer_Info");
        App.WaitUntil(() => App.Exists("Info_People") && App.TextOf("Info_People").Contains(Lee, StringComparison.Ordinal), "the name in the info pane", TimeSpan.FromSeconds(10));
        App.Screenshot("33_viewer_people");
        App.Click("Viewer_Info");
        BackToLibrary();
    });

    [Fact, Step(34)]
    public void S34_Split_and_merge() => Run(34, "People: split a face out (Not this person) and merge it back", ["people-named"], null, () =>
    {
        OpenPeople();
        App.MouseClick(App.Find("PersonCard_" + Lee));
        App.WaitUntil(() => App.TextOf("PageTitle") == Lee, "the person page");
        App.Click("Person_Faces");
        var grid = App.Find("Faces_Grid", TimeSpan.FromSeconds(10));
        Thread.Sleep(800);
        var faces = grid.FindAllChildren(cf => cf.ByControlType(ControlType.ListItem));
        if (faces.Length != 2) throw new InvalidOperationException($"Expected 2 faces of {Lee}, found {faces.Length}.");
        App.Screenshot("34_faces_dialog");
        faces[0].Patterns.SelectionItem.Pattern.Select();
        Thread.Sleep(300);
        App.DialogButton("PrimaryButton"); // Not this person
        App.WaitUntil(() => App.TextOf("LibrarySubtitle").StartsWith("1 photo", StringComparison.Ordinal), "one photo left in the group", TimeSpan.FromSeconds(10));

        OpenPeople();
        App.WaitUntil(() => PersonCards().Count == 3, "the split face as a new group", TimeSpan.FromSeconds(10));
        var split = PersonCards().Single(c => !(c.Properties.AutomationId.ValueOrDefault ?? string.Empty).EndsWith(Lee, StringComparison.Ordinal)
                                              && !(c.Properties.AutomationId.ValueOrDefault ?? string.Empty).EndsWith(Cernan, StringComparison.Ordinal));
        App.Find("PersonCard_" + Lee).Patterns.SelectionItem.Pattern.AddToSelection();
        split.Patterns.SelectionItem.Pattern.AddToSelection();
        Thread.Sleep(400);
        App.Click("People_MergeSelected");
        var picker = App.Find("People_PickPerson", TimeSpan.FromSeconds(10));
        qa.Evidence.Log("merge keeps: " + AppSession.TextOf(picker));
        App.Screenshot("34_merge_dialog");
        App.DialogButton("PrimaryButton");
        App.WaitUntil(() => PersonCards().Count == 2, "two groups after merging", TimeSpan.FromSeconds(10));
        var lee = App.Find("PersonCard_" + Lee).Properties.Name.ValueOrDefault ?? string.Empty;
        if (!lee.Contains("2 photo", StringComparison.Ordinal)) throw new InvalidOperationException("After merging: " + lee);
    });

    [Fact, Step(35)]
    public void S35_Exclude_and_include_a_photo() => Run(35, "People: exclude a photo from People in the viewer, then include it again", ["people-named"], null, () =>
    {
        OpenPeople();
        App.MouseClick(App.Find("PersonCard_" + Cernan));
        App.WaitUntil(() => App.TextOf("PageTitle") == Cernan, "the person page");
        WaitLibraryIdle();
        OpenInViewer("gemini9_crew.jpg");
        App.Click("Viewer_Info");
        App.Click("Info_ExcludeFromPeople");
        App.WaitUntil(() => App.Exists("Info_IncludeInPeople"), "the photo to be excluded", TimeSpan.FromSeconds(10));
        App.Screenshot("35_excluded");
        App.Click("Viewer_Info");
        BackToLibrary();
        OpenPeople();
        App.WaitUntil(() => (App.Find("PersonCard_" + Cernan).Properties.Name.ValueOrDefault ?? string.Empty).Contains("1 photo", StringComparison.Ordinal), "Cernan with one photo", TimeSpan.FromSeconds(10));

        App.MouseClick(App.Find("PersonCard_" + Cernan));
        App.WaitUntil(() => App.TextOf("PageTitle") == Cernan, "the person page");
        Nav("Nav_All");
        OpenInViewer("gemini9_crew.jpg");
        App.Click("Viewer_Info");
        App.Click("Info_IncludeInPeople");
        WaitActivity(TimeSpan.FromMinutes(2));
        DismissActivity();
        App.Click("Viewer_Info");
        BackToLibrary();
        OpenPeople();
        App.WaitUntil(() => (App.Find("PersonCard_" + Cernan).Properties.Name.ValueOrDefault ?? string.Empty).Contains("2 photo", StringComparison.Ordinal), "Cernan with both photos again", TimeSpan.FromSeconds(15));
    });

    [Fact, Step(36)]
    public void S36_People_persist_after_restart() => Run(36, "People: names and groups persist after restarting the app", ["people-named"], null, () =>
    {
        qa.CloseApp();
        qa.Launch();
        App.Find("AssetsGrid", TimeSpan.FromSeconds(40));
        App.WaitUntil(() => App.Exists("Nav_People"), "the People section after restart", TimeSpan.FromSeconds(15));
        OpenPeople();
        App.Find("PersonCard_" + Lee);
        App.Find("PersonCard_" + Cernan);
        Thread.Sleep(1200);
        App.Screenshot("36_people_after_restart");
    });

    [Fact, Step(37)]
    public void S37_Delete_all_face_data() => Run(37, "People: delete all face recognition data; the library stays intact", ["people-on"], null, () =>
    {
        Nav("Nav_All");
        var before = App.TextOf("LibrarySubtitle");
        OpenSettings();
        App.Click("Settings_DeletePeopleData");
        qa.Evidence.Log("confirmation: " + App.DialogMessage());
        App.Screenshot("37_delete_all_confirmation");
        App.DialogButton("PrimaryButton");
        App.WaitUntil(() => !App.Exists("Nav_People"), "the People section to disappear", TimeSpan.FromSeconds(15));
        App.Find("Settings_EnablePeople", TimeSpan.FromSeconds(10));
        Nav("Nav_All");
        var after = App.TextOf("LibrarySubtitle");
        if (before != after) throw new InvalidOperationException($"The library changed: '{before}' → '{after}'.");
        App.Screenshot("37_library_after_delete");
    });
}
