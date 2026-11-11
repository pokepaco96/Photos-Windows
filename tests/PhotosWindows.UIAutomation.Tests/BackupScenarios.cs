using System.Text.Json;
using FlaUI.Core.Definitions;

namespace PhotosWindows.UIAutomation.Tests;

/// <summary>
/// Originals backup through the real interface: destination chosen with the real folder picker, preflight, a complete
/// verified backup checked on disk against its manifest, resuming an unfinished one, verifying an existing one,
/// and the incomplete case when an original is unavailable. Cancelling mid-copy is not exercised here (the synthetic
/// library copies in well under a second); it is covered by the integration tests.
/// </summary>
public sealed partial class EndToEndTests
{
    private string BackupDestination => Path.Combine(qa.WorkRoot, "originals-backup-dest");

    // Static: xUnit creates a new test-class instance per step.
    private static string? _backupFolder;

    private void OpenBackup()
    {
        Nav("Nav_Backup");
        App.WaitUntil(() => App.TextOf("BackupPage_Title") is "Backup" or "Copia de seguridad", "the Backup page");
        App.WaitUntil(() => App.TextOf("Backup_Analysis").Contains("files", StringComparison.Ordinal), "the analysis of the library", TimeSpan.FromSeconds(30));
        Thread.Sleep(500);
    }

    private string BackupResult(TimeSpan? timeout = null)
    {
        App.WaitUntil(() => App.Exists("Backup_Result") && !App.Exists("Backup_Cancel"), "the backup to finish", timeout ?? TimeSpan.FromMinutes(3));
        Thread.Sleep(400);
        return AppSession.AllText(App.Find("Backup_Result"));
    }

