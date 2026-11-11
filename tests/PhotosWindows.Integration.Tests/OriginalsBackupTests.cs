using Microsoft.Extensions.DependencyInjection;
using PhotosWindows.Application.Abstractions;
using PhotosWindows.Application.Backup;
using PhotosWindows.Application.Import;
using PhotosWindows.Domain;
using PhotosWindows.Integration.Tests.Support;

namespace PhotosWindows.Integration.Tests;

public sealed class OriginalsBackupPlannerTests
{
    private static long _id;

    private static OriginalFileRow Row(long asset, string name, DateTime taken, FileRole role = FileRole.Original, DateSource source = DateSource.ExifOriginal, string? stored = null) =>
        new(new AssetFile { Id = Interlocked.Increment(ref _id), AssetId = asset, Role = role, StorageMode = StorageMode.Referenced, StoredPath = stored ?? "/src/" + name, SizeBytes = 10, Sha256 = "aa" },
            new Guid((int)asset, 0, 0, new byte[8]), MediaType.Photo, taken, source, role == FileRole.Original ? name : "ignored.jpg");

    private static string[] Paths(params OriginalFileRow[] rows) => OriginalsBackupPlanner.Plan(rows, f => f.StoredPath).Select(e => e.RelativePath).ToArray();

    [Theory]
    [InlineData(2024, 7, 14, "2024/07")]
    [InlineData(2022, 12, 31, "2022/12")]
    [InlineData(2023, 1, 1, "2023/01")]
    [InlineData(1999, 3, 5, "1999/03")]
    public void Folder_is_year_and_two_digit_month(int y, int m, int d, string expected) =>
        Assert.Equal(expected, OriginalsBackupPlanner.FolderFor(new DateTime(y, m, d, 23, 59, 59), DateSource.ExifOriginal));

    [Fact]
    public void Unknown_dates_go_to_the_undated_folder_and_fallback_sources_keep_their_date()
    {
        Assert.Equal("Sin fecha/00", OriginalsBackupPlanner.FolderFor(new DateTime(2024, 7, 14), DateSource.Unknown));
        Assert.Equal("Sin fecha/00", OriginalsBackupPlanner.FolderFor(DateTime.MinValue, DateSource.ExifOriginal));
        Assert.Equal("2021/05", OriginalsBackupPlanner.FolderFor(new DateTime(2021, 5, 2), DateSource.FileModified)); // the date the library already uses
    }

    [Fact]
    public void Local_capture_time_decides_the_month_never_utc()
    {
        // 31 Dec 23:30 local stays in December whatever the offset was.
        Assert.Equal(["2023/12/a.jpg"], Paths(Row(1, "a.jpg", new DateTime(2023, 12, 31, 23, 30, 0))));
    }

