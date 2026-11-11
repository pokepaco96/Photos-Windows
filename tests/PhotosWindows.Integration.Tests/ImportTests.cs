using PhotosWindows.Application.Import;
using PhotosWindows.Domain;
using PhotosWindows.Integration.Tests.Support;

namespace PhotosWindows.Integration.Tests;

public class ImportTests
{
    [Fact]
    public async Task Add_in_place_indexes_without_touching_originals()
    {
        await using var host = new TestHost();
        await host.CreateLibraryAsync();
        var dir = host.SourceDir("trip");
        var a = Fixtures.Jpeg(Path.Combine(dir, "IMG_0001.jpg"), new Fixtures.PhotoSpec { Location = new GeoLocation(40.4168, -3.7038, 650), Seed = 1 });
        var b = Fixtures.Png(Path.Combine(dir, "sub", "drawing.png"), seed: 2);
        var before = new[] { a, b }.Select(f => (Fixtures.Sha256(f), File.GetLastWriteTimeUtc(f))).ToList();

        var result = await host.ImportAsync(dir, ImportMode.AddInPlace);

        Assert.Equal(ImportJobStatus.Completed, result.Status);
        Assert.Equal(2, result.NewAssetIds.Count);
        Assert.Equal(before, new[] { a, b }.Select(f => (Fixtures.Sha256(f), File.GetLastWriteTimeUtc(f))).ToList());
        Assert.Empty(Directory.EnumerateFiles(host.Get<PhotosWindows.Application.Abstractions.ILibraryContext>().Current!.OriginalsPath, "*", SearchOption.AllDirectories));

        var jpg = (await host.AllAsync()).Single(s => s.FileName == "IMG_0001.jpg");
        var asset = (await host.Assets.GetAsync(jpg.Id, CancellationToken.None))!;
        Assert.Equal(new DateTime(2023, 7, 14, 18, 30, 5), asset.CaptureDate.LocalDateTime);
        Assert.Equal(TimeSpan.FromHours(2), asset.CaptureDate.UtcOffset);
        Assert.Equal(DateSource.ExifOriginal, asset.CaptureDate.Source);
        Assert.Equal("Acme Z100", asset.Camera.Model);
        Assert.Equal("Acme 24-70mm", asset.Camera.Lens);
        Assert.Equal(2.8, asset.Camera.FNumber!.Value, 2);
        Assert.Equal(1 / 250.0, asset.Camera.ExposureTimeSeconds!.Value, 6);
        Assert.Equal(200, asset.Camera.Iso);
        Assert.Equal(40.4168, asset.Location!.Value.Latitude, 3);
        Assert.Equal(-3.7038, asset.Location!.Value.Longitude, 3);
        Assert.Equal(650, asset.Location!.Value.Altitude!.Value, 1);
        Assert.Equal((120, 80), (asset.PixelWidth, asset.PixelHeight));

        var file = (await host.Assets.GetOriginalFileAsync(jpg.Id, CancellationToken.None))!;
        Assert.Equal(StorageMode.Referenced, file.StorageMode);
        Assert.Equal(Path.GetFullPath(a), file.StoredPath);
        Assert.Equal(Fixtures.Sha256(a), file.Sha256);
    }

