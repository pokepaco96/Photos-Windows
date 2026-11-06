using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PhotosWindows.Application.Abstractions;
using PhotosWindows.Application.Backup;
using PhotosWindows.Application.Duplicates;
using PhotosWindows.Application.Editing;
using PhotosWindows.Application.Export;
using PhotosWindows.Application.Import;
using PhotosWindows.Application.Library;
using PhotosWindows.Application.Maintenance;
using PhotosWindows.Application.Organization;
using PhotosWindows.Application.Search;
using PhotosWindows.Application.Thumbnails;
using PhotosWindows.Domain;
using PhotosWindows.Imaging;
using PhotosWindows.Infrastructure;
using PhotosWindows.Infrastructure.Settings;

namespace PhotosWindows.Integration.Tests.Support;

public sealed class FakeClock : IClock
{
    public DateTime UtcNow { get; set; } = new(2025, 3, 15, 10, 0, 0, DateTimeKind.Utc);

    public TimeZoneInfo LocalZone { get; } = TimeZoneInfo.CreateCustomTimeZone("test+1", TimeSpan.FromHours(1), "test+1", "test+1");
}

/// <summary>A complete, isolated application stack (real SQLite, real files) in a temporary folder.</summary>
public sealed class TestHost : IAsyncDisposable
{
    private readonly ServiceProvider _provider;

    public TestHost()
    {
        Root = Path.Combine(Path.GetTempPath(), "pw-it-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Sources);
        Directory.CreateDirectory(Libraries);
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddPhotosWindowsCore(new DefaultAppPaths(Path.Combine(Root, "appdata")));
        services.AddSingleton<IClock>(Clock);
        services.AddPhotosWindowsImaging();
        _provider = services.BuildServiceProvider();
    }

    public string Root { get; }
    public string Sources => Path.Combine(Root, "sources");
    public string Libraries => Path.Combine(Root, "libraries");
    public FakeClock Clock { get; } = new();

    public T Get<T>() where T : notnull => _provider.GetRequiredService<T>();

    public LibraryManager Libs => Get<LibraryManager>();
    public ImportService Import => Get<ImportService>();
    public IAssetRepository Assets => Get<IAssetRepository>();
    public OrganizationService Organization => Get<OrganizationService>();
    public SearchService Search => Get<SearchService>();
    public EditingService Editing => Get<EditingService>();
    public ExportService Export => Get<ExportService>();
    public ThumbnailService Thumbnails => Get<ThumbnailService>();
    public BackupService Backup => Get<BackupService>();
    public DuplicateService Duplicates => Get<DuplicateService>();
    public LibraryMaintenanceService Maintenance => Get<LibraryMaintenanceService>();
    public IImportJobRepository Jobs => Get<IImportJobRepository>();

    public Task<LibraryInfo> CreateLibraryAsync(string name = "Test") => Libs.CreateAsync(Libraries, name, CancellationToken.None);

    public string SourceDir(string name)
    {
        var dir = Path.Combine(Sources, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public Task<ImportResult> ImportAsync(string source, ImportMode mode, bool duplicates = false) =>
        Import.ImportAsync(new ImportRequest([source], mode, duplicates), null, CancellationToken.None);

    public Task<IReadOnlyList<AssetSummary>> AllAsync(LibraryScope scope = LibraryScope.All) =>
        Assets.QueryAsync(new AssetQuery { Scope = scope, Sort = AssetSort.FileNameAscending }, CancellationToken.None);

    public async ValueTask DisposeAsync()
    {
        Libs.Close();
        await _provider.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
                return;
            }
            catch (IOException)
            {
                await Task.Delay(100);
            }
        }
    }
}
