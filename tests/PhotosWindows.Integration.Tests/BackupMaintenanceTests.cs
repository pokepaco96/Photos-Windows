using PhotosWindows.Application.Abstractions;
using PhotosWindows.Application.Duplicates;
using PhotosWindows.Domain;
using PhotosWindows.Integration.Tests.Support;
using SkiaSharp;

namespace PhotosWindows.Integration.Tests;

public class BackupMaintenanceTests
{
    [Fact]
    public async Task Backup_is_verified_tamper_is_detected_and_restore_creates_a_new_library()
    {
        await using var host = new TestHost();
        var lib = await host.CreateLibraryAsync("Family");
        Fixtures.Jpeg(Path.Combine(host.SourceDir("b"), "a.jpg"), new Fixtures.PhotoSpec { Seed = 1 });
        Fixtures.Jpeg(Path.Combine(host.SourceDir("b"), "b.jpg"), new Fixtures.PhotoSpec { Seed = 2 });
        await host.ImportAsync(Path.Combine(host.Sources, "b"), ImportMode.CopyToLibrary);
        var albumId = await host.Organization.CreateAlbumAsync("Kept", null, (await host.AllAsync()).Select(a => a.Id).ToList(), CancellationToken.None);

        var backupRoot = Path.Combine(host.Root, "backups");
        var backup = await host.Backup.CreateBackupAsync(backupRoot, includeManagedOriginals: true, null, CancellationToken.None);
        Assert.False(backup.EndsWith(".partial", StringComparison.Ordinal));
        var verification = await host.Backup.VerifyAsync(backup, CancellationToken.None);
        Assert.True(verification.IsValid, string.Join(";", verification.Problems));
        Assert.Equal(2, verification.Manifest!.AssetCount);
        Assert.True(verification.Manifest.IncludesManagedOriginals);
        Assert.Contains(verification.Manifest.Files, f => f.Path.StartsWith("originals/", StringComparison.Ordinal));
        Assert.Single(PhotosWindows.Application.Backup.BackupService.FindBackups(backupRoot));

        var restored = await host.Backup.RestoreAsNewLibraryAsync(backup, host.Libraries, CancellationToken.None);
        Assert.NotEqual(lib.RootPath, restored.RootPath);
        Assert.NotEqual(lib.Id, restored.Id);
        var assets = await host.AllAsync();
        Assert.Equal(2, assets.Count);
        Assert.All(assets, a => Assert.True(File.Exists(restored.Resolve(host.Assets.GetOriginalFileAsync(a.Id, CancellationToken.None).Result!))));
        Assert.Equal(2, (await host.Organization.GetAlbumAsync(albumId, CancellationToken.None))!.ItemCount);

        // Tampering is detected.
        var originalFile = Directory.EnumerateFiles(Path.Combine(backup, "originals"), "*", SearchOption.AllDirectories).First();
        await File.AppendAllTextAsync(originalFile, "x");
        var tampered = await host.Backup.VerifyAsync(backup, CancellationToken.None);
        Assert.False(tampered.IsValid);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Backup.RestoreAsNewLibraryAsync(backup, host.Libraries, CancellationToken.None));
    }

    [Fact]
    public async Task Folder_without_manifest_is_not_a_valid_backup()
    {
        await using var host = new TestHost();
        await host.CreateLibraryAsync();
        var fake = Directory.CreateDirectory(Path.Combine(host.Root, "PhotosWindows-Backup-x")).FullName;
        Assert.False((await host.Backup.VerifyAsync(fake, CancellationToken.None)).IsValid);
        Assert.Empty(PhotosWindows.Application.Backup.BackupService.FindBackups(host.Root));
    }

    [Fact]
    public async Task Catalog_restore_in_place_rolls_back_catalog_changes_and_keeps_a_safety_copy()
    {
        await using var host = new TestHost();
        var lib = await host.CreateLibraryAsync();
        Fixtures.Jpeg(Path.Combine(host.SourceDir("c"), "a.jpg"));
        await host.ImportAsync(Path.Combine(host.Sources, "c"), ImportMode.AddInPlace);
        var backup = await host.Backup.CreateBackupAsync(Path.Combine(host.Root, "bk"), false, null, CancellationToken.None);
        await host.Organization.CreateAlbumAsync("After backup", null, null, CancellationToken.None);

        await host.Backup.RestoreCatalogInPlaceAsync(backup, CancellationToken.None);

        Assert.Empty(await host.Organization.GetAlbumsAsync(CancellationToken.None));
        Assert.Single(await host.AllAsync());
        Assert.Single(Directory.EnumerateFiles(lib.BackupsPath, "replaced-*.db"));
    }

    [Fact]
    public async Task Verification_detects_missing_reappeared_orphan_and_partial_files_and_can_repair()
    {
        await using var host = new TestHost();
        var lib = await host.CreateLibraryAsync();
        var referenced = Fixtures.Jpeg(Path.Combine(host.SourceDir("ref"), "r.jpg"), new Fixtures.PhotoSpec { Seed = 3 });
        await host.ImportAsync(Path.Combine(host.Sources, "ref"), ImportMode.AddInPlace);
        var orphan = Fixtures.Jpeg(Path.Combine(lib.OriginalsPath, "2020", "01", "orphan.jpg"), new Fixtures.PhotoSpec { Seed = 4 });
        var partial = Path.Combine(lib.OriginalsPath, "2020", "01", "x.jpg.pwpartial");
        await File.WriteAllTextAsync(partial, "incomplete");

        var moved = Path.Combine(host.SourceDir("moved"), "deeper", "r.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
        File.Move(referenced, moved);

        var report = await host.Maintenance.VerifyAsync(verifyContentHashes: true, null, CancellationToken.None);
        Assert.False(report.IsHealthy);
        Assert.Single(report.MissingFiles);
        Assert.Equal([orphan], report.OrphanManagedFiles);
        Assert.Equal([partial], report.PartialFiles);
        Assert.False((await host.AllAsync()).Single().IsAvailable);

        Assert.Equal(1, await host.Maintenance.RelinkMissingAsync(report.MissingFiles, Path.Combine(host.Sources, "moved"), CancellationToken.None));
        Assert.True((await host.AllAsync()).Single().IsAvailable);
        Assert.Equal(1, host.Maintenance.CleanPartialFiles(report));
        var adopted = await host.Maintenance.AdoptOrphansAsync(report, null, CancellationToken.None);
        Assert.Single(adopted.NewAssetIds);
        Assert.Equal(StorageMode.Managed, (await host.Assets.GetOriginalFileAsync(adopted.NewAssetIds[0], CancellationToken.None))!.StorageMode);

        var clean = await host.Maintenance.VerifyAsync(false, null, CancellationToken.None);
        Assert.True(clean.IsHealthy, $"{clean.MissingFiles.Count} {clean.OrphanManagedFiles.Count} {clean.PartialFiles.Count} {clean.ChangedFiles.Count}");
        await host.Maintenance.RebuildSearchIndexAsync(CancellationToken.None);
        Assert.Single(await host.Search.SearchAsync("orphan", null, CancellationToken.None));
    }

    [Fact]
    public async Task Reappearing_drive_restores_availability()
    {
        await using var host = new TestHost();
        await host.CreateLibraryAsync();
        var f = Fixtures.Jpeg(Path.Combine(host.SourceDir("usb"), "a.jpg"));
        await host.ImportAsync(Path.Combine(host.Sources, "usb"), ImportMode.AddInPlace);
        File.Move(f, f + ".away");
        await host.Maintenance.VerifyAsync(false, null, CancellationToken.None);
        Assert.False((await host.AllAsync()).Single().IsAvailable);
        File.Move(f + ".away", f);
        var report = await host.Maintenance.VerifyAsync(false, null, CancellationToken.None);
        Assert.Single(report.ReappearedFiles);
        Assert.True((await host.AllAsync()).Single().IsAvailable);
    }

    [Fact]
    public async Task Duplicates_exact_and_similar_are_suggested_resolved_and_dismissed()
    {
        await using var host = new TestHost();
        await host.CreateLibraryAsync();
        var dir = host.SourceDir("d");
        var a = Fixtures.Jpeg(Path.Combine(dir, "a.jpg"), new Fixtures.PhotoSpec { Seed = 10, Width = 400, Height = 300 });
        using (var big = Fixtures.Pattern(400, 300, 10))
        using (var small = big.Resize(new SKImageInfo(200, 150), new SKSamplingOptions(SKFilterMode.Linear)))
        using (var img = SKImage.FromBitmap(small))
        {
            File.WriteAllBytes(Path.Combine(dir, "a_small.jpg"), img.Encode(SKEncodedImageFormat.Jpeg, 70).ToArray());
        }

        Fixtures.Jpeg(Path.Combine(dir, "unrelated.jpg"), new Fixtures.PhotoSpec { Seed = 99, Width = 400, Height = 300 });
        await host.ImportAsync(dir, ImportMode.AddInPlace);
        var exactDir = host.SourceDir("exact");
        File.Copy(a, Path.Combine(exactDir, "a_copy.jpg"));
        await host.ImportAsync(exactDir, ImportMode.AddInPlace, duplicates: true);
        while (await host.Thumbnails.ProcessPendingAsync(50, CancellationToken.None) > 0) { }

        var groups = await host.Duplicates.FindAsync(includeSimilar: true, DuplicateService.DefaultSimilarityThreshold, CancellationToken.None);
        var exact = Assert.Single(groups, g => g.Kind == DuplicateKind.Exact);
        Assert.Equal(2, exact.Assets.Count);
        var similar = groups.Where(g => g.Kind == DuplicateKind.Similar).ToList();
        Assert.Contains(similar, g => g.Assets.Any(x => x.FileName == "a_small.jpg"));
        Assert.DoesNotContain(groups, g => g.Assets.Any(x => x.FileName == "unrelated.jpg"));

        await host.Duplicates.ResolveAsync(exact, exact.Assets[0].Id, CancellationToken.None);
        Assert.Single(await host.AllAsync(LibraryScope.RecentlyDeleted));
        Assert.True(File.Exists(a));

        var remaining = await host.Duplicates.FindAsync(true, DuplicateService.DefaultSimilarityThreshold, CancellationToken.None);
        foreach (var g in remaining) await host.Duplicates.DismissAsync(g, CancellationToken.None);
        Assert.Empty(await host.Duplicates.FindAsync(true, DuplicateService.DefaultSimilarityThreshold, CancellationToken.None));
    }
}
