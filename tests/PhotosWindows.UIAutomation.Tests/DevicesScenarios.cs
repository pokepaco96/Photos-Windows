using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

namespace PhotosWindows.UIAutomation.Tests;

/// <summary>
/// Devices page through the real interface with SIMULATED devices (folders): one that can delete and one that
/// cannot. This exercises the flows and their safeguards; it is not evidence that any real phone is supported.
/// </summary>
public sealed partial class EndToEndTests
{
    private string SimDevice => Path.Combine(qa.WorkRoot, "sim-device");
    private string SimReadOnly => SimDevice + "-readonly";

    // Static: xUnit creates a new test-class instance per step.
    private static Dictionary<string, string>? _deviceHashes;

    private void OpenDevices(bool refresh = true)
    {
        Nav("Nav_Devices");
        App.WaitUntil(() => App.TextOf("Devices_Title") is "Devices" or "Dispositivos", "the Devices page");
        if (refresh) App.Click("Devices_Refresh");
        App.WaitUntil(() => App.TryFindById("Devices_Loading") is not { } r || IsRingIdle(r), "the device list to load", TimeSpan.FromSeconds(30));
        Thread.Sleep(600);
    }

    private void SelectDevice(string name)
    {
        var combo = App.Find("Devices_Device").AsComboBox();
        combo.Expand();
        Thread.Sleep(300);
        combo.Select(name);
        combo.Collapse();
        App.WaitUntil(() => App.TextOf("Devices_Status").Contains(name, StringComparison.Ordinal), $"the device '{name}' to open", TimeSpan.FromSeconds(20));
        Thread.Sleep(500);
    }

    private AutomationElement DeviceTile(string file) => App.Find("DeviceTile_DCIM/100APPLE/" + file, TimeSpan.FromSeconds(20));

    private string DeviceResult()
    {
        App.WaitUntil(() => App.Exists("Devices_Result") && !App.Exists("Devices_Cancel"), "the device import to finish", TimeSpan.FromMinutes(2));
        Thread.Sleep(400);
        return AppSession.AllText(App.Find("Devices_Result"));
    }

    private Dictionary<string, string> HashDevices() =>
        Directory.EnumerateFiles(SimDevice, "*", SearchOption.AllDirectories).Concat(Directory.EnumerateFiles(SimReadOnly, "*", SearchOption.AllDirectories))
            .ToDictionary(f => f, QaDataset.Sha256);

