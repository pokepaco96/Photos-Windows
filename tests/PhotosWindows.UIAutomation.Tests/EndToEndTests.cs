using System.Diagnostics;
using System.Text.RegularExpressions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Definitions;
using FlaUI.Core.WindowsAPI;

namespace PhotosWindows.UIAutomation.Tests;

/// <summary>
/// End-to-end scenarios executed against the real application through its UI controls. Each scenario records
/// pass/fail/blocked with screenshots, UI Automation trees and logs (see Evidence). A scenario whose prerequisite
/// failed is reported as BLOCKED and fails the test: nothing is reported as passing unless it actually ran.
/// </summary>
[TestCaseOrderer("PhotosWindows.UIAutomation.Tests.StepOrderer", "PhotosWindows.UIAutomation.Tests")]
public sealed partial class EndToEndTests(QaFixture qa) : IClassFixture<QaFixture>
{
    private AppSession App => qa.App;

    private void Run(int number, string name, string[] requires, string? achieves, Action body)
    {
        qa.Evidence.BeginStep();
        qa.Evidence.Log($"=== {number:00} {name}");
        var sw = Stopwatch.StartNew();
        if (qa.Missing(requires) is { } missing)
        {
            qa.Evidence.Record(number, name, StepStatus.Blocked, sw.Elapsed, $"not executed: prerequisite '{missing}' was not reached");
            throw new InvalidOperationException($"BLOCKED – prerequisite '{missing}' was not reached, the scenario was not executed.");
        }

        try
        {
            body();
            qa.Session?.AssertNoErrorDialog();
            if (achieves is not null) qa.Achieve(achieves);
            qa.Evidence.Record(number, name, StepStatus.Passed, sw.Elapsed, null);
        }
        catch (Exception ex)
        {
            if (qa.Session is { } s && !s.App.HasExited)
            {
                s.Screenshot($"FAIL_{number:00}_{name}");
                s.DumpTree($"FAIL_{number:00}_{name}");
            }
            else
            {
                CaptureScreen($"FAIL_{number:00}_{name}");
            }

            qa.Evidence.Record(number, name, StepStatus.Failed, sw.Elapsed, ex.GetType().Name + ": " + ex.Message);
            throw;
        }
    }

    private void CaptureScreen(string name)
    {
        try
        {
            FlaUI.Core.Capturing.Capture.Screen().ToFile(qa.Evidence.NewFile("screenshots", name, ".png"));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            qa.Evidence.Log("screen capture failed: " + ex.Message);
        }
    }

    // ---------------- Helpers working through the UI ----------------

    private void Nav(string id)
    {
        App.Click(id);
        Thread.Sleep(700);
        WaitLibraryIdle();
    }

    private void OpenSettings()
    {
        var item = App.WaitFor(() => App.TryFind(cf => cf.ByAutomationId("SettingsItem").Or(cf.ByName("Settings").And(cf.ByControlType(ControlType.ListItem)))), "the Settings item");
        App.Invoke(item);
        App.Find("Settings_Title");
        Thread.Sleep(800);
    }

    private void WaitLibraryIdle()
    {
        Thread.Sleep(300);
        App.WaitUntil(() => App.TryFindById("LibraryLoading") is not { } ring || !ring.Patterns.RangeValue.IsSupported && ring.Properties.IsOffscreen.ValueOrDefault || IsRingIdle(ring), "the library to finish loading", TimeSpan.FromSeconds(30));
    }

    private static bool IsRingIdle(AutomationElement ring) => ring.Properties.IsOffscreen.ValueOrDefault || ring.BoundingRectangle.IsEmpty;

    private AutomationElement Tile(string fileName, TimeSpan? timeout = null) => App.Find("Tile_" + fileName, timeout);

    private bool TileExists(string fileName) => App.Exists("Tile_" + fileName);

