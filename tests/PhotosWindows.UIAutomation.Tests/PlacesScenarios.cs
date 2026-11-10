using System.Diagnostics;
using System.Text.RegularExpressions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.WindowsAPI;
using PhotosWindows.Domain;

namespace PhotosWindows.UIAutomation.Tests;

/// <summary>
/// Places through the real interface, with a synthetic GPS dataset (fixed seeds): Lisbon (3), Fiji and Samoa on
/// both sides of the antimeridian, Reykjavik, and a (0,0) placeholder that must count as "without location".
/// </summary>
public sealed partial class EndToEndTests
{
    private string PlacesFolder => Path.Combine(qa.WorkRoot, "places-src");

    private static readonly (string Name, double? Lat, double? Lon, DateTime Taken)[] PlacesPhotos =
    [
        ("Lisboa_1.jpg", 38.7223, -9.1393, new DateTime(2022, 6, 10, 10, 0, 0)),
        ("Lisboa_2.jpg", 38.7225, -9.1390, new DateTime(2022, 6, 11, 10, 0, 0)),
        ("Lisboa_3.jpg", 38.7221, -9.1396, new DateTime(2022, 6, 12, 10, 0, 0)),
        ("Fiji.jpg", -17.7134, 178.065, new DateTime(2023, 8, 1, 9, 0, 0)),
        ("Samoa.jpg", -14.0, -179.5, new DateTime(2023, 8, 3, 9, 0, 0)),
        ("Reykjavik.jpg", 64.1466, -21.9426, new DateTime(2018, 3, 5, 12, 0, 0)),
        ("Cero.jpg", 0, 0, new DateTime(2023, 1, 1, 12, 0, 0)),
    ];

    // Static: xUnit creates a new test-class instance per step.
    private static Dictionary<string, string>? _placesHashes;

    private void OpenPlaces()
    {
        Nav("Nav_Places");
        App.WaitUntil(() => App.TextOf("Places_Title") is "Places" or "Lugares", "the Places page");
        App.WaitUntil(() => App.TryFindById("Places_Loading") is not { } r || IsRingIdle(r), "Places to finish loading", TimeSpan.FromSeconds(30));
        Thread.Sleep(1000);
    }

    private IReadOnlyList<AutomationElement> Clusters() =>
        App.FindAll(cf => cf.ByAutomationId("Places_Cluster")).Where(e => !e.Properties.IsOffscreen.ValueOrDefault).ToList();

    private static int ItemsIn(string name)
    {
        var m = Regex.Match(name, @"(\d+) (items?|elementos?)");
        return m.Success ? int.Parse(m.Groups[1].Value) : 0;
    }

    /// <summary>Puts Places back in map mode (map on, list off) whatever a previous step left.</summary>
    private void EnsureMapMode()
    {
        App.SetChecked("Places_MapSwitch", true);
        App.SetChecked("Places_ListToggle", false);
        App.WaitUntil(() => App.Exists("Places_FitAll"), "map mode");
    }

    private int SummaryCount() => ItemsIn(App.TextOf("Places_Summary"));

    private AutomationElement ClusterNamed(string fragment) =>
        App.WaitFor(() => Clusters().FirstOrDefault(c => (c.Properties.Name.ValueOrDefault ?? "").Contains(fragment, StringComparison.Ordinal)), $"a cluster '{fragment}'");