    [Fact]
    public void Companions_stay_with_their_asset_and_share_the_collision_suffix()
    {
        var july = new DateTime(2024, 7, 1);
        var paths = Paths(
            Row(1, "IMG_0001.HEIC", july), Row(1, "x", july, FileRole.LiveVideo, stored: "/a/IMG_0001.MOV"),
            Row(2, "IMG_0001.HEIC", july), Row(2, "x", july, FileRole.LiveVideo, stored: "/b/IMG_0001.MOV"), Row(2, "x", july, FileRole.Sidecar, stored: "/b/IMG_0001.xmp"),
            Row(3, "img_0001.heic", july),                                  // differs only in case: still a collision on Windows
            Row(4, "IMG_0001.HEIC", new DateTime(2024, 8, 1)),             // another month: no collision
            Row(5, "DSC_1.JPG", july), Row(5, "x", july, FileRole.RawPair, stored: "C:\\cam\\DSC_1.CR3"));
        Assert.Equal(
        [
            "2024/07/IMG_0001.HEIC", "2024/07/IMG_0001.MOV",
            "2024/07/IMG_0001 (2).HEIC", "2024/07/IMG_0001 (2).MOV", "2024/07/IMG_0001 (2).xmp",
            "2024/07/img_0001 (3).heic",
            "2024/08/IMG_0001.HEIC",
            "2024/07/DSC_1.JPG", "2024/07/DSC_1.CR3",
        ], paths);
        Assert.Equal(paths.Length, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Names_are_made_safe_for_windows_and_cannot_escape_the_folder()
    {
        var d = new DateTime(2024, 7, 1);
        var paths = Paths(Row(1, "CON.jpg", d), Row(2, "nul", d), Row(3, "a<b>:c?.jpg", d), Row(4, "../../evil.jpg", d), Row(5, "día ñandú 😀.tar.gz", d), Row(6, "trailing. ", d));
        Assert.Equal(["2024/07/_CON.jpg", "2024/07/_nul", "2024/07/a_b__c_.jpg", "2024/07/evil.jpg", "2024/07/día ñandú 😀.tar.gz", "2024/07/trailing"], paths);
        Assert.All(paths, p => Assert.DoesNotContain("..", p, StringComparison.Ordinal));
    }

    [Fact]
    public void Plan_is_deterministic_whatever_the_row_order()
    {
        var d = new DateTime(2024, 7, 1);
        var rows = Enumerable.Range(1, 40).Select(i => Row(i, i % 3 == 0 ? "same.jpg" : $"f{i}.jpg", d)).ToArray();
        var a = OriginalsBackupPlanner.Plan(rows, f => f.StoredPath).Select(e => (e.FileId, e.RelativePath)).ToArray();
        var b = OriginalsBackupPlanner.Plan(rows.Reverse(), f => f.StoredPath).Select(e => (e.FileId, e.RelativePath)).ToArray();
        Assert.Equal(a, b);
    }

    [Fact]
    public void Manifest_round_trips_and_rejects_other_formats_and_versions()
    {
        var m = new OriginalsManifest
        {
            LibraryId = Guid.NewGuid(), LibraryName = "Mi biblioteca", CreatedUtc = new DateTime(2026, 11, 10, 16, 30, 0, DateTimeKind.Utc), Complete = true, FileCount = 1,
            Files = [new OriginalsManifestFile { AssetId = Guid.NewGuid(), Role = FileRole.LiveVideo, OriginalName = "IMG_1.MOV", Path = "2024/07/IMG_1.MOV", Size = 5, Sha256 = "ab", CaptureLocal = "2024-07-01T10:00:00", DateSource = DateSource.ExifOriginal, MatchesCatalogHash = true }],
            Issues = [new OriginalsManifestIssue { Role = FileRole.Original, OriginalName = "x.jpg", Path = "2024/07/x.jpg", Kind = OriginalsIssueKind.SourceMissing }],
        };
        var json = m.ToJson();
        Assert.Contains("\"role\": \"LiveVideo\"", json, StringComparison.Ordinal);
        Assert.Contains("\"kind\": \"SourceMissing\"", json, StringComparison.Ordinal);
        var back = OriginalsManifest.Parse(json);
        Assert.Equal(m.Files[0], back.Files[0]);
        Assert.Equal((m.LibraryId, m.Complete, 1), (back.LibraryId, back.Complete, back.FormatVersion));
        Assert.Throws<InvalidDataException>(() => OriginalsManifest.Parse(json.Replace("photos-windows-originals-backup", "something-else", StringComparison.Ordinal)));
        Assert.Throws<InvalidDataException>(() => OriginalsManifest.Parse(json.Replace("\"formatVersion\": 1", "\"formatVersion\": 99", StringComparison.Ordinal)));
    }
}

/// <summary>A volume whose free space and file-size limit the test controls.</summary>
public sealed class FakeVolume : IVolumeInspector
{
    public long? Free { get; set; } = long.MaxValue / 4;
    public long? MaxFile { get; set; }
    public long? FreeBytes(string path) => Free;
    public long? MaxFileBytes(string path) => MaxFile;
}

public sealed class OriginalsBackupTests : IAsyncLifetime
{
    private readonly FakeVolume _volume = new();
    private TestHost _host = null!;
    private string _refDir = null!;
    private string _copyDir = null!;
    private string _dest = null!;

    public async Task InitializeAsync()
    {
        _host = new TestHost(s => s.AddSingleton<IVolumeInspector>(_volume));
        await _host.CreateLibraryAsync("Mi biblioteca");
        _refDir = _host.SourceDir("referenced");
        _copyDir = _host.SourceDir("to-copy");
        _dest = Path.Combine(_host.Root, "external-disk");
        Directory.CreateDirectory(_dest);

        // Referenced (stay where they are): a RAW+JPEG pair with a sidecar, a Live Photo style pair, a video and a PNG without any date.
        Fixtures.Jpeg(Path.Combine(_refDir, "DSC_0100.JPG"), new Fixtures.PhotoSpec { Seed = 1, Taken = new DateTime(2023, 12, 31, 23, 30, 0) });
        Fixtures.FakeRaw(Path.Combine(_refDir, "DSC_0100.CR2"), 1);
        Fixtures.Xmp(Path.Combine(_refDir, "DSC_0100.xmp"), "Fin de año", ["family"]);
        Fixtures.Jpeg(Path.Combine(_refDir, "IMG_0200.JPG"), new Fixtures.PhotoSpec { Seed = 2, Taken = new DateTime(2024, 7, 14, 10, 0, 0), Location = new GeoLocation(40.4168, -3.7038) });
        Fixtures.QuickTime(Path.Combine(_refDir, "IMG_0200.MOV"), TimeSpan.FromSeconds(2), new DateTime(2024, 7, 14, 8, 0, 0, DateTimeKind.Utc));
        Fixtures.QuickTime(Path.Combine(_refDir, "clip largo.mov"), TimeSpan.FromSeconds(30), new DateTime(2022, 3, 5, 12, 0, 0, DateTimeKind.Utc));
        await _host.ImportAsync(_refDir, ImportMode.AddInPlace);

        // Managed (copied into the library), with a name that collides with a referenced file of the same month.
        Fixtures.Jpeg(Path.Combine(_copyDir, "IMG_0200.JPG"), new Fixtures.PhotoSpec { Seed = 3, Taken = new DateTime(2024, 7, 20, 10, 0, 0) });
        Fixtures.Jpeg(Path.Combine(_copyDir, "oculta.jpg"), new Fixtures.PhotoSpec { Seed = 4, Taken = new DateTime(2021, 5, 2, 10, 0, 0) });
        Fixtures.Jpeg(Path.Combine(_copyDir, "borrada.jpg"), new Fixtures.PhotoSpec { Seed = 5, Taken = new DateTime(2021, 5, 3, 10, 0, 0) });
        await _host.ImportAsync(_copyDir, ImportMode.CopyToLibrary);
        var ids = (await _host.Assets.QueryAsync(new AssetQuery(), CancellationToken.None)).GroupBy(a => a.FileName).ToDictionary(g => g.Key, g => g.First().Id, StringComparer.Ordinal);
        await _host.Organization.SetHiddenAsync([ids["oculta.jpg"]], true, CancellationToken.None);
        await _host.Organization.MoveToTrashAsync([ids["borrada.jpg"]], CancellationToken.None);
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private OriginalsBackupService Backup => _host.Get<OriginalsBackupService>();

    private static string[] Tree(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/')).Order(StringComparer.Ordinal).ToArray();

    private Dictionary<string, string> SourceHashes() =>
        Directory.EnumerateFiles(_host.Root, "*", SearchOption.AllDirectories)
            .Where(f => !f.StartsWith(_dest, StringComparison.Ordinal) && !f.Contains("catalog.db", StringComparison.Ordinal) && !f.Contains("appdata", StringComparison.Ordinal))
            .ToDictionary(f => f, Fixtures.Sha256);

    [Fact]
    public async Task Complete_backup_copies_every_original_byte_for_byte_into_year_month_folders()
    {
        var before = SourceHashes();
        var pre = await Backup.AnalyzeAsync(_dest, CancellationToken.None);
        Assert.True(pre.CanStart && pre.CanBeComplete);
        Assert.Equal(pre.FileCount, pre.Unavailable.Count + 9);

        var progress = new List<OriginalsProgress>();
        var result = await Backup.CreateAsync(_dest, false, new SyncProgress<OriginalsProgress>(progress.Add), CancellationToken.None);

        Assert.Equal(OriginalsBackupStatus.Complete, result.Status);
        Assert.Equal((9, 0, 0), (result.FilesCopied, result.FilesAlreadyPresent, result.Issues.Count));
        Assert.StartsWith("PhotosWindows-Originales-Backup-Mi biblioteca-", Path.GetFileName(result.Folder), StringComparison.Ordinal);
        Assert.Equal(
        [
            "2021/05/borrada.jpg",        // recently deleted: still in the catalog, still protected
            "2021/05/oculta.jpg",         // hidden
            "2022/03/clip largo.mov",
            "2023/12/DSC_0100.CR2", "2023/12/DSC_0100.JPG", "2023/12/DSC_0100.xmp",
            "2024/07/IMG_0200 (2).JPG",   // managed file with the same name as a referenced one
            "2024/07/IMG_0200.JPG", "2024/07/IMG_0200.MOV",
            "backup-manifest.json",
        ], Tree(result.Folder));

        var manifest = OriginalsManifest.Parse(File.ReadAllText(Path.Combine(result.Folder, OriginalsManifest.FileName)));
        Assert.True(manifest.Complete);
        Assert.Equal((9, result.Bytes), (manifest.FileCount, manifest.TotalBytes));
        Assert.Equal(pre.AssetCount, manifest.AssetCount);
        // Every copy is identical to a source file, and its manifest entry says the truth about it.
        var sourceByHash = before.GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.First().Key);
        foreach (var f in manifest.Files)
        {
            var copy = Path.Combine(result.Folder, f.Path);
            Assert.Equal(f.Sha256, Fixtures.Sha256(copy));
            Assert.Equal(f.Size, new FileInfo(copy).Length);
            Assert.True(sourceByHash.ContainsKey(f.Sha256), f.Path);
            Assert.Equal(Path.GetExtension(sourceByHash[f.Sha256]), Path.GetExtension(copy), ignoreCase: true);
            Assert.True(f.MatchesCatalogHash);
            Assert.False(File.GetAttributes(copy).HasFlag(FileAttributes.ReparsePoint)); // a real copy, not a link
        }

        Assert.Contains(manifest.Files, f => f is { Role: FileRole.RawPair, Path: "2023/12/DSC_0100.CR2" });
        Assert.Contains(manifest.Files, f => f is { Role: FileRole.Sidecar, Path: "2023/12/DSC_0100.xmp" });
        Assert.Contains(manifest.Files, f => f is { Role: FileRole.LiveVideo, Path: "2024/07/IMG_0200.MOV" });
        Assert.Equal(before, SourceHashes());                                   // no source was touched
        Assert.Empty(Directory.EnumerateFiles(_dest, "*.pwpartial", SearchOption.AllDirectories));
        Assert.Equal(9, progress[^1].FilesDone);
        Assert.Equal(result.Bytes, progress[^1].BytesDone);

        var verification = await Backup.VerifyAsync(result.Folder, null, CancellationToken.None);
        Assert.True(verification.IsValid, string.Join("; ", verification.Problems));
        Assert.Equal(9, verification.FilesChecked);
    }

    [Fact]
    public async Task Manifest_holds_no_absolute_paths_gps_or_face_data()
    {
        var result = await Backup.CreateAsync(_dest, false, null, CancellationToken.None);
        var json = File.ReadAllText(Path.Combine(result.Folder, OriginalsManifest.FileName));
        Assert.DoesNotContain(_host.Root, json, StringComparison.Ordinal);
        Assert.DoesNotContain("referenced", json, StringComparison.Ordinal);
        Assert.DoesNotContain("40.4", json, StringComparison.Ordinal);
        foreach (var word in new[] { "latitude", "longitude", "gps", "embedding", "person", "face" }) Assert.DoesNotContain(word, json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Missing_originals_block_the_backup_unless_the_user_accepts_an_incomplete_one()
    {
        File.Delete(Path.Combine(_refDir, "clip largo.mov")); // e.g. the external drive is not connected
        var pre = await Backup.AnalyzeAsync(_dest, CancellationToken.None);
        Assert.False(pre.CanBeComplete);
        Assert.Equal(["clip largo.mov"], pre.Unavailable.Select(i => i.OriginalName));

        var blocked = await Assert.ThrowsAsync<OriginalsBackupBlockedException>(() => Backup.CreateAsync(_dest, false, null, CancellationToken.None));
        Assert.Single(blocked.Preflight.Unavailable);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_dest)); // nothing was started

        var result = await Backup.CreateAsync(_dest, true, null, CancellationToken.None);
        Assert.Equal(OriginalsBackupStatus.PartialAccepted, result.Status);
        Assert.EndsWith("-INCOMPLETA", result.Folder, StringComparison.Ordinal);
        var manifest = OriginalsManifest.Parse(File.ReadAllText(Path.Combine(result.Folder, OriginalsManifest.FileName)));
        Assert.False(manifest.Complete);                          // never presented as a complete backup
        Assert.Equal(8, manifest.FileCount);
        Assert.Equal(OriginalsIssueKind.SourceMissing, Assert.Single(manifest.Issues).Kind);
        var verification = await Backup.VerifyAsync(result.Folder, null, CancellationToken.None);
        Assert.False(verification.IsValid);
        Assert.Contains(verification.Problems, p => p.Contains("not complete", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Source_changed_since_import_is_copied_as_it_is_but_reported_and_the_backup_is_not_complete()
    {
        var path = Path.Combine(_refDir, "DSC_0100.xmp");
        File.AppendAllText(path, "<!-- edited by another program -->");
        var result = await Backup.CreateAsync(_dest, false, null, CancellationToken.None);
        Assert.Equal(OriginalsBackupStatus.Incomplete, result.Status);
        Assert.EndsWith(".partial", result.Folder, StringComparison.Ordinal);
        var issue = Assert.Single(result.Issues);
        Assert.Equal((OriginalsIssueKind.ChangedSinceImport, "DSC_0100.xmp"), (issue.Kind, issue.OriginalName));
        var manifest = OriginalsManifest.Parse(File.ReadAllText(Path.Combine(result.Folder, OriginalsManifest.FileName)));
        Assert.False(manifest.Complete);
        Assert.False(manifest.Files.Single(f => f.OriginalName == "DSC_0100.xmp").MatchesCatalogHash);
        Assert.Equal(Fixtures.Sha256(path), Fixtures.Sha256(Path.Combine(result.Folder, "2023", "12", "DSC_0100.xmp")));
    }

    [Fact]
    public async Task Cancelling_leaves_a_resumable_folder_and_resuming_reverifies_what_was_copied()
    {
        using var cts = new CancellationTokenSource();
        var cancelled = await Backup.CreateAsync(_dest, false, new SyncProgress<OriginalsProgress>(p => { if (p.FilesDone == 4) cts.Cancel(); }), cts.Token);
        Assert.Equal(OriginalsBackupStatus.Cancelled, cancelled.Status);
        Assert.EndsWith(".partial", cancelled.Folder, StringComparison.Ordinal);
        Assert.Equal(4, cancelled.FilesCopied);
        Assert.False(OriginalsManifest.Parse(File.ReadAllText(Path.Combine(cancelled.Folder, OriginalsManifest.FileName))).Complete);
        Assert.Empty(Directory.EnumerateFiles(cancelled.Folder, "*.pwpartial", SearchOption.AllDirectories));
        Assert.Equal([cancelled.Folder], Backup.FindUnfinished(_dest));

        // While it waits: one copied file gets damaged, and a stray temporary is left behind.
        var copied = Tree(cancelled.Folder).Where(p => p != OriginalsManifest.FileName).ToArray();
        File.WriteAllText(Path.Combine(cancelled.Folder, copied[0]), "damaged");
        File.WriteAllText(Path.Combine(cancelled.Folder, copied[1] + ".pwpartial"), "half");
        // The external drive comes back with another letter: the folder is somewhere else now.
        var moved = Path.Combine(_host.Root, "other-letter", Path.GetFileName(cancelled.Folder));
        Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
        Directory.Move(cancelled.Folder, moved);

        var resumed = await Backup.ResumeAsync(moved, false, null, CancellationToken.None);
        Assert.Equal(OriginalsBackupStatus.Complete, resumed.Status);
        Assert.Equal((3, 6), (resumed.FilesAlreadyPresent, resumed.FilesCopied)); // 3 trusted after re-hashing, the damaged one and 5 new copied
        Assert.False(Directory.Exists(moved));
        Assert.True((await Backup.VerifyAsync(resumed.Folder, null, CancellationToken.None)).IsValid);
        Assert.Empty(Directory.EnumerateFiles(resumed.Folder, "*.pwpartial", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Destination_checks_space_file_size_limit_and_forbidden_locations()
    {
        var pre = await Backup.AnalyzeAsync(_dest, CancellationToken.None);
        _volume.Free = pre.TotalBytes; // the files fit but the safety margin does not
        Assert.False((await Backup.AnalyzeAsync(_dest, CancellationToken.None)).EnoughSpace);
        await Assert.ThrowsAsync<OriginalsBackupBlockedException>(() => Backup.CreateAsync(_dest, true, null, CancellationToken.None));
        _volume.Free = long.MaxValue / 4;

        // A FAT32-like limit: files above it are listed, and the backup cannot be complete on that disk.
        var biggest = Directory.EnumerateFiles(_refDir).Max(f => new FileInfo(f).Length);
        _volume.MaxFile = biggest - 1;
        var limited = await Backup.AnalyzeAsync(_dest, CancellationToken.None);
        Assert.NotEmpty(limited.TooLarge);
        Assert.False(limited.CanBeComplete);
        await Assert.ThrowsAsync<OriginalsBackupBlockedException>(() => Backup.CreateAsync(_dest, false, null, CancellationToken.None));
        _volume.MaxFile = null;

        var library = _host.Get<ILibraryContext>().RequireCurrent();
        Assert.Equal("inside-library", (await Backup.AnalyzeAsync(Path.Combine(library.RootPath, "backups"), CancellationToken.None)).DestinationProblem);
        Assert.Equal("inside-source", (await Backup.AnalyzeAsync(Path.Combine(_refDir, "copia"), CancellationToken.None)).DestinationProblem);
        await Assert.ThrowsAsync<OriginalsBackupBlockedException>(() => Backup.CreateAsync(_refDir, true, null, CancellationToken.None));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_dest));
    }

    [Fact]
    public async Task Destination_that_disappears_mid_copy_stops_the_backup_without_calling_it_done()
    {
        // After the second file the working folder vanishes and cannot be recreated, as when a drive is unplugged.
        var result = await Backup.CreateAsync(_dest, false, new SyncProgress<OriginalsProgress>(p =>
        {
            if (p.FilesDone != 2) return;
            var work = Directory.EnumerateDirectories(_dest).Single();
            Directory.Delete(work, recursive: true);
            File.WriteAllText(work, "not a folder");
        }), CancellationToken.None);
        Assert.Equal(OriginalsBackupStatus.Incomplete, result.Status);
        Assert.Equal("The destination is no longer available.", result.Error);
        Assert.EndsWith(".partial", result.Folder, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateDirectories(_dest)); // nothing that could pass for a finished backup
    }

    [Fact]
    public async Task Destination_that_runs_out_of_space_mid_copy_stops_and_can_be_resumed()
    {
        var result = await Backup.CreateAsync(_dest, false, new SyncProgress<OriginalsProgress>(p =>
        {
            if (p.FilesDone != 3) return;
            _volume.Free = 0;
            // The next file cannot be written: its month folder is occupied by a file, like a write that fails on a full disk.
            var work = Directory.EnumerateDirectories(_dest).Single();
            foreach (var month in new[] { "2021/05", "2022/03", "2023/12", "2024/07" })
            {
                var dir = Path.Combine(work, month);
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dir)!);
                    File.WriteAllText(dir, "full");
                }
            }
        }), CancellationToken.None);
        Assert.Equal(OriginalsBackupStatus.Incomplete, result.Status);
        Assert.Equal("The destination ran out of space.", result.Error);
        Assert.False(OriginalsManifest.Parse(File.ReadAllText(Path.Combine(result.Folder, OriginalsManifest.FileName))).Complete);

        // Space is freed: resuming finishes the job.
        _volume.Free = long.MaxValue / 4;
        foreach (var blocker in Directory.EnumerateFiles(result.Folder, "*", SearchOption.AllDirectories).Where(f => new FileInfo(f).Length == 4 && File.ReadAllText(f) == "full")) File.Delete(blocker);
        var resumed = await Backup.ResumeAsync(result.Folder, false, null, CancellationToken.None);
        Assert.Equal(OriginalsBackupStatus.Complete, resumed.Status);
        Assert.Equal(9, resumed.FilesCopied + resumed.FilesAlreadyPresent);
    }

    [Fact]
    public async Task Verify_detects_missing_changed_extra_and_unsafe_entries_and_never_overwrites_an_earlier_backup()
    {
        var first = await Backup.CreateAsync(_dest, false, null, CancellationToken.None);
        _host.Clock.UtcNow = _host.Clock.UtcNow.AddMinutes(1);
        var second = await Backup.CreateAsync(_dest, false, null, CancellationToken.None);
        Assert.NotEqual(first.Folder, second.Folder);
        Assert.True((await Backup.VerifyAsync(first.Folder, null, CancellationToken.None)).IsValid); // the earlier backup is intact
        await Assert.ThrowsAsync<IOException>(() => Backup.CreateAsync(_dest, false, null, CancellationToken.None)); // same second: refuses, never overwrites

        File.Delete(Path.Combine(second.Folder, "2022", "03", "clip largo.mov"));
        File.AppendAllText(Path.Combine(second.Folder, "2021", "05", "oculta.jpg"), "x");
        File.WriteAllText(Path.Combine(second.Folder, "2021", "05", "intruso.txt"), "x");
        var v = await Backup.VerifyAsync(second.Folder, null, CancellationToken.None);
        Assert.False(v.IsValid);
        Assert.Contains("Missing: 2022/03/clip largo.mov", v.Problems);
        Assert.Contains("Size differs: 2021/05/oculta.jpg", v.Problems);
        Assert.Contains("Not in the manifest: 2021/05/intruso.txt", v.Problems);

        var manifestPath = Path.Combine(first.Folder, OriginalsManifest.FileName);
        File.WriteAllText(manifestPath, File.ReadAllText(manifestPath).Replace("2021/05/oculta.jpg", "../../outside.jpg", StringComparison.Ordinal));
        Assert.Contains((await Backup.VerifyAsync(first.Folder, null, CancellationToken.None)).Problems, p => p.StartsWith("Unsafe path", StringComparison.Ordinal));
        Assert.False((await Backup.VerifyAsync(_refDir, null, CancellationToken.None)).IsValid); // a folder that is not a backup
    }
}