    private IReadOnlyList<string> VisibleTiles() =>
        App.FindAll(cf => cf.ByControlType(ControlType.ListItem))
            .Select(e => e.Properties.AutomationId.ValueOrDefault ?? string.Empty)
            .Where(id => id.StartsWith("Tile_", StringComparison.Ordinal))
            .Select(id => id["Tile_".Length..])
            .Distinct()
            .ToList();

    private void SelectTiles(params string[] files)
    {
        for (var i = 0; i < files.Length; i++)
        {
            var tile = Tile(files[i]);
            var item = tile.Patterns.SelectionItem.Pattern;
            if (i == 0) item.Select();
            else item.AddToSelection();
            Thread.Sleep(200);
        }

        App.WaitUntil(() => App.TextOf("SelectionText").Contains(files.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal), $"{files.Length} selected item(s)");
    }

    /// <summary>Clicks a command bar button; opens the overflow ("…") menu if the button is not visible.</summary>
    private void Command(string id)
    {
        if (!App.Exists(id))
        {
            var more = App.WaitFor(() => App.FindAll(cf => cf.ByAutomationId("MoreButton")).FirstOrDefault(m => !m.Properties.IsOffscreen.ValueOrDefault), "the overflow menu button");
            App.Invoke(more);
            Thread.Sleep(500);
        }

        App.Click(id);
    }

    /// <summary>Waits until the activity bar shows a finished operation and returns its text.</summary>
    private string WaitActivity(TimeSpan? timeout = null)
    {
        Thread.Sleep(500);
        App.WaitUntil(() => App.TryFindById("ActivityBar") is { } bar && !bar.Properties.IsOffscreen.ValueOrDefault && !App.Exists("Common_CancelButton"),
            "the activity to finish", timeout ?? TimeSpan.FromMinutes(3));
        Thread.Sleep(500);
        var text = AppSession.AllText(App.Find("ActivityBar"));
        qa.Evidence.Log("activity: " + text);
        return text;
    }

    private void DismissActivity()
    {
        if (App.TryFindById("ActivityBar") is { } bar && bar.FindFirstDescendant(cf => cf.ByAutomationId("CloseButton")) is { } close)
        {
            App.Invoke(close);
        }
    }

    private void ImportFolder(string folder, bool copy)
    {
        Nav("Nav_Import");
        App.Click("Import_FromFolder");
        App.PickFolder(folder);
        App.Find("Import_ModeCopyRadio");
        App.Screenshot("import-options-dialog");
        App.SetChecked(copy ? "Import_ModeCopyRadio" : "Import_ModeAddRadio", true);
        App.DialogButton("PrimaryButton");
    }

    private static (int New, int Duplicates, int Skipped, int Failed) ParseImportSummary(string text)
    {
        var m = ImportSummaryRegex().Match(text);
        if (!m.Success) throw new InvalidOperationException("Import summary not found in: " + text);
        return (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value), int.Parse(m.Groups[4].Value));
    }

    [GeneratedRegex(@"(\d+) new item\(s\), (\d+) duplicate\(s\), (\d+) skipped, (\d+) failed")]
    private static partial Regex ImportSummaryRegex();

    private void OpenInViewer(string fileName)
    {
        var tile = Tile(fileName);
        tile.Patterns.ScrollItem.PatternOrDefault?.ScrollIntoView();
        tile.DoubleClick();
        App.WaitUntil(() => App.Exists("Viewer_FileName") && App.TextOf("Viewer_FileName") == fileName, $"the viewer showing {fileName}", TimeSpan.FromSeconds(30));
        Thread.Sleep(1500);
    }

    private void BackToLibrary()
    {
        AppSession.Keys(VirtualKeyShort.ESCAPE);
        App.WaitUntil(() => App.Exists("AssetsGrid"), "the library grid after going back", TimeSpan.FromSeconds(15));
        WaitLibraryIdle();
    }

    private string ManagedCopy(string fileName) =>
        Directory.EnumerateFiles(Path.Combine(qa.LibraryRoot, "originals"), fileName, SearchOption.AllDirectories).Single();
}