    [Fact, Step(40)]
    public void S40_Places_open_with_clusters_and_no_gps_notice() => Run(40, "Places: open the section; clusters, counts and the no-GPS notice", ["library"], "places-open", () =>
    {
        Directory.CreateDirectory(qa.Inside(PlacesFolder));
        var seed = 400;
        foreach (var p in PlacesPhotos)
        {
            QaDataset.Jpeg(Path.Combine(PlacesFolder, p.Name), seed++, p.Taken, TimeSpan.FromHours(1), p.Lat is { } lat ? new GeoLocation(lat, p.Lon!.Value) : null, null, []);
        }

        _placesHashes = Directory.GetFiles(PlacesFolder).ToDictionary(f => f, QaDataset.Sha256);
        ImportFolder(PlacesFolder, copy: false);
        var summary = WaitActivity(TimeSpan.FromMinutes(2));
        if (ParseImportSummary(summary).New != PlacesPhotos.Length) throw new InvalidOperationException("Places dataset import: " + summary);
        DismissActivity();

        OpenPlaces();
        App.Find("Places_Map");
        var clusters = Clusters();
        qa.Evidence.Log("clusters: " + string.Join(" ; ", clusters.Select(c => c.Properties.Name.ValueOrDefault)));
        if (clusters.Count == 0) throw new InvalidOperationException("No clusters on the map.");
        if (clusters.Sum(c => ItemsIn(c.Properties.Name.ValueOrDefault ?? "")) != SummaryCount())
        {
            throw new InvalidOperationException($"Cluster counts do not add up to the summary '{App.TextOf("Places_Summary")}'.");
        }

        // (0,0) is a device placeholder: it must be counted as "without location", never drawn at Null Island.
        var notice = AppSession.AllText(App.Find("Places_NoGpsInfo"));
        if (ItemsIn(notice) < 1) throw new InvalidOperationException("No-GPS notice missing: " + notice);
        if (!App.TextOf("Places_Attribution").Contains("Natural Earth", StringComparison.Ordinal)) throw new InvalidOperationException("Map attribution missing.");
        App.Screenshot("40_places_map_light");
    });

    [Fact, Step(41)]
    public void S41_Places_enter_a_cluster() => Run(41, "Places: enter a cluster (zoom to area) and see its photos in the tray", ["places-open"], "places-cluster", () =>
    {
        App.Click("Places_FitAll");
        Thread.Sleep(800);
        var before = Clusters().Count;
        // The biggest cluster near Iberia groups Lisbon with Madrid/Málaga at low zoom; zooming to it splits them.
        var iberia = Clusters().OrderByDescending(c => ItemsIn(c.Properties.Name.ValueOrDefault ?? "")).First();
        App.Invoke(iberia);
        App.WaitUntil(() => App.Exists("Places_TrayCount"), "the tray");
        App.Click("Places_ZoomToSelection");
        Thread.Sleep(1200);
        qa.Evidence.Log($"clusters before {before}, after zoom {Clusters().Count}");
        var lisbon = ClusterNamed("9.14° W");
        App.Invoke(lisbon);
        App.WaitUntil(() => ItemsIn(App.TextOf("Places_TrayCount")) == 3, "the tray to list the 3 Lisbon photos");
        foreach (var n in new[] { "Lisboa_1.jpg", "Lisboa_2.jpg", "Lisboa_3.jpg" })
        {
            App.Find("PlacesTile_" + n, TimeSpan.FromSeconds(10));
        }

        // The tray narrows the map: the selected area must stay inside it (recentred if needed).
        Thread.Sleep(600);
        var mapRect = App.Find("Places_Map").BoundingRectangle;
        var selectedRect = ClusterNamed("9.14° W").BoundingRectangle;
        if (!mapRect.Contains(selectedRect)) throw new InvalidOperationException($"The selected area {selectedRect} is outside the map {mapRect} after the tray opened.");
        App.Screenshot("41_places_cluster_tray");
    });

    [Fact, Step(42)]
    public void S42_Places_open_photo_and_come_back() => Run(42, "Places: select a photo, open the viewer, go back with selection kept", ["places-cluster"], null, () =>
    {
        App.Find("PlacesTile_Lisboa_2.jpg").Patterns.SelectionItem.Pattern.Select();
        Thread.Sleep(300);
        App.Click("Places_OpenPhoto");
        App.WaitUntil(() => App.Exists("Viewer_FileName") && App.TextOf("Viewer_FileName") == "Lisboa_2.jpg", "the viewer showing Lisboa_2.jpg", TimeSpan.FromSeconds(30));
        App.Screenshot("42_viewer_from_places");
        AppSession.Keys(VirtualKeyShort.ESCAPE);
        App.WaitUntil(() => App.Exists("Places_TrayCount"), "Places with the tray still open", TimeSpan.FromSeconds(15));
        if (ItemsIn(App.TextOf("Places_TrayCount")) != 3) throw new InvalidOperationException("The selected area was lost after coming back from the viewer.");
    });