    [Fact]
    public async Task Copy_to_library_creates_managed_copies_by_capture_month_and_keeps_sources()
    {
        await using var host = new TestHost();
        var lib = await host.CreateLibraryAsync();
        var dir = host.SourceDir("card");
        var src = Fixtures.Jpeg(Path.Combine(dir, "DSC_1.jpg"));
        var hash = Fixtures.Sha256(src);

        var result = await host.ImportAsync(dir, ImportMode.CopyToLibrary);

        Assert.Single(result.NewAssetIds);
        Assert.True(File.Exists(src));
        Assert.Equal(hash, Fixtures.Sha256(src));
        var file = (await host.Assets.GetOriginalFileAsync(result.NewAssetIds[0], CancellationToken.None))!;
        Assert.Equal(StorageMode.Managed, file.StorageMode);
        Assert.False(Path.IsPathRooted(file.StoredPath));
        var copy = lib.Resolve(file);
        Assert.Equal(Path.Combine(lib.OriginalsPath, "2023", "07", "DSC_1.jpg"), copy);
        Assert.Equal(hash, Fixtures.Sha256(copy));
        Assert.Equal(File.GetLastWriteTimeUtc(src), File.GetLastWriteTimeUtc(copy));
        Assert.Empty(Directory.EnumerateFiles(lib.OriginalsPath, "*.pwpartial", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Name_conflicts_in_managed_folder_never_overwrite()
    {
        await using var host = new TestHost();
        var lib = await host.CreateLibraryAsync();
        Fixtures.Jpeg(Path.Combine(host.SourceDir("a"), "IMG.jpg"), new Fixtures.PhotoSpec { Seed = 1 });
        Fixtures.Jpeg(Path.Combine(host.SourceDir("b"), "IMG.jpg"), new Fixtures.PhotoSpec { Seed = 2 });
        await host.ImportAsync(Path.Combine(host.Sources, "a"), ImportMode.CopyToLibrary);
        await host.ImportAsync(Path.Combine(host.Sources, "b"), ImportMode.CopyToLibrary);
        var names = Directory.EnumerateFiles(Path.Combine(lib.OriginalsPath, "2023", "07")).Select(Path.GetFileName).Order().ToList();
        Assert.Equal(["IMG (1).jpg", "IMG.jpg"], names);
    }

    [Fact]
    public async Task Reimporting_the_same_folder_and_identical_copies_are_detected()
    {
        await using var host = new TestHost();
        await host.CreateLibraryAsync();
        var dir = host.SourceDir("x");
        var f = Fixtures.Jpeg(Path.Combine(dir, "a.jpg"));
        await host.ImportAsync(dir, ImportMode.AddInPlace);

        var again = await host.ImportAsync(dir, ImportMode.AddInPlace);
        Assert.Equal(0, again.Imported);
        Assert.Equal(1, again.Duplicates);
        var items = await host.Jobs.GetItemsAsync(again.JobId, null, CancellationToken.None);
        Assert.Equal(ImportItemStatus.AlreadyInLibrary, items.Single().Status);

        var copyDir = host.SourceDir("copy");
        File.Copy(f, Path.Combine(copyDir, "renamed.jpg"));
        var dup = await host.ImportAsync(copyDir, ImportMode.CopyToLibrary);
        Assert.Equal(ImportItemStatus.Duplicate, (await host.Jobs.GetItemsAsync(dup.JobId, null, CancellationToken.None)).Single().Status);
        Assert.Single(await host.AllAsync());
        Assert.Empty(Directory.EnumerateFiles(host.Get<PhotosWindows.Application.Abstractions.ILibraryContext>().Current!.OriginalsPath, "*", SearchOption.AllDirectories));

        var forced = await host.ImportAsync(copyDir, ImportMode.AddInPlace, duplicates: true);
        Assert.Single(forced.NewAssetIds);
        Assert.Equal(2, (await host.AllAsync()).Count);
    }

    [Fact]
    public async Task Identical_files_in_the_same_batch_are_imported_once()
    {
        await using var host = new TestHost();
        await host.CreateLibraryAsync();
        var dir = host.SourceDir("batch");
        var f = Fixtures.Jpeg(Path.Combine(dir, "a.jpg"));
        File.Copy(f, Path.Combine(dir, "b.jpg"));
        File.Copy(f, Path.Combine(dir, "c.jpg"));
        var result = await host.ImportAsync(dir, ImportMode.AddInPlace);
        Assert.Equal(1, result.Imported);
        Assert.Equal(2, result.Duplicates);
    }

    [Fact]
    public async Task Corrupt_empty_and_disguised_files_are_reported_without_stopping_the_job()
    {
        await using var host = new TestHost();
        await host.CreateLibraryAsync();
        var dir = host.SourceDir("mixed");
        Fixtures.Jpeg(Path.Combine(dir, "good.jpg"));
        await File.WriteAllTextAsync(Path.Combine(dir, "fake.jpg"), "not really a jpeg");
        await File.WriteAllBytesAsync(Path.Combine(dir, "empty.png"), []);
        Fixtures.Png(Path.Combine(dir, "png-named.jpg"));
        await File.WriteAllTextAsync(Path.Combine(dir, "notes.txt"), "ignored");

        var result = await host.ImportAsync(dir, ImportMode.AddInPlace);

        Assert.Equal(1, result.Imported);
        Assert.Equal(3, result.Skipped);
        var items = await host.Jobs.GetItemsAsync(result.JobId, null, CancellationToken.None);
        Assert.Equal(4, items.Count); // notes.txt is not a candidate
        Assert.All(items.Where(i => i.Status == ImportItemStatus.Unsupported), i => Assert.False(string.IsNullOrEmpty(i.Message)));
    }

    [Fact]
    public async Task Raw_pair_live_clip_and_sidecar_are_grouped_into_one_asset()
    {
        await using var host = new TestHost();
        var lib = await host.CreateLibraryAsync();
        var dir = host.SourceDir("pairs");
        Fixtures.Jpeg(Path.Combine(dir, "IMG_0100.JPG"));
        Fixtures.FakeRaw(Path.Combine(dir, "IMG_0100.CR2"));
        Fixtures.QuickTime(Path.Combine(dir, "IMG_0100.MOV"), TimeSpan.FromSeconds(2.5), new DateTime(2023, 7, 14, 16, 30, 5, DateTimeKind.Utc));
        Fixtures.Xmp(Path.Combine(dir, "IMG_0100.xmp"), "Sunset", ["beach", "family"], rating: 4);

        var result = await host.ImportAsync(dir, ImportMode.CopyToLibrary);

        Assert.Single(result.NewAssetIds);
        var summary = (await host.AllAsync()).Single();
        Assert.True(summary.HasRawPair);
        Assert.True(summary.HasLiveVideo);
        var files = await host.Assets.GetFilesAsync(summary.Id, CancellationToken.None);
        Assert.Equal([FileRole.Original, FileRole.RawPair, FileRole.LiveVideo, FileRole.Sidecar], files.Select(f => f.Role).ToArray());
        Assert.All(files, f => Assert.True(File.Exists(lib.Resolve(f))));
        var asset = (await host.Assets.GetAsync(summary.Id, CancellationToken.None))!;
        Assert.Equal("Sunset", asset.Title);
        Assert.Equal(4, asset.Rating);
        Assert.Equal(["beach", "family"], await host.Organization.GetKeywordsForAsync(summary.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Long_video_with_same_name_is_imported_as_its_own_asset()
    {
        await using var host = new TestHost();
        await host.CreateLibraryAsync();
        var dir = host.SourceDir("vid");
        Fixtures.Jpeg(Path.Combine(dir, "IMG_0200.jpg"));
        Fixtures.QuickTime(Path.Combine(dir, "IMG_0200.mov"), TimeSpan.FromSeconds(42), new DateTime(2022, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        var result = await host.ImportAsync(dir, ImportMode.AddInPlace);
        Assert.Equal(2, result.NewAssetIds.Count);
        var video = (await host.AllAsync(LibraryScope.Videos)).Single();
        Assert.Equal(TimeSpan.FromSeconds(42), video.Duration);
        var asset = (await host.Assets.GetAsync(video.Id, CancellationToken.None))!;
        Assert.Equal(DateSource.QuickTime, asset.CaptureDate.Source);
        Assert.Equal(new DateTime(2022, 1, 2, 4, 4, 5), asset.CaptureDate.LocalDateTime); // test zone is UTC+1
    }

    [Fact]
    public async Task Exif_orientation_is_recorded_and_display_size_is_swapped()
    {
        await using var host = new TestHost();
        await host.CreateLibraryAsync();
        var dir = host.SourceDir("rot");
        Fixtures.Jpeg(Path.Combine(dir, "portrait.jpg"), new Fixtures.PhotoSpec { Orientation = 6 });
        await host.ImportAsync(dir, ImportMode.AddInPlace);
        var s = (await host.AllAsync()).Single();
        Assert.Equal((80, 120), (s.DisplayWidth, s.DisplayHeight));
    }

    [Fact]
    public async Task Photo_without_metadata_falls_back_to_file_name_date()
    {
        await using var host = new TestHost();
        await host.CreateLibraryAsync();
        var dir = host.SourceDir("names");
        Fixtures.Jpeg(Path.Combine(dir, "IMG_20190305_101112.jpg"), new Fixtures.PhotoSpec { Taken = null, Make = null, Model = null, Lens = null });
        await host.ImportAsync(dir, ImportMode.AddInPlace);
        var asset = (await host.Assets.GetAsync((await host.AllAsync()).Single().Id, CancellationToken.None))!;
        Assert.Equal(DateSource.FileName, asset.CaptureDate.Source);
        Assert.Equal(new DateTime(2019, 3, 5, 10, 11, 12), asset.CaptureDate.LocalDateTime);
    }

    [Fact]
    public async Task Cancelled_import_can_be_resumed_without_duplicating_work()
    {
        await using var host = new TestHost();
        await host.CreateLibraryAsync();
        var dir = host.SourceDir("many");
        for (var i = 0; i < 12; i++) Fixtures.Jpeg(Path.Combine(dir, $"p{i:00}.jpg"), new Fixtures.PhotoSpec { Seed = i });

        using var cts = new CancellationTokenSource();
        // Progress reports are throttled, so cancel from the per-asset event instead.
        var committed = 0;
        void OnImported(object? s, long id) { if (Interlocked.Increment(ref committed) >= 3) cts.Cancel(); }
        host.Import.AssetImported += OnImported;
        var first = await host.Import.ImportAsync(new ImportRequest([dir], ImportMode.CopyToLibrary), null, cts.Token);
        host.Import.AssetImported -= OnImported;
        Assert.Equal(ImportJobStatus.Cancelled, first.Status);
        Assert.InRange(first.Imported, 1, 11);
        var pending = await host.Jobs.GetItemsAsync(first.JobId, ImportItemStatus.Pending, CancellationToken.None);
        Assert.NotEmpty(pending);
        Assert.Empty(Directory.EnumerateFiles(host.Get<PhotosWindows.Application.Abstractions.ILibraryContext>().Current!.OriginalsPath, "*.pwpartial", SearchOption.AllDirectories));

        var resumed = await host.Import.ResumeAsync(first.JobId, null, CancellationToken.None);
        Assert.Equal(ImportJobStatus.Completed, resumed.Status);
        Assert.Equal(12, (await host.AllAsync()).Count);
        var job = (await host.Jobs.GetJobAsync(first.JobId, CancellationToken.None))!;
        Assert.Equal(12, job.Imported);
        Assert.Equal(12, job.Processed);
    }

    [Fact]
    public async Task Interrupted_jobs_are_marked_on_open_and_missing_sources_fail_cleanly()
    {
        await using var host = new TestHost();
        var lib = await host.CreateLibraryAsync();
        var gone = Path.Combine(host.Sources, "gone.jpg");
        var jobId = await host.Jobs.CreateJobAsync(ImportMode.AddInPlace, false, [gone], host.Clock.UtcNow, CancellationToken.None);
        await host.Jobs.SetJobStatusAsync(jobId, ImportJobStatus.Running, null, CancellationToken.None);

        host.Libs.Close();
        await host.Libs.OpenAsync(lib.RootPath, CancellationToken.None);
        Assert.Equal(ImportJobStatus.Interrupted, (await host.Jobs.GetJobAsync(jobId, CancellationToken.None))!.Status);

        var result = await host.Import.ResumeAsync(jobId, null, CancellationToken.None);
        Assert.Equal(ImportJobStatus.CompletedWithErrors, result.Status);
        var item = (await host.Jobs.GetItemsAsync(jobId, null, CancellationToken.None)).Single();
        Assert.Equal(ImportItemStatus.Failed, item.Status);
        Assert.Contains("no longer available", item.Message);
    }

    [Fact]
    public async Task Scan_skips_the_library_cache_and_unsupported_files()
    {
        await using var host = new TestHost();
        var lib = await host.CreateLibraryAsync();
        Fixtures.Jpeg(Path.Combine(lib.CachePath, "thumbs", "x.jpg"));
        Fixtures.Jpeg(Path.Combine(host.SourceDir("s"), "a.jpg"));
        await File.WriteAllTextAsync(Path.Combine(host.Sources, "s", "readme.md"), "x");
        var files = host.Import.Scan([host.Sources, lib.CachePath], recursive: true, CancellationToken.None);
        Assert.Single(files);
    }

    [Fact]
    public async Task Progress_reports_totals()
    {
        await using var host = new TestHost();
        await host.CreateLibraryAsync();
        var dir = host.SourceDir("p");
        for (var i = 0; i < 3; i++) Fixtures.Jpeg(Path.Combine(dir, $"{i}.jpg"), new Fixtures.PhotoSpec { Seed = i });
        var reports = new List<ImportProgress>();
        await host.Import.ImportAsync(new ImportRequest([dir], ImportMode.AddInPlace), new SyncProgress<ImportProgress>(reports.Add), CancellationToken.None);
        // The scan reports first, with the number of candidates found so far; then every report has the total.
        Assert.Equal(ImportPhase.Scanning, reports[0].Phase);
        Assert.Contains(reports, r => r.Phase == ImportPhase.Scanning && r.Examined == 3 && r.Total == 3);
        Assert.All(reports.Where(r => r.Phase is not (ImportPhase.Scanning)), r => Assert.Equal(3, r.Total));
        Assert.Equal(3, reports[^1].Processed);
        Assert.Equal(1, reports[^1].Fraction);
    }
}

public class ImportResponsivenessTests
{
    [Fact]
    public async Task Progress_is_coalesced_and_the_final_report_is_complete()
    {
        await using var host = new TestHost();
        await host.CreateLibraryAsync();
        var dir = host.SourceDir("many");
        for (var i = 0; i < 120; i++) Fixtures.Jpeg(Path.Combine(dir, $"p{i:000}.jpg"), new Fixtures.PhotoSpec { Seed = i });
        var reports = new List<ImportProgress>();
        var result = await host.Import.ImportAsync(new ImportRequest([dir], ImportMode.AddInPlace), new SyncProgress<ImportProgress>(reports.Add), CancellationToken.None);

        Assert.Equal(120, result.Imported);
        // Previously two reports per file reached the UI (240+); now they are rate-limited.
        Assert.True(reports.Count < 60, $"{reports.Count} progress reports for 120 files");
        Assert.Equal(120, reports[^1].Processed);
        Assert.Equal(120, reports[^1].Total);
        Assert.True(reports[^1].BytesProcessed > 0);
    }

    [Fact]
    public async Task Scan_reports_examined_and_compatible_files_before_the_job_exists()
    {
        await using var host = new TestHost();
        await host.CreateLibraryAsync();
        var dir = host.SourceDir("tree");
        for (var i = 0; i < 4; i++)
        {
            var sub = Path.Combine(dir, $"carpeta {i}", "más");
            Fixtures.Jpeg(Path.Combine(sub, $"f{i}.jpg"), new Fixtures.PhotoSpec { Seed = i });
            File.WriteAllText(Path.Combine(sub, $"notas{i}.txt"), "no es una foto");
        }

        var scans = new List<(int Examined, int Found, string? Folder)>();
        var files = host.Import.Scan([dir], recursive: true, (e, f, folder) => scans.Add((e, f, folder)), CancellationToken.None);

        Assert.Equal(4, files.Count);
        Assert.NotEmpty(scans);
        Assert.Equal((8, 4), (scans[^1].Examined, scans[^1].Found));
        // Only a folder name is reported, never a full path.
        Assert.All(scans.Where(s => s.Folder is not null), s => Assert.DoesNotContain(Path.DirectorySeparatorChar, s.Folder!));
    }

    [Fact]
    public async Task Diagnostics_time_each_phase_without_recording_paths()
    {
        await using var host = new TestHost();
        await host.CreateLibraryAsync();
        var dir = host.SourceDir("diag");
        for (var i = 0; i < 5; i++) Fixtures.Jpeg(Path.Combine(dir, $"secreto-{i}.jpg"), new Fixtures.PhotoSpec { Seed = i });
        await host.Import.ImportAsync(new ImportRequest([dir], ImportMode.CopyToLibrary), null, CancellationToken.None);

        var d = host.Import.Diagnostics;
        Assert.Equal(5, d.Count(ImportPhase.CopyAndHash));
        Assert.Equal(5, d.Count(ImportPhase.Signature));
        Assert.Equal(1, d.Count(ImportPhase.Scanning));
        Assert.True(d.BytesHashed > 0);
        Assert.DoesNotContain("secreto", d.ToString(), StringComparison.Ordinal);
    }
}

/// <summary>IProgress that invokes synchronously (Progress&lt;T&gt; posts asynchronously, which makes tests racy).</summary>
public sealed class SyncProgress<T>(Action<T> action) : IProgress<T>
{
    private readonly Lock _lock = new();

    public void Report(T value)
    {
        lock (_lock) action(value);
    }
}
