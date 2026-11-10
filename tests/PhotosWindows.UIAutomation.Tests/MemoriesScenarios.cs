using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.WindowsAPI;
using PhotosWindows.Domain;

namespace PhotosWindows.UIAutomation.Tests;

/// <summary>
/// Memories through the real interface, with synthetic photos dated relative to the day of the run: four taken
/// "on this day" two years ago and nine in one evening (an event). Originals are hashed before and checked after.
/// </summary>
public sealed partial class EndToEndTests
{
    private string MemoriesFolder => Path.Combine(qa.WorkRoot, "memories-src");

    // Static: xUnit creates a new test-class instance per step.
    private static Dictionary<string, string>? _memoryHashes;
    private static DateTime _memoryDay;

    private string OnThisDayId => "Memory_onthisday:" + _memoryDay.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
    private const string EventId = "Memory_event:202406011900";

    private void OpenMemories()
    {
        Nav("Nav_Memories");
        App.WaitUntil(() => App.TextOf("Memories_Title") is "Memories" or "Recuerdos", "the Memories page");
        App.WaitUntil(() => App.TryFindById("Memories_Loading") is not { } r || IsRingIdle(r), "Memories to finish loading", TimeSpan.FromSeconds(30));
        Thread.Sleep(800);
    }

    private AutomationElement MemoryCard(string id) => App.Find(id, TimeSpan.FromSeconds(20));

    private void CardAction(string cardId, string action)
    {
        var button = MemoryCard(cardId).FindFirstDescendant(cf => cf.ByAutomationId(action)) ?? throw new InvalidOperationException($"{action} not found in {cardId}.");
        App.Invoke(button);
    }