    [Fact, Step(43)]
    public void S43_Places_antimeridian() => Run(43, "Places: Fiji and Samoa (both sides of 180°) are framed together", ["places-open"], null, () =>
    {
        OpenPlaces();
        EnsureMapMode();
        if (App.Exists("Places_CloseTray")) App.Click("Places_CloseTray");
        App.Click("Places_FitAll");
        Thread.Sleep(1000);
        var map = App.Find("Places_Map").BoundingRectangle;
        var clusters = Clusters();
        var outside = clusters.Where(c => !map.Contains(c.BoundingRectangle)).Select(c => c.Properties.Name.ValueOrDefault).ToList();
        if (outside.Count > 0) throw new InvalidOperationException("Clusters outside the framed map: " + string.Join(", ", outside));
        if (clusters.Sum(c => ItemsIn(c.Properties.Name.ValueOrDefault ?? "")) != SummaryCount()) throw new InvalidOperationException("Not every place is on screen after 'Show all'.");
        App.Screenshot("43_places_fit_all");
    });

    [Fact, Step(44)]
    public void S44_Places_filter_by_date() => Run(44, "Places: filter by year, then clear filters", ["places-open"], null, () =>
    {
        var total = SummaryCount();
        var combo = App.Find("Places_Year").AsComboBox();
        combo.Expand();
        Thread.Sleep(300);
        combo.Select("2023");
        combo.Collapse();
        App.WaitUntil(() => SummaryCount() == 2, "2 located items in 2023 (Fiji, Samoa)", TimeSpan.FromSeconds(15));
        App.Screenshot("44_places_year_2023");
        App.Click("Places_ClearFilters");
        App.WaitUntil(() => SummaryCount() == total, "all places again after clearing filters", TimeSpan.FromSeconds(15));
    });

    [Fact, Step(45)]
    public void S45_Places_list_mode() => Run(45, "Places: list mode with one row per area", ["places-open"], null, () =>
    {
        App.SetChecked("Places_ListToggle", true);
        App.WaitUntil(() => App.Exists("Places_List") && !App.Exists("Places_FitAll"), "list mode");
        var rows = App.FindAll(cf => cf.ByAutomationId("Places_Group"));
        qa.Evidence.Log("groups: " + string.Join(" ; ", rows.Select(r => r.Properties.Name.ValueOrDefault)));
        if (rows.Count < 4) throw new InvalidOperationException($"Expected at least 4 areas, found {rows.Count}.");
        var fiji = rows.First(r => (r.Properties.Name.ValueOrDefault ?? "").Contains("17.71° S", StringComparison.Ordinal));
        App.Invoke(fiji);
        App.WaitUntil(() => App.Exists("Places_TrayCount") && ItemsIn(App.TextOf("Places_TrayCount")) == 1, "the Fiji tray");
        App.Screenshot("45_places_list");
        App.SetChecked("Places_ListToggle", false);
        App.WaitUntil(() => App.Exists("Places_FitAll"), "map mode again");
    });

    [Fact, Step(46)]
    public void S46_Places_map_off_and_on() => Run(46, "Places: turn the map off (persisted across navigation) and on", ["places-open"], null, () =>
    {
        OpenPlaces();
        EnsureMapMode();
        App.SetChecked("Places_MapSwitch", false);
        App.WaitUntil(() => App.Exists("Places_List") && !App.Exists("Places_FitAll"), "the list when the map is off");
        Nav("Nav_All");
        OpenPlaces();
        if (App.Exists("Places_FitAll")) throw new InvalidOperationException("The map came back after navigating although it was turned off.");
        App.Screenshot("46_places_map_off");
        App.SetChecked("Places_MapSwitch", true);
        App.WaitUntil(() => App.Exists("Places_FitAll"), "the map turned on");
    });

    [Fact, Step(47)]
    public void S47_Places_remove_location() => Run(47, "Places: remove an area's location; catalog updated, originals untouched", ["places-open"], null, () =>
    {
        OpenPlaces();
        EnsureMapMode();
        App.Click("Places_FitAll");
        Thread.Sleep(800);
        var total = SummaryCount();
        var without = ItemsIn(AppSession.AllText(App.Find("Places_NoGpsInfo")));
        App.Invoke(ClusterNamed("64.15° N"));
        App.WaitUntil(() => App.Exists("Places_TrayCount"), "the Reykjavik tray");
        App.Click("Places_RemoveLocation");
        App.DialogButton("PrimaryButton");
        App.WaitUntil(() => SummaryCount() == total - 1, "one place fewer", TimeSpan.FromSeconds(15));
        var after = ItemsIn(AppSession.AllText(App.Find("Places_NoGpsInfo")));
        if (after != without + 1) throw new InvalidOperationException($"No-GPS count {without} → {after}, expected +1.");
        App.Click("Places_ShowWithoutLocation");
        App.WaitUntil(() => TileExists("Reykjavik.jpg"), "Reykjavik among the items without location", TimeSpan.FromSeconds(15));
        var changed = _placesHashes!.Where(kv => QaDataset.Sha256(kv.Key) != kv.Value).Select(kv => kv.Key).ToList();
        if (changed.Count > 0) throw new InvalidOperationException("Original files changed: " + string.Join(", ", changed));
        qa.Evidence.Log($"All {_placesHashes!.Count} Places originals unchanged (SHA-256).");
    });