    private static (bool Complete, int FileCount, List<(string Path, long Size, string Sha256)> Files) ReadBackupManifest(string folder)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "backup-manifest.json")));
        var root = doc.RootElement;
        var files = root.GetProperty("files").EnumerateArray()
            .Select(f => (f.GetProperty("path").GetString()!, f.GetProperty("size").GetInt64(), f.GetProperty("sha256").GetString()!)).ToList();
        return (root.GetProperty("complete").GetBoolean(), root.GetProperty("fileCount").GetInt32(), files);
    }

    [Fact, Step(60)]
    public void S60_Backup_destination_and_preflight() => Run(60, "Backup: choose the destination with the folder picker; preflight shows files, bytes and free space", ["library"], "backup-ready", () =>
    {
        Directory.CreateDirectory(qa.Inside(BackupDestination));
        OpenBackup();
        App.Click("Backup_ChooseDestination");
        App.PickFolder(BackupDestination);
        App.WaitUntil(() => App.TextOf("Backup_Destination") == BackupDestination, "the chosen destination on the page", TimeSpan.FromSeconds(20));
        App.WaitUntil(() => App.TextOf("Backup_Space").Contains("free", StringComparison.Ordinal), "free space of the destination", TimeSpan.FromSeconds(20));
        qa.Evidence.Log("analysis: " + App.TextOf("Backup_Analysis"));
        qa.Evidence.Log("space: " + App.TextOf("Backup_Space"));
        if (App.Exists("Backup_Warning")) qa.Evidence.Log("warning: " + AppSession.AllText(App.Find("Backup_Warning")));
        if (App.Exists("Backup_Blocker")) throw new InvalidOperationException("The backup is blocked: " + AppSession.AllText(App.Find("Backup_Blocker")));
        if (!App.Find("Backup_Start").IsEnabled) throw new InvalidOperationException("The start button is disabled although nothing blocks the backup.");
        // The two kinds of backup must be told apart on the page.
        App.Find("Backup_OriginalsHeader");
        App.Find("Backup_LibraryHeader");
        App.Screenshot("60_backup_preflight");
    });

    [Fact, Step(61)]
    public void S61_Backup_complete_and_verified_on_disk() => Run(61, "Backup: complete backup of originals; YEAR/MONTH tree and SHA-256 checked on disk", ["backup-ready"], "backup-done", () =>
    {
        OpenBackup();
        App.Click("Backup_Start");
        var result = BackupResult();
        qa.Evidence.Log("result: " + result);
        if (!result.Contains("Backup completed and verified", StringComparison.Ordinal)) throw new InvalidOperationException("The backup did not complete: " + result);
        App.Screenshot("61_backup_complete");

        var folder = Directory.EnumerateDirectories(BackupDestination, "PhotosWindows-Originales-Backup-*").Single();
        if (folder.EndsWith(".partial", StringComparison.Ordinal) || folder.EndsWith("-INCOMPLETA", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected folder name: " + folder);
        _backupFolder = folder;
        var (complete, count, files) = ReadBackupManifest(folder);
        if (!complete) throw new InvalidOperationException("The manifest does not say complete.");
        var onDisk = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/')).Where(p => p != "backup-manifest.json").ToList();
        if (count != files.Count || onDisk.Count != files.Count) throw new InvalidOperationException($"Manifest lists {files.Count} files (fileCount {count}); {onDisk.Count} on disk.");
        foreach (var (path, size, sha) in files)
        {
            var parts = path.Split('/');
            var dated = parts.Length == 3 && parts[0].Length == 4 && parts[0].All(char.IsDigit) && parts[1].Length == 2 && int.Parse(parts[1]) is >= 1 and <= 12;
            var undated = parts.Length == 3 && parts[0] == "Sin fecha" && parts[1] == "00";
            if (!dated && !undated) throw new InvalidOperationException("Not a YEAR/MONTH path: " + path);
            var full = Path.Combine(folder, path);
            if (new FileInfo(full).Length != size || QaDataset.Sha256(full) != sha) throw new InvalidOperationException("Copy differs from its manifest entry: " + path);
        }

        // Originals added without moving are included, byte for byte: compare with the source that is still on disk.
        var playa = Path.Combine(qa.Dataset.InPlaceFolder, "Playa 2021.jpg");
        var copy = files.Single(f => f.Path.EndsWith("/Playa 2021.jpg", StringComparison.Ordinal));
        if (copy.Path != "2021/07/Playa 2021.jpg") throw new InvalidOperationException("Unexpected folder for a July 2021 photo: " + copy.Path);
        if (copy.Sha256 != QaDataset.Sha256(playa)) throw new InvalidOperationException("The referenced original was not copied byte for byte.");
        qa.Evidence.Log($"backup on disk: {files.Count} files, {files.Sum(f => f.Size)} bytes, folders: {string.Join(", ", files.Select(f => f.Path[..f.Path.LastIndexOf('/')]).Distinct().Order())}");
        if (!App.Find("Backup_OpenFolder").IsEnabled) throw new InvalidOperationException("'Open backup folder' is not available after a complete backup.");
    });

    [Fact, Step(62)]
    public void S62_Backup_resume_unfinished() => Run(62, "Backup: an unfinished backup is offered for resuming and completes after re-verification", ["backup-done"], null, () =>
    {
        // Turn the finished backup into an unfinished one, as an interrupted run leaves it: working name, manifest not
        // complete, two files missing and one damaged.
        var folder = _backupFolder!;
        var (_, _, files) = ReadBackupManifest(folder);
        var partial = folder + ".partial";
        Directory.Move(folder, partial);
        var manifestPath = Path.Combine(partial, "backup-manifest.json");
        File.WriteAllText(manifestPath, File.ReadAllText(manifestPath).Replace("\"complete\": true", "\"complete\": false", StringComparison.Ordinal));
        File.Delete(Path.Combine(partial, files[0].Path));
        File.Delete(Path.Combine(partial, files[1].Path));
        File.WriteAllText(Path.Combine(partial, files[2].Path), "damaged");

        Nav("Nav_All");
        OpenBackup();
        App.Find("Backup_Resume", TimeSpan.FromSeconds(20));
        App.Screenshot("62_backup_resume_offered");
        App.Click("Backup_Resume");
        var result = BackupResult();
        qa.Evidence.Log("resume result: " + result);
        if (!result.Contains("Backup completed and verified", StringComparison.Ordinal)) throw new InvalidOperationException("Resuming did not complete the backup: " + result);
        if (!Directory.Exists(folder) || Directory.Exists(partial)) throw new InvalidOperationException("The folder did not get its final name after resuming.");
        foreach (var (path, size, sha) in files.Take(3))
        {
            var full = Path.Combine(folder, path);
            if (!File.Exists(full) || new FileInfo(full).Length != size || QaDataset.Sha256(full) != sha) throw new InvalidOperationException("Not restored correctly: " + path);
        }
    });

    [Fact, Step(63)]
    public void S63_Backup_verify_existing() => Run(63, "Backup: verify an existing backup (intact, then tampered)", ["backup-done"], null, () =>
    {
        OpenBackup();
        App.Click("Backup_VerifyExisting");
        App.PickFolder(_backupFolder!);
        var ok = BackupResult();
        if (!ok.Contains("The backup is intact", StringComparison.Ordinal)) throw new InvalidOperationException("An intact backup did not verify: " + ok);

        var (_, _, files) = ReadBackupManifest(_backupFolder!);
        var victim = Path.Combine(_backupFolder!, files[^1].Path);
        var original = File.ReadAllBytes(victim);
        File.AppendAllText(victim, "x");
        try
        {
            App.Click("Backup_VerifyExisting");
            App.PickFolder(_backupFolder!);
            App.WaitUntil(() => AppSession.AllText(App.Find("Backup_Result")).Contains("has problems", StringComparison.Ordinal), "the tampered backup to be reported", TimeSpan.FromMinutes(1));
            var problems = AppSession.AllText(App.Find("Backup_Problems"));
            if (!problems.Contains("Size differs", StringComparison.Ordinal)) throw new InvalidOperationException("The changed file is not listed: " + problems);
            App.Screenshot("63_backup_verify_problems");
        }
        finally
        {
            File.WriteAllBytes(victim, original);
        }
    });

    [Fact, Step(64)]
    public void S64_Backup_unavailable_original() => Run(64, "Backup: an unavailable original blocks a complete backup; the accepted export is marked incomplete", ["backup-done"], null, () =>
    {
        var source = Path.Combine(qa.Dataset.InPlaceFolder, "Nieve_2019.jpg");
        var away = source + ".offline";
        File.Move(source, away); // as if its drive were disconnected
        try
        {
            Nav("Nav_All");
            OpenBackup();
            App.WaitUntil(() => App.Exists("Backup_Warning"), "the warning about unavailable originals", TimeSpan.FromSeconds(20));
            var warning = AppSession.AllText(App.Find("Backup_Warning"));
            if (!warning.Contains("1 original files are not available", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected warning: " + warning);
            // Regression (UI run #43): the previous step's verification result was still shown here.
            if (App.Exists("Backup_Result")) throw new InvalidOperationException("A previous result is still shown after coming back to the page.");
            if (!AppSession.AllText(App.Find("Backup_Problems")).Contains("Nieve_2019.jpg", StringComparison.Ordinal)) throw new InvalidOperationException("The missing file is not named.");
            if (App.Find("Backup_Start").IsEnabled) throw new InvalidOperationException("A complete backup can be started although an original is missing.");
            App.Screenshot("64_backup_unavailable");
            App.SetChecked("Backup_AcceptIncomplete", true);
            App.WaitUntil(() => App.Find("Backup_Start").IsEnabled, "start enabled after accepting an incomplete export");
            App.Click("Backup_Start");
            var result = BackupResult();
            qa.Evidence.Log("incomplete result: " + result);
            if (!result.Contains("Incomplete backup", StringComparison.Ordinal)) throw new InvalidOperationException("The export without a file was not presented as incomplete: " + result);
            var folder = Directory.EnumerateDirectories(BackupDestination, "*-INCOMPLETA").Single();
            if (ReadBackupManifest(folder).Complete) throw new InvalidOperationException("An incomplete export says complete in its manifest.");
        }
        finally
        {
            File.Move(away, source);
        }
    });

    [Fact, Step(65)]
    public void S65_Backup_dark_small_window_and_names() => Run(65, "Backup: dark theme, small window and accessible names", ["backup-ready"], null, () =>
    {
        OpenSettings();
        App.WaitFor(() => App.TryFind(cf => cf.ByName("Dark").And(cf.ByControlType(ControlType.RadioButton))), "the Dark theme option").Patterns.SelectionItem.Pattern.Select();
        Thread.Sleep(1000);
        OpenBackup();
        // Regression (UI run #43): a result banner opened in the light theme kept light colours in dark.
        App.Click("Backup_VerifyExisting");
        App.PickFolder(_backupFolder!);
        BackupResult();
        App.Screenshot("65_backup_dark");
        var banner = App.Find("Backup_Result").BoundingRectangle;
        var stats = App.PixelStats(new System.Drawing.Rectangle(banner.X + banner.Width / 2, banner.Y + 4, Math.Min(200, banner.Width / 3), Math.Max(8, banner.Height / 3)));
        qa.Evidence.Log($"dark banner pixels: {stats}");
        if (stats.MinLuminance > 120) throw new InvalidOperationException($"The result banner is light in the dark theme: {stats}");
        var window = App.MainWindow;
        window.Patterns.Window.Pattern.SetWindowVisualState(WindowVisualState.Normal);
        window.Patterns.Transform.Pattern.Resize(900, 640);
        Thread.Sleep(1500);
        App.Screenshot("65_backup_small_window_dark");
        var unnamed = App.FindAll(cf => cf.ByControlType(ControlType.Button))
            .Where(b => !b.Properties.IsOffscreen.ValueOrDefault && string.IsNullOrWhiteSpace(b.Properties.Name.ValueOrDefault))
            .Select(b => b.Properties.AutomationId.ValueOrDefault ?? "?").ToList();
        window.Patterns.Window.Pattern.SetWindowVisualState(WindowVisualState.Maximized);
        OpenSettings();
        App.WaitFor(() => App.TryFind(cf => cf.ByName("System").And(cf.ByControlType(ControlType.RadioButton))), "the System theme option").Patterns.SelectionItem.Pattern.Select();
        if (unnamed.Count > 0) throw new InvalidOperationException("Backup buttons without an accessible name: " + string.Join(", ", unnamed.Distinct()));
    });
}
