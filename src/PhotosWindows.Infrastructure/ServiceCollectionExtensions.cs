using Microsoft.Extensions.DependencyInjection;
using PhotosWindows.Application.Abstractions;
using PhotosWindows.Application.Backup;
using PhotosWindows.Application.Duplicates;
using PhotosWindows.Application.Editing;
using PhotosWindows.Application.Export;
using PhotosWindows.Application.Import;
using PhotosWindows.Application.Library;
using PhotosWindows.Application.Maintenance;
using PhotosWindows.Application.Organization;
using PhotosWindows.Application.People;
using PhotosWindows.Application.Search;
using PhotosWindows.Application.Thumbnails;
using PhotosWindows.Infrastructure.FileSystem;
using PhotosWindows.Infrastructure.Metadata;
using PhotosWindows.Infrastructure.Persistence;
using PhotosWindows.Infrastructure.Settings;

namespace PhotosWindows.Infrastructure;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers catalog, metadata and application services. Imaging and platform decoders are added separately.</summary>
    public static IServiceCollection AddPhotosWindowsCore(this IServiceCollection services, IAppPaths? paths = null)
    {
        services.AddSingleton<IAppPaths>(paths ?? new DefaultAppPaths());
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<LibraryContext>();
        services.AddSingleton<ILibraryContext>(sp => sp.GetRequiredService<LibraryContext>());
        services.AddSingleton<ISettingsStore, JsonSettingsStore>();
        services.AddSingleton<IFileDeleter, FileDeleter>();

        services.AddSingleton<SqliteDb>();
        services.AddSingleton<ICatalogStore, SqliteCatalogStore>();
        services.AddSingleton<IAssetRepository, AssetRepository>();
        services.AddSingleton<IKeywordRepository, KeywordRepository>();
        services.AddSingleton<IAlbumRepository, AlbumRepository>();
        services.AddSingleton<IImportJobRepository, ImportJobRepository>();
        services.AddSingleton<IEditRepository, EditRepository>();
        services.AddSingleton<IDuplicateDismissalRepository, DuplicateDismissalRepository>();
        services.AddSingleton<IPeopleRepository, PeopleRepository>();
        services.AddSingleton<IMetadataReader, MetadataExtractorReader>();

        services.AddSingleton<LibraryManager>();
        services.AddSingleton<ImportService>();
        services.AddSingleton<ThumbnailService>();
        services.AddSingleton<OrganizationService>();
        services.AddSingleton<SearchService>();
        services.AddSingleton<EditingService>();
        services.AddSingleton<ExportService>();
        services.AddSingleton<DuplicateService>();
        services.AddSingleton<BackupService>();
        services.AddSingleton<LibraryMaintenanceService>();
        services.AddSingleton<PeopleService>();
        return services;
    }
}