    [Fact, Step(48)]
    public void S48_Places_offline() => Run(48, "Places: the map draws while the app holds no network connection (sampled)", ["places-open"], null, () =>
    {
        OpenPlaces();
        EnsureMapMode();
        OpenPlaces();
        App.Click("Places_ZoomIn");
        App.Click("Places_ZoomOut");
        Thread.Sleep(1000);
        var r = App.Find("Places_Map").BoundingRectangle;
        var stats = App.PixelStats(new System.Drawing.Rectangle(r.X + r.Width / 4, r.Y + r.Height / 4, Math.Min(200, r.Width / 2), Math.Min(150, r.Height / 2)));
        qa.Evidence.Log($"map pixels: {stats}");
        if (stats.MaxLuminance - stats.MinLuminance < 5) throw new InvalidOperationException("The map area looks blank.");
        var pid = App.App.ProcessId;
        var psi = new ProcessStartInfo("powershell", $"-NoProfile -Command \"Get-NetTCPConnection -OwningProcess {pid} -ErrorAction SilentlyContinue | Where-Object {{ $_.RemoteAddress -notin '127.0.0.1','::1','0.0.0.0','::' }} | ForEach-Object {{ $_.RemoteAddress + ':' + $_.RemotePort }}\"")
        { RedirectStandardOutput = true, UseShellExecute = false };
        using var ps = Process.Start(psi)!;
        var output = ps.StandardOutput.ReadToEnd().Trim();
        ps.WaitForExit();
        qa.Evidence.Log("remote TCP connections of the app: " + (output.Length == 0 ? "none" : output));
        if (output.Length > 0) throw new InvalidOperationException("The app holds network connections: " + output);
    });

    [Fact, Step(49)]
    public void S49_Places_dark_theme_and_small_window() => Run(49, "Places: dark theme and small-window screenshots; accessible names", ["places-open"], null, () =>
    {
        OpenSettings();
        App.WaitFor(() => App.TryFind(cf => cf.ByName("Dark").And(cf.ByControlType(ControlType.RadioButton))), "the Dark theme option").Patterns.SelectionItem.Pattern.Select();
        Thread.Sleep(1000);
        OpenPlaces();
        App.Screenshot("49_places_map_dark");
        var r = App.Find("Places_Map").BoundingRectangle;
        var stats = App.PixelStats(new System.Drawing.Rectangle(r.X + 20, r.Y + 20, 120, 80));
        if (stats.MaxLuminance > 120) throw new InvalidOperationException($"The map is not dark in the dark theme: {stats}");
        App.Invoke(Clusters().First());
        var window = App.MainWindow;
        window.Patterns.Window.Pattern.SetWindowVisualState(WindowVisualState.Normal);
        window.Patterns.Transform.Pattern.Resize(900, 640);
        Thread.Sleep(1500);
        App.Screenshot("49_places_small_window_dark");
        var unnamed = App.FindAll(cf => cf.ByControlType(ControlType.Button))
            .Where(b => !b.Properties.IsOffscreen.ValueOrDefault && string.IsNullOrWhiteSpace(b.Properties.Name.ValueOrDefault))
            .Select(b => b.Properties.AutomationId.ValueOrDefault ?? "?").ToList();
        window.Patterns.Window.Pattern.SetWindowVisualState(WindowVisualState.Maximized);
        OpenSettings();
        App.WaitFor(() => App.TryFind(cf => cf.ByName("System").And(cf.ByControlType(ControlType.RadioButton))), "the System theme option").Patterns.SelectionItem.Pattern.Select();
        if (unnamed.Count > 0) throw new InvalidOperationException("Places buttons without an accessible name: " + string.Join(", ", unnamed.Distinct()));
    });
}
