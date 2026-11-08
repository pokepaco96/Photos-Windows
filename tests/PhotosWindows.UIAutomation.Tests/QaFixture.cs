using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace PhotosWindows.UIAutomation.Tests;

/// <summary>
/// Shared state of one QA run. Everything lives under <see cref="WorkRoot"/> (a fresh temporary folder): dataset,
/// isolated app profile, libraries, exports, backups. Nothing outside it is written or deleted.
/// </summary>
public sealed class QaFixture : IDisposable
{
    private readonly HashSet<string> _achieved = [];
    private readonly List<string> _notes = [];

    public QaFixture()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("UI automation needs Windows with an interactive desktop session.");
        }

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        WorkRoot = Environment.GetEnvironmentVariable("PW_QA_WORK") is { Length: > 0 } w ? Path.GetFullPath(w) : Path.Combine(Path.GetTempPath(), "pwqa-" + stamp);
        if (Directory.Exists(WorkRoot) && Directory.EnumerateFileSystemEntries(WorkRoot).Any())
        {
            throw new InvalidOperationException($"The QA work folder must be empty or new: {WorkRoot}");
        }

        Directory.CreateDirectory(WorkRoot);
        var output = Environment.GetEnvironmentVariable("PW_QA_OUTPUT") is { Length: > 0 } o ? Path.GetFullPath(o) : Path.Combine(RepoRoot(), "artifacts", "qa", stamp);
        Evidence = new Evidence(output);
        ExePath = Environment.GetEnvironmentVariable("PW_APP_EXE") is { Length: > 0 } e ? Path.GetFullPath(e) : Path.Combine(RepoRoot(), "artifacts", "PhotosWindows-win-x64", "PhotosWindows.exe");
        Profile = Path.Combine(WorkRoot, "profile");
        Libraries = Path.Combine(WorkRoot, "libraries");
        Exports = Path.Combine(WorkRoot, "exports");
        Backups = Path.Combine(WorkRoot, "backups");
        Dataset = new QaDataset(Path.Combine(WorkRoot, "dataset"));
        foreach (var d in new[] { Profile, Libraries, Exports, Backups }) Directory.CreateDirectory(d);
        Evidence.Log($"Work root: {WorkRoot}");
        Evidence.Log($"Executable: {ExePath}");
        Dataset.Generate();
        if (!Dataset.HasVideo) Note("No MP4 could be generated (" + Dataset.VideoNote + "); video scenarios are blocked.");
        Evidence.Log($"Dataset: {Dataset.Hashes.Count} files");
    }

    public string WorkRoot { get; }
    public string ExePath { get; }
    public string Profile { get; }
    public string Libraries { get; }
    public string Exports { get; }
    public string Backups { get; }
    public QaDataset Dataset { get; }
    public Evidence Evidence { get; }
    public AppSession? Session { get; private set; }
    public string LibraryName { get; } = "Biblioteca QA";

    /// <summary>Root folder of the library created in the run (inside <see cref="Libraries"/>).</summary>
    public string LibraryRoot => Path.Combine(Libraries, LibraryName + ".pwlibrary");

    public AppSession App => Session ?? throw new InvalidOperationException("The app is not running.");

    public AppSession Launch()
    {
        Session?.Dispose();
        Session = AppSession.Launch(ExePath, Profile, Evidence);
        return Session;
    }

    public void CloseApp()
    {
        Session?.Dispose();
        Session = null;
    }

    public void Achieve(string milestone) => _achieved.Add(milestone);

    public string? Missing(IEnumerable<string> milestones) => milestones.FirstOrDefault(m => !_achieved.Contains(m));

    public void Note(string note)
    {
        _notes.Add(note);
        Evidence.Log("NOTE " + note);
    }

    /// <summary>Guards against any accidental write/delete outside the run's work folder.</summary>
    public string Inside(string path)
    {
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(WorkRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Refusing to use a path outside the QA work folder: {full}");
        }

        return full;
    }

    public void Dispose()
    {
        try
        {
            CloseApp();
        }
        catch (Exception ex)
        {
            Evidence.Log("CloseApp failed during cleanup: " + ex.Message);
        }

        var appLogs = Path.Combine(Profile, "logs");
        if (Directory.Exists(appLogs))
        {
            foreach (var f in Directory.EnumerateFiles(appLogs)) File.Copy(f, Path.Combine(Evidence.Root, "logs", "app-" + Path.GetFileName(f)), overwrite: true);
        }

        File.Copy(Path.Combine(Dataset.Root, "manifest.json"), Path.Combine(Evidence.Root, "dataset-manifest.json"), overwrite: true);
        Evidence.WriteReport(EnvironmentInfo(), _notes);
    }

    private Dictionary<string, string> EnvironmentInfo()
    {
        var info = new Dictionary<string, string>
        {
            ["Date (UTC)"] = DateTime.UtcNow.ToString("u", CultureInfo.InvariantCulture),
            ["OS"] = RuntimeInformation.OSDescription,
            ["Machine"] = Environment.MachineName,
            ["User interactive"] = Environment.UserInteractive.ToString(),
            ["Session id"] = Process.GetCurrentProcess().SessionId.ToString(CultureInfo.InvariantCulture),
            ["Culture"] = CultureInfo.CurrentUICulture.Name,
            ["Commit"] = Environment.GetEnvironmentVariable("GITHUB_SHA") ?? "local",
            ["Executable"] = ExePath,
            ["Work folder"] = WorkRoot,
        };
        foreach (var kv in Extra) info[kv.Key] = kv.Value;
        return info;
    }

    public Dictionary<string, string> Extra { get; } = [];

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PhotosWindows.sln"))) dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }
}
