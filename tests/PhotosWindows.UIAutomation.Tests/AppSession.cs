using System.Diagnostics;
using System.Text;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;

namespace PhotosWindows.UIAutomation.Tests;

/// <summary>
/// One running instance of the real PhotosWindows.exe, driven through Windows UI Automation (UIA3).
/// The app is started with PHOTOSWINDOWS_DATA_DIR pointing to an isolated profile, so the user's real settings,
/// recent libraries and logs are never touched.
/// </summary>
public sealed class AppSession : IDisposable
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);
    private readonly UIA3Automation _automation = new();
    private readonly Evidence _evidence;

    private AppSession(FlaUI.Core.Application app, Window window, Evidence evidence)
    {
        App = app;
        MainWindow = window;
        _evidence = evidence;
    }

    public FlaUI.Core.Application App { get; }

    public Window MainWindow { get; private set; }

    public UIA3Automation Automation => _automation;

    public static AppSession Launch(string exePath, string profileDir, Evidence evidence)
    {
        if (!File.Exists(exePath)) throw new FileNotFoundException("PhotosWindows.exe not found. Set PW_APP_EXE.", exePath);
        var psi = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exePath)!,
        };
        psi.Environment["PHOTOSWINDOWS_DATA_DIR"] = profileDir;
        // A folder next to the profile plays the role of a connected device for the Devices scenarios (QA hook).
        psi.Environment["PW_QA_SIMULATED_DEVICE"] = Path.Combine(Path.GetDirectoryName(profileDir)!, "sim-device");
        psi.Environment["PW_QA_SIMULATED_DEVICE_DELETE"] = "1";
        var app = FlaUI.Core.Application.Launch(psi);
        var automation = new UIA3Automation();
        Window? window = null;
        var sw = Stopwatch.StartNew();
        while (window is null && sw.Elapsed < TimeSpan.FromSeconds(60))
        {
            if (app.HasExited) throw new InvalidOperationException($"PhotosWindows exited during start-up (exit code {app.ExitCode}).");
            try
            {
                window = app.GetMainWindow(automation, TimeSpan.FromSeconds(2));
            }
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                window = null;
            }
        }

        automation.Dispose();
        if (window is null) throw new TimeoutException("The main window did not appear within 60 s.");
        var session = new AppSession(app, window, evidence);
        session.MainWindow = app.GetMainWindow(session._automation, TimeSpan.FromSeconds(10))
            ?? throw new TimeoutException("The main window could not be re-acquired.");
        session.MainWindow.SetForeground();
        evidence.Log($"Launched PID {app.ProcessId}, window '{session.MainWindow.Title}' {session.MainWindow.BoundingRectangle}");
        return session;
    }

    // ---------------- Finding ----------------

    /// <summary>All top-level windows of the app process (main window, popups, flyouts, OS file dialogs).</summary>
    public IEnumerable<AutomationElement> Roots()
    {
        AutomationElement[] windows;
        try
        {
            windows = App.GetAllTopLevelWindows(_automation);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            windows = [];
        }

        // Main window first; popups (flyouts, dialogs) after.
        return windows.OrderBy(w => w.Properties.NativeWindowHandle.ValueOrDefault == MainWindow.Properties.NativeWindowHandle.ValueOrDefault ? 0 : 1);
    }

    public AutomationElement? TryFind(Func<ConditionFactory, ConditionBase> condition)
    {
        foreach (var root in Roots())
        {
            try
            {
                var found = root.FindFirstDescendant(condition);
                if (found is not null) return found;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // Element went away while searching; try next root / next poll.
            }
        }

        return null;
    }

    public AutomationElement? TryFindById(string automationId) => TryFind(cf => cf.ByAutomationId(automationId));

    public AutomationElement Find(string automationId, TimeSpan? timeout = null) =>
        WaitFor(() => Visible(BringIntoView(TryFindById(automationId))), $"element '{automationId}'", timeout);

    /// <summary>
    /// Elements below the fold of a ScrollViewer are in the tree but offscreen (UI run #12: the backup section of
    /// Settings). Scroll them into view like a user would: ScrollItem if supported, else keyboard focus.
    /// </summary>
    private static AutomationElement? BringIntoView(AutomationElement? e)
    {
        if (e is null) return null;
        try
        {
            if (!e.Properties.IsOffscreen.ValueOrDefault) return e;
            if (e.Patterns.ScrollItem.PatternOrDefault is { } scroll) scroll.ScrollIntoView();
            else if (e.Properties.IsKeyboardFocusable.ValueOrDefault && e.Properties.IsEnabled.ValueOrDefault) e.Focus();
            Thread.Sleep(300);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or FlaUI.Core.Exceptions.PropertyNotSupportedException or FlaUI.Core.Exceptions.PatternNotSupportedException)
        {
            // Best effort; the caller keeps polling.
        }

        return e;
    }

    public AutomationElement FindByName(string name, TimeSpan? timeout = null) =>
        WaitFor(() => Visible(TryFind(cf => cf.ByName(name))), $"element named '{name}'", timeout);

    public IReadOnlyList<AutomationElement> FindAll(Func<ConditionFactory, ConditionBase> condition) =>
        Roots().SelectMany(r =>
        {
            try
            {
                return r.FindAllDescendants(condition);
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                return [];
            }
        }).ToList();

    private static AutomationElement? Visible(AutomationElement? e)
    {
        if (e is null) return null;
        try
        {
            return e.Properties.IsOffscreen.ValueOrDefault ? null : e;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    public T WaitFor<T>(Func<T?> probe, string what, TimeSpan? timeout = null) where T : class
    {
        var limit = timeout ?? DefaultTimeout;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < limit)
        {
            ThrowIfCrashed();
            var value = probe();
            if (value is not null) return value;
            Thread.Sleep(200);
        }

        throw new TimeoutException($"Timed out after {limit.TotalSeconds:0} s waiting for {what}.");
    }

    public void WaitUntil(Func<bool> condition, string what, TimeSpan? timeout = null) =>
        WaitFor(() => condition() ? "ok" : null, what, timeout);

    public bool Exists(string automationId) => Visible(TryFindById(automationId)) is not null;

    // ---------------- Acting ----------------

    public void Click(string automationId, TimeSpan? timeout = null)
    {
        var e = Find(automationId, timeout);
        Invoke(e);
        _evidence.Log($"click {automationId}");
    }

    /// <summary>Uses the Invoke pattern when available (robust), otherwise a real mouse click.</summary>
    public void Invoke(AutomationElement e)
    {
        ThrowIfCrashed();
        if (e.Patterns.Invoke.IsSupported)
        {
            e.Patterns.Invoke.Pattern.Invoke();
        }
        else if (e.Patterns.SelectionItem.IsSupported)
        {
            e.Patterns.SelectionItem.Pattern.Select();
        }
        else if (e.Patterns.Toggle.IsSupported)
        {
            e.Patterns.Toggle.Pattern.Toggle();
        }
        else
        {
            e.Click();
        }

        Wait.UntilInputIsProcessed();
        Thread.Sleep(250);
    }

    public void MouseClick(AutomationElement e)
    {
        e.Click(moveMouse: true);
        Wait.UntilInputIsProcessed();
        Thread.Sleep(250);
    }

    public void SetText(string automationId, string text)
    {
        var e = Find(automationId);
        if (e.Patterns.Value.IsSupported)
        {
            e.Patterns.Value.Pattern.SetValue(text);
        }
        else
        {
            e.Focus();
            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
            Keyboard.Type(text);
        }

        Wait.UntilInputIsProcessed();
        _evidence.Log($"text {automationId} = '{text}'");
    }

    public string TextOf(string automationId)
    {
        var e = Find(automationId);
        return TextOf(e);
    }

    public static string TextOf(AutomationElement e)
    {
        if (e.Patterns.Value.IsSupported) return e.Patterns.Value.Pattern.Value.ValueOrDefault ?? string.Empty;
        if (e.Patterns.Text.IsSupported) return e.Patterns.Text.Pattern.DocumentRange.GetText(-1);
        return e.Properties.Name.ValueOrDefault ?? string.Empty;
    }

    /// <summary>All visible text inside an element (InfoBar messages, dialogs...).</summary>
    public static string AllText(AutomationElement e)
    {
        // Elements can vanish or change while the text is read (e.g. an InfoBar updating): never throw here.
        var sb = new StringBuilder(e.Properties.Name.ValueOrDefault);
        foreach (var t in e.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)))
        {
            sb.Append(" | ").Append(t.Properties.Name.ValueOrDefault);
        }

        return sb.ToString();
    }

    public void SetChecked(string automationId, bool value)
    {
        var e = Find(automationId);
        if (e.Patterns.Toggle.IsSupported)
        {
            if ((e.Patterns.Toggle.Pattern.ToggleState.Value == ToggleState.On) != value) e.Patterns.Toggle.Pattern.Toggle();
        }
        else if (e.Patterns.SelectionItem.IsSupported && value)
        {
            e.Patterns.SelectionItem.Pattern.Select();
        }

        Wait.UntilInputIsProcessed();
    }

    public void SelectComboIndex(string automationId, int index)
    {
        var combo = Find(automationId).AsComboBox();
        combo.Expand();
        Thread.Sleep(300);
        combo.Select(index);
        combo.Collapse();
        Wait.UntilInputIsProcessed();
        Thread.Sleep(300);
    }

    public void SetSlider(string automationId, double value)
    {
        var e = Find(automationId);
        e.Patterns.RangeValue.Pattern.SetValue(value);
        Wait.UntilInputIsProcessed();
        Thread.Sleep(400);
        _evidence.Log($"slider {automationId} = {value}");
    }

    public double SliderValue(string automationId) => Find(automationId).Patterns.RangeValue.Pattern.Value.Value;

    /// <summary>Presses and releases keys (a chord when several). Keyboard.Press alone never released the key.</summary>
    public static void Keys(params VirtualKeyShort[] keys)
    {
        if (keys.Length == 1) Keyboard.Type(keys[0]);
        else Keyboard.TypeSimultaneously(keys);
        Wait.UntilInputIsProcessed();
        Thread.Sleep(250);
    }

    /// <summary>Clicks a ContentDialog button (template parts PrimaryButton / SecondaryButton / CloseButton).</summary>
    public void DialogButton(string part = "PrimaryButton")
    {
        var e = Find(part);
        Invoke(e);
        _evidence.Log($"dialog {part}");
        Thread.Sleep(400);
    }

    public string DialogMessage() => TextOf(Find("DialogMessage"));

    /// <summary>
    /// Drives the Windows common "Select folder" dialog (an OS window, not part of the app): types the path into the
    /// folder box, presses Enter, and clicks the dialog's default button if it is still open.
    /// </summary>
    public void PickFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        var dialog = WaitFor(FindFileDialog, "the folder picker dialog", TimeSpan.FromSeconds(30));
        _evidence.Log($"folder dialog '{dialog.Properties.Name.ValueOrDefault}' → {folder}");
        var edits = dialog.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit));
        var box = edits.FirstOrDefault(e => e.AutomationId is "1152" or "1148" or "1001")
                  ?? edits.LastOrDefault(e => !e.Properties.IsOffscreen.ValueOrDefault)
                  ?? throw new InvalidOperationException("No text box found in the folder dialog.");
        box.Focus();
        if (box.Patterns.Value.IsSupported) box.Patterns.Value.Pattern.SetValue(folder);
        else Keyboard.Type(folder);
        Keyboard.Type(VirtualKeyShort.RETURN);
        Thread.Sleep(1200);
        if (FindFileDialog() is not null)
        {
            var ok = dialog.FindFirstDescendant(cf => cf.ByAutomationId("1").And(cf.ByControlType(ControlType.Button)));
            if (ok is not null) ok.Patterns.Invoke.Pattern.Invoke();
        }

        WaitUntil(() => FindFileDialog() is null, "the folder picker to close", TimeSpan.FromSeconds(20));
        Thread.Sleep(500);
    }

    /// <summary>The Windows common file dialog (#32770). It is owned by the main window, so it may appear as its child.</summary>
    public AutomationElement? FindFileDialog() =>
        Roots().FirstOrDefault(w => w.ClassName == "#32770")
        ?? TryFind(cf => cf.ByClassName("#32770").And(cf.ByControlType(ControlType.Window)));

    /// <summary>Best-effort return to a usable state after a failed scenario: cancel OS dialogs and app dialogs.</summary>
    public void Recover()
    {
        for (var i = 0; i < 3 && !App.HasExited; i++)
        {
            if (FindFileDialog() is { } dialog && dialog.FindFirstDescendant(cf => cf.ByAutomationId("2").And(cf.ByControlType(ControlType.Button))) is { } cancel)
            {
                cancel.Patterns.Invoke.PatternOrDefault?.Invoke();
                Thread.Sleep(800);
                continue;
            }

            if (TryFindById("CloseButton") is { } close && !close.Properties.IsOffscreen.ValueOrDefault && close.Patterns.Invoke.IsSupported)
            {
                close.Patterns.Invoke.Pattern.Invoke();
                Thread.Sleep(600);
                continue;
            }

            Keyboard.Type(VirtualKeyShort.ESCAPE);
            Thread.Sleep(400);
        }
    }

    // ---------------- Health & evidence ----------------

    private readonly DateTime _launchedLocal = DateTime.Now;
    private bool _crashReported;

    public void ThrowIfCrashed()
    {
        if (!App.HasExited) return;
        // ExitCode is not always readable (the process object may not own the process): never let that hide the crash.
        string code;
        try
        {
            code = "0x" + App.ExitCode.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (InvalidOperationException)
        {
            code = "unknown";
        }

        if (!_crashReported)
        {
            _crashReported = true;
            _evidence.Log($"APP EXITED (exit code {code}). Windows Application event log since launch:");
            _evidence.Log(ApplicationEventLog(_launchedLocal));
        }

        throw new InvalidOperationException($"PhotosWindows crashed or exited unexpectedly (exit code {code}).");
    }

    /// <summary>.NET Runtime, Application Error and WER entries since <paramref name="since"/>: the cause of a native crash.</summary>
    private static string ApplicationEventLog(DateTime since)
    {
        try
        {
            var script = "Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = [datetime]'" + since.ToString("s", System.Globalization.CultureInfo.InvariantCulture) + "' } -ErrorAction SilentlyContinue | " +
                "Where-Object { $_.ProviderName -in 'Application Error', '.NET Runtime', 'Windows Error Reporting', 'Application Hang' } | " +
                "Select-Object -First 6 | ForEach-Object { '--- ' + $_.TimeCreated + ' ' + $_.ProviderName + ' ' + $_.Id + [Environment]::NewLine + $_.Message }";
            var psi = new System.Diagnostics.ProcessStartInfo("powershell", "-NoProfile -Command \"" + script.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var ps = System.Diagnostics.Process.Start(psi)!;
            var text = ps.StandardOutput.ReadToEnd();
            ps.WaitForExit(30000);
            return text.Length == 0 ? "(no entries)" : text.Length > 12000 ? text[..12000] : text;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return "(event log unavailable: " + ex.Message + ")";
        }
    }

    /// <summary>Fails when the app shows its unexpected-error dialog ("Something went wrong").</summary>
    public void AssertNoErrorDialog()
    {
        ThrowIfCrashed();
        var error = TryFind(cf => cf.ByName("Something went wrong").Or(cf.ByName("Algo salió mal")));
        if (error is not null)
        {
            var text = TryFindById("DialogMessage") is { } m ? TextOf(m) : string.Empty;
            throw new InvalidOperationException("The app reported an unexpected error: " + text);
        }
    }

    /// <summary>
    /// Darkest and brightest luminance (0-255) around the system Close caption button (top-right of the window).
    /// Used to check that the caption glyphs contrast with the app theme (WinUI draws them with the OS theme
    /// unless the app sets the title bar button colours).
    /// </summary>
    public (int Min, int Max) CaptionButtonLuminance()
    {
        var r = MainWindow.BoundingRectangle;
        var stats = PixelStats(new System.Drawing.Rectangle(r.Right - 44, r.Top + 6, 38, 20));
        _evidence.Log($"caption button luminance: min {stats.MinLuminance}, max {stats.MaxLuminance}");
        return (stats.MinLuminance, stats.MaxLuminance);
    }

    /// <summary>Luminance range and highest saturation (0-255) of a screen rectangle.</summary>
    public (int MinLuminance, int MaxLuminance, int MaxSaturation) PixelStats(System.Drawing.Rectangle area)
    {
        MainWindow.SetForeground();
        Thread.Sleep(300);
        using var image = Capture.Rectangle(area);
        var bitmap = image.Bitmap;
        int min = 255, max = 0, sat = 0;
        for (var x = 0; x < bitmap.Width; x++)
        {
            for (var y = 0; y < bitmap.Height; y++)
            {
                var c = bitmap.GetPixel(x, y);
                var l = (int)Math.Round(0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B);
                min = Math.Min(min, l);
                max = Math.Max(max, l);
                sat = Math.Max(sat, Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B)));
            }
        }

        return (min, max, sat);
    }

    public string Screenshot(string name)
    {
        var path = _evidence.NewFile("screenshots", name, ".png");
        try
        {
            MainWindow.SetForeground();
            Thread.Sleep(200);
            Capture.Screen().ToFile(path);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException)
        {
            _evidence.Log("screenshot failed: " + ex.Message);
        }

        return path;
    }

    public string DumpTree(string name)
    {
        var path = _evidence.NewFile("trees", name, ".txt");
        var sb = new StringBuilder();
        foreach (var root in Roots())
        {
            Dump(root, 0, sb);
        }

        File.WriteAllText(path, sb.ToString());
        return path;
    }

    private static void Dump(AutomationElement e, int depth, StringBuilder sb)
    {
        if (depth > 40) return;
        try
        {
            sb.Append(new string(' ', depth * 2))
              .Append(e.ControlType).Append(" | id=").Append(e.Properties.AutomationId.ValueOrDefault)
              .Append(" | name=").Append(e.Properties.Name.ValueOrDefault)
              .Append(" | class=").Append(e.Properties.ClassName.ValueOrDefault)
              .Append(e.Properties.IsOffscreen.ValueOrDefault ? " | offscreen" : string.Empty)
              .Append(e.Properties.IsEnabled.ValueOrDefault ? string.Empty : " | disabled")
              .Append(" | ").Append(e.Properties.BoundingRectangle.ValueOrDefault)
              .AppendLine();
            foreach (var child in e.FindAllChildren()) Dump(child, depth + 1, sb);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            sb.AppendLine(new string(' ', depth * 2) + "<element vanished>");
        }
    }

    public void Close()
    {
        if (App.HasExited) return;
        try
        {
            MainWindow.Close();
            if (!WaitForExit(TimeSpan.FromSeconds(15)))
            {
                _evidence.Log("App did not exit after closing the window; killing it.");
                App.Kill();
            }
        }
        catch (Exception ex)
        {
            // Closing can fail while a modal dialog disables the window; never let cleanup fail the run.
            _evidence.Log($"Close failed ({ex.GetType().Name}); killing the app.");
            if (!App.HasExited) App.Kill();
        }
    }

    public bool WaitForExit(TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (App.HasExited) return true;
            Thread.Sleep(200);
        }

        return App.HasExited;
    }

    public void Dispose()
    {
        Close();
        App.Dispose();
        _automation.Dispose();
    }
}