    private (int Position, int Total) SlidePosition()
    {
        var m = System.Text.RegularExpressions.Regex.Match(App.TextOf("Slideshow_Position"), @"(\d+)\D+(\d+)");
        return (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
    }

    [Fact, Step(50)]
    public void S50_Memories_open_and_explained() => Run(50, "Memories: cards for 'on this day' and an event, each explaining why", ["library"], "memories-open", () =>
    {
        Directory.CreateDirectory(qa.Inside(MemoriesFolder));
        var today = DateTime.Today;
        // 29 February only exists in leap years: use the same day four years back then.
        _memoryDay = today is { Month: 2, Day: 29 } ? today.AddYears(-4) : today.AddYears(-2);
        var seed = 500;
        for (var i = 0; i < 4; i++) QaDataset.Jpeg(Path.Combine(MemoriesFolder, $"Mem_dia_{i}.jpg"), seed++, _memoryDay.AddHours(9 + i), null, null, null, []);
        for (var i = 0; i < 9; i++) QaDataset.Jpeg(Path.Combine(MemoriesFolder, $"Mem_fiesta_{i}.jpg"), seed++, new DateTime(2024, 6, 1, 19, i * 5, 0), TimeSpan.FromHours(2), new GeoLocation(43.2630, -2.9350), null, []);
        _memoryHashes = Directory.GetFiles(MemoriesFolder).ToDictionary(f => f, QaDataset.Sha256);
        ImportFolder(MemoriesFolder, copy: false);
        var summary = WaitActivity(TimeSpan.FromMinutes(2));
        if (ParseImportSummary(summary).New != 13) throw new InvalidOperationException("Memories dataset import: " + summary);
        DismissActivity();

        OpenMemories();
        var day = MemoryCard(OnThisDayId);
        var party = MemoryCard(EventId);
        var dayText = AppSession.AllText(day);
        var partyText = AppSession.AllText(party);
        qa.Evidence.Log("on this day: " + dayText);
        qa.Evidence.Log("event: " + partyText);
        var years = today.Year - _memoryDay.Year;
        if (!dayText.Contains($"{years} years ago", StringComparison.Ordinal)) throw new InvalidOperationException("The 'on this day' card does not say how long ago: " + dayText);
        if (!dayText.Contains("4 items", StringComparison.Ordinal)) throw new InvalidOperationException("The 'on this day' card does not give its count: " + dayText);
        if (!partyText.Contains("9 items taken close together", StringComparison.Ordinal) || !partyText.Contains("43.26° N, 2.94° W", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The event card does not explain itself with its facts: " + partyText);
        }

        // Regression (UI run #34, screenshot): the second card's lower buttons were cut off because cards had different heights.
        foreach (var card in new[] { day, party })
        {
            var hide = card.FindFirstDescendant(cf => cf.ByAutomationId("Memory_Hide")) ?? throw new InvalidOperationException("Hide button missing.");
            if (hide.Properties.IsOffscreen.ValueOrDefault || !card.BoundingRectangle.Contains(hide.BoundingRectangle))
            {
                throw new InvalidOperationException($"A card's buttons are clipped: card {card.BoundingRectangle}, Hide {hide.BoundingRectangle}.");
            }
        }

        if (!App.TextOf("Memories_Summary").Contains("memories from", StringComparison.Ordinal)) throw new InvalidOperationException("Summary missing.");
        Thread.Sleep(1500);
        App.Screenshot("50_memories_light");
    });

    [Fact, Step(51)]
    public void S51_Memories_open_a_memory() => Run(51, "Memories: open a memory as a grid, open a photo, come back", ["memories-open"], null, () =>
    {
        OpenMemories();
        CardAction(EventId, "Memory_Open");
        App.WaitUntil(() => App.Exists("AssetsGrid") && App.TextOf("PageTitle").StartsWith("Moments of", StringComparison.Ordinal), "the memory's grid with its title", TimeSpan.FromSeconds(20));
        WaitLibraryIdle();
        for (var i = 0; i < 9; i++) Tile($"Mem_fiesta_{i}.jpg");
        if (TileExists("Mem_dia_0.jpg")) throw new InvalidOperationException("The grid shows photos that are not in the memory.");
        AssertNavSelected("Nav_Memories");
        OpenInViewer("Mem_fiesta_3.jpg");
        App.Screenshot("51_memory_photo_in_viewer");
        BackToLibrary();
        if (!App.TextOf("PageTitle").StartsWith("Moments of", StringComparison.Ordinal)) throw new InvalidOperationException("Coming back from the viewer lost the memory's grid.");
    });

    [Fact, Step(52)]
    public void S52_Memories_slideshow() => Run(52, "Memories: slideshow with pause, next, previous (wrapping), play, keyboard and exit", ["memories-open"], null, () =>
    {
        OpenMemories();
        CardAction(EventId, "Memory_Slideshow");
        App.Find("Slideshow_PlayPause", TimeSpan.FromSeconds(20));
        if (App.Find("Slideshow_PlayPause").Properties.Name.ValueOrDefault != "Pause") throw new InvalidOperationException("The slideshow did not start playing.");
        App.Click("Slideshow_PlayPause"); // pause
        App.WaitUntil(() => App.Find("Slideshow_PlayPause").Properties.Name.ValueOrDefault == "Play", "the slideshow paused");
        var (start, total) = SlidePosition();
        if (total != 9) throw new InvalidOperationException($"Expected 9 slides, found {total}.");
        Thread.Sleep(4500); // longer than the interval: paused means it must not move
        if (SlidePosition().Position != start) throw new InvalidOperationException("The slideshow advanced while paused.");
        App.Click("Slideshow_Next");
        App.WaitUntil(() => SlidePosition().Position == start % 9 + 1, "next slide");
        App.Click("Slideshow_Previous");
        App.WaitUntil(() => SlidePosition().Position == start, "previous slide");
        for (var i = 0; i < start; i++) App.Click("Slideshow_Previous"); // step back past the first: wraps to the last
        App.WaitUntil(() => SlidePosition().Position == 9, "wrap from the first slide to the last");
        App.Screenshot("52_slideshow_paused");
        App.Find("Slideshow_Next").Focus();
        AppSession.Keys(VirtualKeyShort.RIGHT); // wraps forward to the first
        App.WaitUntil(() => SlidePosition().Position == 1, "Right arrow to the first slide");
        AppSession.Keys(VirtualKeyShort.LEFT);
        App.WaitUntil(() => SlidePosition().Position == 9, "Left arrow back to the last slide");
        App.Click("Slideshow_PlayPause"); // play
        App.WaitUntil(() => SlidePosition().Position != 9, "the slideshow advancing on its own", TimeSpan.FromSeconds(12));
        AppSession.Keys(VirtualKeyShort.ESCAPE);
        App.WaitUntil(() => App.Exists("Memories_Title"), "Memories after leaving the slideshow", TimeSpan.FromSeconds(15));
        MemoryCard(EventId); // the page kept its cards
    });

    [Fact, Step(53)]
    public void S53_Memories_create_album() => Run(53, "Memories: create an album from a memory", ["memories-open"], null, () =>
    {
        OpenMemories();
        CardAction(EventId, "Memory_CreateAlbum");
        App.SetText("PromptTextBox", "Recuerdo QA");
        App.DialogButton("PrimaryButton");
        App.WaitUntil(() => App.Exists("Memories_Notice") && AppSession.AllText(App.Find("Memories_Notice")).Contains("Recuerdo QA", StringComparison.Ordinal), "the album-created notice", TimeSpan.FromSeconds(15));
        Nav("Nav_Albums");
        var card = App.Find("AlbumCard_Recuerdo QA").Properties.Name.ValueOrDefault ?? string.Empty;
        qa.Evidence.Log("album card: " + card);
        if (!card.Contains('9')) throw new InvalidOperationException("The album does not hold the memory's 9 photos: " + card);
    });

    [Fact, Step(54)]
    public void S54_Memories_hide_undo_and_persist() => Run(54, "Memories: hide with undo; hidden memories stay hidden after restart; restore", ["memories-open"], null, () =>
    {
        OpenMemories();
        CardAction(OnThisDayId, "Memory_Hide");
        App.WaitUntil(() => !App.Exists(OnThisDayId), "the card to disappear");
        App.Click("Memories_Undo");
        MemoryCard(OnThisDayId);
        CardAction(OnThisDayId, "Memory_Hide");
        App.WaitUntil(() => !App.Exists(OnThisDayId), "the card to disappear again");
        App.Screenshot("54_memory_hidden");

        qa.CloseApp();
        qa.Launch();
        App.Find("AssetsGrid", TimeSpan.FromSeconds(40));
        OpenMemories();
        MemoryCard(EventId);
        if (App.Exists(OnThisDayId)) throw new InvalidOperationException("The hidden memory came back after restarting.");
        for (var i = 0; i < 4; i++)
        {
            if (!File.Exists(Path.Combine(MemoriesFolder, $"Mem_dia_{i}.jpg"))) throw new InvalidOperationException("Hiding a memory deleted a photo.");
        }

        App.SetChecked("Memories_Options", true);
        App.WaitUntil(() => AppSession.AllText(App.Find("Memories_DecisionText")).Contains("Hidden", StringComparison.Ordinal), "the hidden memory listed in Options");
        App.Screenshot("54_memories_options");
        App.Click("Memories_Restore");
        MemoryCard(OnThisDayId);
    });

    [Fact, Step(55)]
    public void S55_Memories_exclude_a_kind() => Run(55, "Memories: exclude a kind of memory and bring it back", ["memories-open"], null, () =>
    {
        OpenMemories();
        App.SetChecked("Memories_Options", true);
        var combo = App.Find("Memories_KindCombo").AsComboBox();
        combo.Expand();
        Thread.Sleep(300);
        combo.Select("Events");
        combo.Collapse();
        App.Click("Memories_ExcludeKind");
        App.WaitUntil(() => !App.Exists(EventId), "event memories to disappear");
        MemoryCard(OnThisDayId);
        if (!AppSession.AllText(App.Find("Memories_DecisionText")).Contains("Kind excluded: Events", StringComparison.Ordinal)) throw new InvalidOperationException("The exclusion is not listed.");
        App.Click("Memories_Restore");
        MemoryCard(EventId);
        App.SetChecked("Memories_Options", false);
    });

    [Fact, Step(56)]
    public void S56_Memories_dark_small_window_and_originals() => Run(56, "Memories: dark theme, small window, accessible names; originals unchanged", ["memories-open"], null, () =>
    {
        OpenSettings();
        App.WaitFor(() => App.TryFind(cf => cf.ByName("Dark").And(cf.ByControlType(ControlType.RadioButton))), "the Dark theme option").Patterns.SelectionItem.Pattern.Select();
        Thread.Sleep(1000);
        OpenMemories();
        Thread.Sleep(1500);
        App.Screenshot("56_memories_dark");
        var window = App.MainWindow;
        window.Patterns.Window.Pattern.SetWindowVisualState(WindowVisualState.Normal);
        window.Patterns.Transform.Pattern.Resize(900, 640);
        Thread.Sleep(1500);
        App.Screenshot("56_memories_small_window_dark");
        MemoryCard(EventId);
        var unnamed = App.FindAll(cf => cf.ByControlType(ControlType.Button))
            .Where(b => !b.Properties.IsOffscreen.ValueOrDefault && string.IsNullOrWhiteSpace(b.Properties.Name.ValueOrDefault))
            .Select(b => b.Properties.AutomationId.ValueOrDefault ?? "?").ToList();
        window.Patterns.Window.Pattern.SetWindowVisualState(WindowVisualState.Maximized);
        OpenSettings();
        App.WaitFor(() => App.TryFind(cf => cf.ByName("System").And(cf.ByControlType(ControlType.RadioButton))), "the System theme option").Patterns.SelectionItem.Pattern.Select();
        if (unnamed.Count > 0) throw new InvalidOperationException("Memories buttons without an accessible name: " + string.Join(", ", unnamed.Distinct()));
        var changed = _memoryHashes!.Where(kv => !File.Exists(kv.Key) || QaDataset.Sha256(kv.Key) != kv.Value).Select(kv => kv.Key).ToList();
        if (changed.Count > 0) throw new InvalidOperationException("Original files changed: " + string.Join(", ", changed));
        qa.Evidence.Log($"All {_memoryHashes!.Count} Memories originals unchanged (SHA-256).");
    });
}
