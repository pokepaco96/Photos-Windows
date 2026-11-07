using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PhotosWindows.UIAutomation.Tests;

public enum StepStatus
{
    Passed,
    Failed,
    Blocked,
}

public sealed record StepResult(int Number, string Name, StepStatus Status, double Seconds, string? Error, IReadOnlyList<string> Files);

/// <summary>
/// Evidence folder of one run: screenshots/, trees/ (UI Automation trees), logs/ (app logs + run log),
/// results.json and report.md. Only synthetic data is ever written here.
/// </summary>
public sealed class Evidence
{
    private readonly Lock _lock = new();
    private readonly List<StepResult> _results = [];
    private readonly List<string> _currentFiles = [];
    private int _counter;

    public Evidence(string root)
    {
        Root = root;
        Directory.CreateDirectory(root);
        foreach (var d in new[] { "screenshots", "trees", "logs" }) Directory.CreateDirectory(Path.Combine(root, d));
        RunLog = Path.Combine(root, "logs", "run.log");
    }

    public string Root { get; }

    public string RunLog { get; }

    public IReadOnlyList<StepResult> Results
    {
        get
        {
            lock (_lock) return _results.ToList();
        }
    }

    public void Log(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}";
        lock (_lock) File.AppendAllText(RunLog, line);
    }

    public string NewFile(string folder, string name, string extension)
    {
        string path;
        lock (_lock)
        {
            _counter++;
            var safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '_' : c));
            path = Path.Combine(Root, folder, $"{_counter:000}_{safe}{extension}");
            _currentFiles.Add(Path.GetRelativePath(Root, path).Replace('\\', '/'));
        }

        return path;
    }

    public void BeginStep() 
    {
        lock (_lock) _currentFiles.Clear();
    }

    public void Record(int number, string name, StepStatus status, TimeSpan duration, string? error)
    {
        lock (_lock)
        {
            _results.Add(new StepResult(number, name, status, Math.Round(duration.TotalSeconds, 1), error, _currentFiles.ToList()));
            Log($"STEP {number:00} {name}: {status}{(error is null ? string.Empty : " – " + error)}");
        }
    }

    public void WriteReport(IDictionary<string, string> environment, IEnumerable<string> notes)
    {
        var results = Results.OrderBy(r => r.Number).ToList();
        File.WriteAllText(Path.Combine(Root, "results.json"), JsonSerializer.Serialize(new { environment, results }, new JsonSerializerOptions { WriteIndented = true }));
        var sb = new StringBuilder();
        sb.AppendLine("# Photos Windows – UI automation report").AppendLine();
        sb.AppendLine("| | |").AppendLine("|---|---|");
        foreach (var kv in environment) sb.AppendLine(CultureInfo.InvariantCulture, $"| {kv.Key} | {kv.Value} |");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"**Passed:** {results.Count(r => r.Status == StepStatus.Passed)} · **Failed:** {results.Count(r => r.Status == StepStatus.Failed)} · **Blocked (not executed):** {results.Count(r => r.Status == StepStatus.Blocked)}");
        sb.AppendLine().AppendLine("| # | Scenario | Result | Time (s) | Evidence / error |").AppendLine("|---|---|---|---|---|");
        foreach (var r in results)
        {
            var icon = r.Status switch { StepStatus.Passed => "✅ passed", StepStatus.Failed => "❌ failed", _ => "⛔ blocked" };
            var files = string.Join("<br>", r.Files.Select(f => $"[{Path.GetFileName(f)}]({f})"));
            var err = r.Error is null ? string.Empty : "<br>`" + r.Error.Replace("|", "/", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal) + "`";
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {r.Number} | {r.Name} | {icon} | {r.Seconds} | {files}{err} |");
        }

        var noteList = notes.ToList();
        if (noteList.Count > 0)
        {
            sb.AppendLine().AppendLine("## Notes");
            foreach (var n in noteList) sb.AppendLine("- " + n);
        }

        File.WriteAllText(Path.Combine(Root, "report.md"), sb.ToString());
    }
}
