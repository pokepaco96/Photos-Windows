using System.Runtime.InteropServices;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using PhotosWindows.Application.Abstractions;
using PhotosWindows.Application.Import;
using PhotosWindows.Application.People;
using PhotosWindows.Domain;
using PhotosWindows.Integration.Tests.Support;

namespace PhotosWindows.Integration.Tests;

/// <summary>
/// People logic with the TEST-ONLY detector/embedder (synthetic faces): grouping, corrections, exclusion, resume,
/// model change, deletion and migration. Real-model inference is covered by FaceInferenceTests.
/// </summary>
public sealed class PeopleTests : IAsyncLifetime
{
    private readonly TestOnlyFaceDetector _detector = new();
    private readonly TestOnlyFaceEmbedder _embedder = new();
    private TestHost _host = null!;
    private LibraryInfo _library = null!;

    private PeopleService People => _host.Get<PeopleService>();

    public async Task InitializeAsync()
    {
        _host = new TestHost(s =>
        {
            s.AddSingleton<IFaceDetector>(_detector);
            s.AddSingleton<IFaceEmbedder>(_embedder);
        });
        _library = await _host.CreateLibraryAsync();
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    /// <summary>Imports: A alone, A+B together, B alone, a photo without faces.</summary>
    private async Task<Dictionary<string, long>> ImportStandardSetAsync()
    {
        var dir = _host.SourceDir("faces");
        FaceFixtures.Photo(Path.Combine(dir, "a1.jpg"), [new(0, 0.1f, 0.2f, 0.3f)], seed: 1);
        FaceFixtures.Photo(Path.Combine(dir, "ab.jpg"), [new(0, 0.05f, 0.1f, 0.3f), new(1, 0.6f, 0.4f, 0.3f)], seed: 2);
        FaceFixtures.Photo(Path.Combine(dir, "b1.jpg"), [new(1, 0.4f, 0.3f, 0.35f)], seed: 3);
        FaceFixtures.Photo(Path.Combine(dir, "none.jpg"), [], seed: 4);
        await _host.ImportAsync(dir, ImportMode.CopyToLibrary);
        return (await _host.AllAsync()).ToDictionary(a => a.FileName, a => a.Id);
    }

    private async Task<FaceAnalysisResult> EnableAndAnalyzeAsync()
    {
        await People.EnableAsync(CancellationToken.None);
        return await People.AnalyzeAsync(null, null, CancellationToken.None);
    }

    [Fact]
    public async Task Nothing_runs_until_enabled_and_models_are_installed()
    {
        await ImportStandardSetAsync();
        Assert.False(await People.IsEnabledAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => People.AnalyzeAsync(null, null, CancellationToken.None));

        _detector.IsReady = false;
        await People.EnableAsync(CancellationToken.None);
        Assert.False(People.ModelsReady);
        await Assert.ThrowsAsync<InvalidOperationException>(() => People.AnalyzeAsync(null, null, CancellationToken.None));
        Assert.Equal(0, _detector.Calls);
    }

    [Fact]
    public async Task Analysis_finds_faces_and_groups_them_without_names()
    {
        var ids = await ImportStandardSetAsync();
        var result = await EnableAndAnalyzeAsync();

        Assert.Equal(4, result.Analyzed);
        Assert.Equal(4, result.FacesFound);
        var stats = await People.GetStatsAsync(CancellationToken.None);
        Assert.Equal(new FaceAnalysisStats(4, 4, 0, 0, 4, 2, 0), stats);
        Assert.Equal(0, stats.Pending);

        var people = await People.GetPeopleAsync(CancellationToken.None);
        Assert.Equal(2, people.Count);
        Assert.All(people, p => Assert.Null(p.Name)); // never inferred
        Assert.All(people, p => Assert.Equal(2, p.AssetCount));

        // Each group holds one identity: A in a1 + ab, B in ab + b1.
        var byAsset = new Dictionary<long, List<long>>();
        foreach (var p in people)
        {
            byAsset[p.Id] = (await People.GetFacesOfPersonAsync(p.Id, CancellationToken.None)).Select(f => f.AssetId).OrderBy(x => x).ToList();
        }

        Assert.Contains(byAsset.Values, v => v.SequenceEqual(new[] { ids["a1.jpg"], ids["ab.jpg"] }.Order()));
        Assert.Contains(byAsset.Values, v => v.SequenceEqual(new[] { ids["ab.jpg"], ids["b1.jpg"] }.Order()));

        // The person filter of the grid returns that person's photos.
        var personA = people.Single(p => byAsset[p.Id].Contains(ids["a1.jpg"]));
        var photos = await _host.Assets.QueryAsync(new AssetQuery { PersonId = personA.Id, Sort = AssetSort.FileNameAscending }, CancellationToken.None);
        Assert.Equal(["a1.jpg", "ab.jpg"], photos.Select(p => p.FileName));
    }

    [Fact]
    public async Task A_photo_without_faces_is_analysed_and_not_analysed_again()
    {
        await ImportStandardSetAsync();
        await EnableAndAnalyzeAsync();
        var calls = _detector.Calls;
        var again = await People.AnalyzeAsync(null, null, CancellationToken.None);
        Assert.Equal(0, again.Analyzed);
        Assert.Equal(calls, _detector.Calls);
    }

    [Fact]
    public async Task Rotated_photo_gives_boxes_in_display_orientation()
    {
        var dir = _host.SourceDir("rotated");
        FaceFixtures.Photo(Path.Combine(dir, "rot.jpg"), [new(2, 0.1f, 0.5f, 0.3f)], width: 400, height: 300, orientation: 6);
        await _host.ImportAsync(dir, ImportMode.CopyToLibrary);
        await EnableAndAnalyzeAsync();
        var asset = (await _host.AllAsync()).Single();
        var face = Assert.Single(await People.GetFacesOfAssetAsync(asset.Id, CancellationToken.None));
        Assert.InRange(face.Box.X, 0.07f, 0.13f);
        Assert.InRange(face.Box.Y, 0.47f, 0.53f);
    }

    [Fact]
    public async Task A_photo_that_cannot_be_decoded_is_recorded_as_failed()
    {
        var ids = await ImportStandardSetAsync();
        var file = await _host.Assets.GetOriginalFileAsync(ids["b1.jpg"], CancellationToken.None);
        await File.WriteAllTextAsync(_library.Resolve(file!), "not an image any more");

        var result = await EnableAndAnalyzeAsync();
        Assert.Equal(1, result.Failed);
        Assert.Equal(3, result.Analyzed);
        var stats = await People.GetStatsAsync(CancellationToken.None);
        Assert.Equal(1, stats.Failed);
        Assert.Equal(0, stats.Pending);
    }

    [Fact]
    public async Task Naming_merging_splitting_and_ignoring_are_kept_by_regrouping()
    {
        await ImportStandardSetAsync();
        await EnableAndAnalyzeAsync();
        var people = await People.GetPeopleAsync(CancellationToken.None);
        var (first, second) = (people[0], people[1]);

        await People.RenameAsync(first.Id, "  Ana  ", CancellationToken.None);
        Assert.Equal("Ana", (await People.GetPersonAsync(first.Id, CancellationToken.None))!.Name);
        Assert.All(await People.GetFacesOfPersonAsync(first.Id, CancellationToken.None), f => Assert.Equal(FaceReview.Confirmed, f.Review));

        // Merge the second group into Ana: one group, name kept, the source group is gone.
        await People.MergeAsync([second.Id], first.Id, CancellationToken.None);
        var merged = Assert.Single(await People.GetPeopleAsync(CancellationToken.None));
        Assert.Equal("Ana", merged.Name);
        Assert.Equal(4, merged.FaceCount);
        Assert.Null(await People.GetPersonAsync(second.Id, CancellationToken.None));

        // Split one face out into a new (unnamed) group, ignore another one.
        var faces = await People.GetFacesOfPersonAsync(first.Id, CancellationToken.None);
        var newId = await People.MoveFacesAsync([faces[0].Id], null, CancellationToken.None);
        await People.IgnoreFacesAsync([faces[1].Id], CancellationToken.None);
        Assert.Equal(2, (await People.GetFacesOfPersonAsync(first.Id, CancellationToken.None)).Count);
        Assert.Null((await People.GetPersonAsync(newId, CancellationToken.None))!.Name);

        // Regrouping never undoes the user's decisions.
        await People.RegroupAsync(CancellationToken.None);
        Assert.Equal(2, (await People.GetFacesOfPersonAsync(first.Id, CancellationToken.None)).Count);
        Assert.Single(await People.GetFacesOfPersonAsync(newId, CancellationToken.None));
        Assert.Equal(FaceReview.Ignored, (await _host.Get<IPeopleRepository>().GetFaceAsync(faces[1].Id, CancellationToken.None))!.Review);
        Assert.Equal(3, (await People.GetStatsAsync(CancellationToken.None)).Faces); // ignored faces are not counted
    }

    [Fact]
    public async Task Merging_into_an_unnamed_group_keeps_the_source_name()
    {
        await ImportStandardSetAsync();
        await EnableAndAnalyzeAsync();
        var people = await People.GetPeopleAsync(CancellationToken.None);
        await People.RenameAsync(people[1].Id, "Bea", CancellationToken.None);
        await People.MergeAsync([people[1].Id], people[0].Id, CancellationToken.None);
        Assert.Equal("Bea", (await People.GetPersonAsync(people[0].Id, CancellationToken.None))!.Name);
    }

    [Fact]
    public async Task Excluded_photo_loses_its_faces_and_is_not_analysed_again()
    {
        var ids = await ImportStandardSetAsync();
        await EnableAndAnalyzeAsync();
        await People.ExcludeAsync([ids["ab.jpg"]], CancellationToken.None);

        Assert.Empty(await People.GetFacesOfAssetAsync(ids["ab.jpg"], CancellationToken.None));
        Assert.True(await People.IsExcludedAsync(ids["ab.jpg"], CancellationToken.None));
        var stats = await People.GetStatsAsync(CancellationToken.None);
        Assert.Equal(1, stats.Excluded);
        Assert.Equal(2, stats.Faces);

        await People.ResetAnalysisAsync(null, CancellationToken.None);
        await People.AnalyzeAsync(null, null, CancellationToken.None);
        Assert.Empty(await People.GetFacesOfAssetAsync(ids["ab.jpg"], CancellationToken.None));

        // Including it again makes it eligible for the next analysis.
        await People.IncludeAsync([ids["ab.jpg"]], CancellationToken.None);
        await People.AnalyzeAsync(null, null, CancellationToken.None);
        Assert.Equal(2, (await People.GetFacesOfAssetAsync(ids["ab.jpg"], CancellationToken.None)).Count);
    }

    [Fact]
    public async Task Hidden_and_deleted_photos_are_not_shown_in_people()
    {
        var ids = await ImportStandardSetAsync();
        await EnableAndAnalyzeAsync();
        await _host.Assets.SetHiddenAsync([ids["a1.jpg"]], true, CancellationToken.None);
        await _host.Assets.MoveToTrashAsync([ids["b1.jpg"]], _host.Clock.UtcNow, CancellationToken.None);
        var people = await People.GetPeopleAsync(CancellationToken.None);
        Assert.All(people, p => Assert.Equal(1, p.AssetCount));
    }

    [Fact]
    public async Task Cancelled_analysis_keeps_its_work_and_resumes_without_duplicates()
    {
        var dir = _host.SourceDir("many");
        for (var i = 0; i < 8; i++) FaceFixtures.Photo(Path.Combine(dir, $"p{i}.jpg"), [new(i % 2, 0.2f, 0.2f, 0.3f)], seed: i);
        await _host.ImportAsync(dir, ImportMode.CopyToLibrary);
        await People.EnableAsync(CancellationToken.None);
        _detector.Delay = TimeSpan.FromMilliseconds(150);

        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress<FaceAnalysisProgress>(p => { if (p.Done >= 2) cts.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => People.AnalyzeAsync(null, progress, cts.Token));
        var partial = await People.GetStatsAsync(CancellationToken.None);
        Assert.InRange(partial.Analyzed, 2, 7);

        _detector.Delay = TimeSpan.Zero;
        var rest = await People.AnalyzeAsync(null, null, CancellationToken.None);
        Assert.Equal(8 - partial.Analyzed, rest.Analyzed);
        var stats = await People.GetStatsAsync(CancellationToken.None);
        Assert.Equal(8, stats.Analyzed);
        Assert.Equal(8, stats.Faces);
        Assert.Equal(2, stats.People);
    }

    [Fact]
    public async Task Changing_the_model_reanalyses_and_keeps_names()
    {
        await ImportStandardSetAsync();
        await EnableAndAnalyzeAsync();
        var people = await People.GetPeopleAsync(CancellationToken.None);
        await People.RenameAsync(people[0].Id, "Ana", CancellationToken.None);
        var anaPhotos = (await People.GetFacesOfPersonAsync(people[0].Id, CancellationToken.None)).Select(f => f.AssetId).Order().ToList();

        _detector.ModelId = "test-only-detector-2";
        Assert.Equal(4, (await People.GetStatsAsync(CancellationToken.None)).Pending);
        var result = await People.AnalyzeAsync(null, null, CancellationToken.None);
        Assert.Equal(4, result.Analyzed);

        var ana = (await People.GetPeopleAsync(CancellationToken.None)).Single(p => p.Name == "Ana");
        var faces = await People.GetFacesOfPersonAsync(ana.Id, CancellationToken.None);
        Assert.Equal(anaPhotos, faces.Select(f => f.AssetId).Order());
        Assert.All(faces, f => Assert.Equal(People.ModelVersion, f.ModelVersion));
    }

    [Fact]
    public async Task Deleting_a_person_keeps_the_photos()
    {
        await ImportStandardSetAsync();
        await EnableAndAnalyzeAsync();
        var person = (await People.GetPeopleAsync(CancellationToken.None))[0];
        await People.DeletePersonAsync(person.Id, CancellationToken.None);
        Assert.Single(await People.GetPeopleAsync(CancellationToken.None));
        Assert.Equal(4, (await _host.AllAsync()).Count);
    }

    [Fact]
    public async Task Face_thumbnail_is_a_cached_crop_and_originals_are_untouched()
    {
        var ids = await ImportStandardSetAsync();
        var file = await _host.Assets.GetOriginalFileAsync(ids["ab.jpg"], CancellationToken.None);
        var before = Fixtures.Sha256(_library.Resolve(file!));
        await EnableAndAnalyzeAsync();
        var face = (await People.GetFacesOfAssetAsync(ids["ab.jpg"], CancellationToken.None))[0];
        var thumb = await People.GetFaceThumbnailAsync(face.Id, CancellationToken.None);
        Assert.NotNull(thumb);
        Assert.StartsWith(Path.Combine(_library.CachePath, "faces"), thumb);
        Assert.True(new FileInfo(thumb).Length > 100);
        Assert.Equal(before, Fixtures.Sha256(_library.Resolve(file!)));
    }

    [Fact]
    public async Task Delete_all_removes_every_derived_byte_and_keeps_the_library()
    {
        var ids = await ImportStandardSetAsync();
        await EnableAndAnalyzeAsync();
        var face = (await People.GetFacesOfAssetAsync(ids["a1.jpg"], CancellationToken.None))[0];
        await People.GetFaceThumbnailAsync(face.Id, CancellationToken.None);
        await People.RenameAsync((await People.GetPeopleAsync(CancellationToken.None))[0].Id, "Zzyzx-Unique-Name", CancellationToken.None);
        var vector = (await _host.Get<IPeopleRepository>().GetVectorsAsync(People.ModelVersion, CancellationToken.None))[0].Embedding;
        var pattern = MemoryMarshal.AsBytes(vector.AsSpan()).ToArray();

        await People.DeleteAllDataAsync(CancellationToken.None);

        Assert.False(await People.IsEnabledAsync(CancellationToken.None));
        Assert.Empty(await People.GetPeopleAsync(CancellationToken.None));
        Assert.False(Directory.Exists(Path.Combine(_library.CachePath, "faces")));
        await using (var c = new SqliteConnection($"Data Source={_library.DatabasePath}"))
        {
            Assert.Equal(0, await c.ExecuteScalarAsync<long>("SELECT (SELECT count(*) FROM faces) + (SELECT count(*) FROM people) + (SELECT count(*) FROM face_analysis)"));
        }

        SqliteConnection.ClearAllPools();
        var bytes = File.ReadAllBytes(_library.DatabasePath);
        var wal = _library.DatabasePath + "-wal";
        if (File.Exists(wal)) bytes = [.. bytes, .. File.ReadAllBytes(wal)];
        Assert.Equal(-1, bytes.AsSpan().IndexOf(pattern));
        Assert.Equal(-1, bytes.AsSpan().IndexOf("Zzyzx-Unique-Name"u8));
        Assert.Equal(4, (await _host.AllAsync()).Count);
    }

    [Fact]
    public async Task Catalog_from_version_1_is_migrated_with_its_data()
    {
        await ImportStandardSetAsync();
        // Turn this catalog back into exactly what version 1 created.
        await using (var c = new SqliteConnection($"Data Source={_library.DatabasePath}"))
        {
            await c.OpenAsync();
            await c.ExecuteAsync("DROP TABLE face_exclusions; DROP TABLE face_analysis; DROP TABLE faces; DROP TABLE people; DELETE FROM schema_migrations WHERE version > 1; PRAGMA user_version = 1;");
        }

        SqliteConnection.ClearAllPools();
        _host.Libs.Close();
        var reopened = await _host.Libs.OpenAsync(_library.RootPath, CancellationToken.None);
        Assert.Equal(_library.Id, reopened.Id);
        Assert.Equal(4, (await _host.AllAsync()).Count);
        await using var check = new SqliteConnection($"Data Source={_library.DatabasePath}");
        Assert.Equal(_host.Get<ICatalogStore>().CurrentSchemaVersion, await check.ExecuteScalarAsync<long>("PRAGMA user_version"));
        Assert.Equal(0, await check.ExecuteScalarAsync<long>("SELECT count(*) FROM faces"));
        await EnableAndAnalyzeAsync();
        Assert.Equal(2, (await People.GetPeopleAsync(CancellationToken.None)).Count);
    }

    [Fact]
    public async Task Backups_leave_face_data_out_unless_asked_and_say_so()
    {
        await ImportStandardSetAsync();
        await EnableAndAnalyzeAsync();
        await People.RenameAsync((await People.GetPeopleAsync(CancellationToken.None))[0].Id, "Zzyzx-Backup-Name", CancellationToken.None);
        var backups = Path.Combine(_host.Root, "backups");

        var without = await _host.Backup.CreateBackupAsync(backups, false, null, CancellationToken.None);
        _host.Clock.UtcNow = _host.Clock.UtcNow.AddMinutes(1);
        var with = await _host.Backup.CreateBackupAsync(backups, false, null, CancellationToken.None, includeFaceData: true);

        var plain = await _host.Backup.VerifyAsync(without, CancellationToken.None);
        var full = await _host.Backup.VerifyAsync(with, CancellationToken.None);
        Assert.True(plain.IsValid);
        Assert.True(full.IsValid);
        Assert.False(plain.Manifest!.IncludesFaceData);
        Assert.True(full.Manifest!.IncludesFaceData);

        SqliteConnection.ClearAllPools();
        Assert.Equal(-1, File.ReadAllBytes(Path.Combine(without, "catalog.db")).AsSpan().IndexOf("Zzyzx-Backup-Name"u8));
        Assert.NotEqual(-1, File.ReadAllBytes(Path.Combine(with, "catalog.db")).AsSpan().IndexOf("Zzyzx-Backup-Name"u8));

        // Restoring the plain backup gives a library without People data that asks for consent again.
        var restored = await _host.Backup.RestoreAsNewLibraryAsync(without, Path.Combine(_host.Root, "restored"), CancellationToken.None);
        await _host.Libs.OpenAsync(restored.RootPath, CancellationToken.None);
        Assert.False(await People.IsEnabledAsync(CancellationToken.None));
        Assert.Empty(await People.GetPeopleAsync(CancellationToken.None));
        Assert.Equal(4, (await _host.AllAsync()).Count);
    }

    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
