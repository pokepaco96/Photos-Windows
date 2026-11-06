using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PhotosWindows.Application.Abstractions;
using PhotosWindows.Application.Library;

namespace PhotosWindows.Infrastructure.Settings;

/// <summary>Stores <see cref="AppSettings"/> as JSON with atomic replace; a damaged file falls back to defaults (and is kept as *.bad).</summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private readonly IAppPaths _paths;
    private readonly ILogger<JsonSettingsStore> _logger;
    private readonly Lock _lock = new();
    private AppSettings? _cached;

    public JsonSettingsStore(IAppPaths paths, ILogger<JsonSettingsStore> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public AppSettings Load()
    {
        lock (_lock)
        {
            if (_cached is not null) return _cached;
            var path = _paths.SettingsFilePath;
            if (!File.Exists(path)) return _cached = new AppSettings();
            try
            {
                _cached = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options) ?? new AppSettings();
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Settings file is damaged; defaults are used and the file is kept as settings.json.bad");
                File.Copy(path, path + ".bad", overwrite: true);
                _cached = new AppSettings();
            }

            return _cached;
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_lock)
        {
            var path = _paths.SettingsFilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options));
            File.Move(temp, path, overwrite: true);
            _cached = settings;
        }
    }
}

/// <summary>Default per-user locations: %LOCALAPPDATA%\PhotosWindows (or the XDG equivalent on other OSes).</summary>
public sealed class DefaultAppPaths : IAppPaths
{
    public DefaultAppPaths(string? overrideRoot = null)
    {
        AppDataPath = overrideRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "PhotosWindows");
        Directory.CreateDirectory(AppDataPath);
    }

    public string AppDataPath { get; }

    public string LogsPath => Path.Combine(AppDataPath, "logs");

    public string SettingsFilePath => Path.Combine(AppDataPath, "settings.json");
}