    [Fact, Step(70)]
    public void S70_Devices_help_then_detection() => Run(70, "Devices: help when nothing is connected; simulated devices are detected and listed", ["library"], "devices-ready", () =>
    {
        OpenDevices();
        if (!App.TextOf("Devices_Status").Contains("No device found", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected status with no device: " + App.TextOf("Devices_Status"));
        var help = AppSession.AllText(App.Find("Devices_Help"));
        foreach (var expected in new[] { "USB cable", "Unlock", "Trust", "Apple Devices", "iCloud", "Keep Originals" })
        {
            if (!help.Contains(expected, StringComparison.Ordinal)) throw new InvalidOperationException($"The help does not mention '{expected}'.");
        }

        App.Screenshot("70_devices_none");
        var seed = 700;
        foreach (var (root, names) in new[] { (SimDevice, new[] { "IMG_1001.JPG", "IMG_1002.JPG", "IMG_1003.JPG" }), (SimReadOnly, new[] { "IMG_2001.JPG", "IMG_2002.JPG" }) })
        {
            var folder = qa.Inside(Path.Combine(root, "DCIM", "100APPLE"));
            Directory.CreateDirectory(folder);
            foreach (var name in names) QaDataset.Jpeg(Path.Combine(folder, name), seed++, new DateTime(2025, 2, 1, 10, 0, 0).AddDays(seed), null, null, null, []);
        }

        _deviceHashes = HashDevices();
        App.Click("Devices_Refresh");
        App.WaitUntil(() => App.TextOf("Devices_Status").Contains("Simulated device (QA)", StringComparison.Ordinal), "the simulated device", TimeSpan.FromSeconds(20));
        foreach (var name in new[] { "IMG_1001.JPG", "IMG_1002.JPG", "IMG_1003.JPG" }) DeviceTile(name);
        if (!App.TextOf("Devices_Summary").Contains("3 photos", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected summary: " + App.TextOf("Devices_Summary"));
        App.Screenshot("70_devices_listed");
    });

    [Fact, Step(71)]
    public void S71_Devices_explore_without_importing() => Run(71, "Devices: explore and preview without importing; library and device untouched", ["devices-ready"], null, () =>
    {
        OpenDevices();
        SelectDevice("Simulated read-only device (QA)");
        DeviceTile("IMG_2001.JPG").Patterns.SelectionItem.Pattern.Select();
        App.WaitUntil(() => App.Exists("Devices_PreviewTitle") && App.TextOf("Devices_PreviewTitle") == "IMG_2001.JPG", "the preview of the selected item");
        Thread.Sleep(1200);
        App.Screenshot("71_devices_preview");
        Nav("Nav_All");
        if (TileExists("IMG_2001.JPG")) throw new InvalidOperationException("Exploring the device added an item to the library.");
        var now = HashDevices();
        if (now.Count != _deviceHashes!.Count || now.Any(kv => _deviceHashes[kv.Key] != kv.Value)) throw new InvalidOperationException("Exploring changed the device.");
    });

    [Fact, Step(72)]
    public void S72_Devices_import_only() => Run(72, "Devices: import only; delete option disabled with its explanation; device untouched", ["devices-ready"], "devices-imported", () =>
    {
        OpenDevices();
        SelectDevice("Simulated read-only device (QA)");
        if (App.Find("Devices_ModeDelete").IsEnabled) throw new InvalidOperationException("The delete option is enabled on a device that cannot delete.");
        if (!App.TextOf("Devices_DeleteUnavailable").Contains("not available", StringComparison.Ordinal)) throw new InvalidOperationException("The explanation for the disabled delete option is missing.");
        if (App.Find("Devices_ImportSelection").IsEnabled) throw new InvalidOperationException("'Import selection' is enabled with nothing selected.");
        DeviceTile("IMG_2001.JPG").Patterns.SelectionItem.Pattern.Select();
        App.WaitUntil(() => App.Find("Devices_ImportSelection").IsEnabled, "import enabled after selecting");
        App.Screenshot("72_devices_delete_disabled");
        App.Click("Devices_ImportSelection");
        var first = DeviceResult();
        qa.Evidence.Log("import selection: " + first);
        if (!first.Contains("1 imported", StringComparison.Ordinal) || !first.Contains("Nothing was changed on the device", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected result: " + first);
        App.Click("Devices_ImportAll");
        var second = DeviceResult();
        qa.Evidence.Log("import all: " + second);
        if (!second.Contains("1 imported", StringComparison.Ordinal) || !second.Contains("1 already in the library", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected result: " + second);
        var now = HashDevices();
        if (now.Count != _deviceHashes!.Count || now.Any(kv => _deviceHashes[kv.Key] != kv.Value)) throw new InvalidOperationException("'Import only' changed the device.");
        Nav("Nav_All");
        App.WaitUntil(() => TileExists("IMG_2001.JPG") && TileExists("IMG_2002.JPG"), "the imported items in the library", TimeSpan.FromSeconds(20));
        var managed = ManagedCopy("IMG_2001.JPG");
        if (QaDataset.Sha256(managed) != _deviceHashes.Single(kv => kv.Key.EndsWith("IMG_2001.JPG", StringComparison.Ordinal)).Value) throw new InvalidOperationException("The managed copy is not identical to the item on the device.");
    });

    [Fact, Step(73)]
    public void S73_Devices_import_and_delete() => Run(73, "Devices: import and delete (simulated device) only after confirmation and verification", ["devices-ready"], null, () =>
    {
        OpenDevices();
        SelectDevice("Simulated device (QA)");
        App.SetChecked("Devices_ModeDelete", true);
        DeviceTile("IMG_1001.JPG").Patterns.SelectionItem.Pattern.Select();

        // Declining the confirmation does nothing at all.
        App.Click("Devices_ImportSelection");
        App.Screenshot("73_devices_delete_confirmation");
        if (!App.DialogMessage().Contains("only after its copy has been verified", StringComparison.Ordinal)) throw new InvalidOperationException("The confirmation does not explain the deletion: " + App.DialogMessage());
        App.DialogButton("CloseButton");
        Thread.Sleep(800);
        if (!File.Exists(Path.Combine(SimDevice, "DCIM", "100APPLE", "IMG_1001.JPG"))) throw new InvalidOperationException("Declining the confirmation deleted the item.");
        if (App.Exists("Devices_Result")) throw new InvalidOperationException("Declining the confirmation still ran an import.");

        App.Click("Devices_ImportSelection");
        App.DialogButton("PrimaryButton");
        var result = DeviceResult();
        qa.Evidence.Log("import and delete: " + result);
        if (!result.Contains("1 imported", StringComparison.Ordinal) || !result.Contains("1 deleted from the device", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected result: " + result);
        var folder = Path.Combine(SimDevice, "DCIM", "100APPLE");
        if (File.Exists(Path.Combine(folder, "IMG_1001.JPG"))) throw new InvalidOperationException("The verified item is still on the device.");
        foreach (var kept in new[] { "IMG_1002.JPG", "IMG_1003.JPG" })
        {
            var path = Path.Combine(folder, kept);
            if (!File.Exists(path) || QaDataset.Sha256(path) != _deviceHashes![path]) throw new InvalidOperationException("An item that was not selected changed: " + kept);
        }

        // The only remaining copy is the one in the library, and it is identical to what the device had.
        var managed = ManagedCopy("IMG_1001.JPG");
        if (QaDataset.Sha256(managed) != _deviceHashes!.Single(kv => kv.Key.EndsWith("IMG_1001.JPG", StringComparison.Ordinal)).Value) throw new InvalidOperationException("The library copy differs from the deleted original.");
        App.WaitUntil(() => App.TextOf("Devices_Summary").Contains("2 photos", StringComparison.Ordinal), "the device list refreshed after deleting", TimeSpan.FromSeconds(20));
        App.Screenshot("73_devices_after_delete");
    });

    [Fact, Step(74)]
    public void S74_Devices_dark_small_window_and_names() => Run(74, "Devices: dark theme, small window and accessible names", ["devices-ready"], null, () =>
    {
        OpenSettings();
        App.WaitFor(() => App.TryFind(cf => cf.ByName("Dark").And(cf.ByControlType(ControlType.RadioButton))), "the Dark theme option").Patterns.SelectionItem.Pattern.Select();
        Thread.Sleep(1000);
        OpenDevices();
        App.Screenshot("74_devices_dark");
        var window = App.MainWindow;
        window.Patterns.Window.Pattern.SetWindowVisualState(WindowVisualState.Normal);
        window.Patterns.Transform.Pattern.Resize(900, 640);
        Thread.Sleep(1500);
        App.Screenshot("74_devices_small_window_dark");
        var unnamed = App.FindAll(cf => cf.ByControlType(ControlType.Button))
            .Where(b => !b.Properties.IsOffscreen.ValueOrDefault && string.IsNullOrWhiteSpace(b.Properties.Name.ValueOrDefault))
            .Select(b => b.Properties.AutomationId.ValueOrDefault ?? "?").ToList();
        window.Patterns.Window.Pattern.SetWindowVisualState(WindowVisualState.Maximized);
        OpenSettings();
        App.WaitFor(() => App.TryFind(cf => cf.ByName("System").And(cf.ByControlType(ControlType.RadioButton))), "the System theme option").Patterns.SelectionItem.Pattern.Select();
        if (unnamed.Count > 0) throw new InvalidOperationException("Devices buttons without an accessible name: " + string.Join(", ", unnamed.Distinct()));
    });
}
