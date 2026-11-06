using Dapper;
using Microsoft.Data.Sqlite;
using PhotosWindows.Application.Abstractions;
using PhotosWindows.Domain;
using PhotosWindows.Integration.Tests.Support;

namespace PhotosWindows.Integration.Tests;

public class OrganizationTests
{
    private static async Task<(TestHost Host, IReadOnlyList<long> Ids)> SeedAsync(int count = 4, ImportMode mode = ImportMode.AddInPlace)
    {
        var host = new TestHost();
        await host.CreateLibraryAsync();
        var dir = host.SourceDir("seed");
        for (var i = 0; i < count; i++)
        {
            Fixtures.Jpeg(Path.Combine(dir, $"photo{i}.jpg"), new Fixtures.PhotoSpec { Seed = i, Taken = new DateTime(2020 + i, 5, 1, 12, 0, 0) });
        }

        await host.ImportAsync(dir, mode);
        var ids = (await host.AllAsync()).Select(s => s.Id).ToList();
        return (host, ids);
    }

    private static async Task<long> CountFilesAsync(TestHost host)
    {
        await using var c = new SqliteConnection($"Data Source={host.Get<ILibraryContext>().Current!.DatabasePath}");
        await c.OpenAsync();
        return await c.ExecuteScalarAsync<long>("SELECT count(*) FROM asset_files");
    }

    [Fact]
    public async Task Albums_are_logical_and_never_duplicate_files()
    {
        var (host, ids) = await SeedAsync();
        await using var _ = host;
        var filesBefore = await CountFilesAsync(host);

        var a = await host.Organization.CreateAlbumAsync("Holidays", null, [ids[0], ids[1]], CancellationToken.None);
        var b = await host.Organization.CreateAlbumAsync("Best", null, null, CancellationToken.None);
        Assert.Equal(2, await host.Organization.AddToAlbumAsync(b, ids, CancellationToken.None) - 2);
        Assert.Equal(0, await host.Organization.AddToAlbumAsync(a, [ids[0]], CancellationToken.None)); // already there

        Assert.Equal(filesBefore, await CountFilesAsync(host));
        Assert.Equal(2, (await host.Organization.GetAlbumAsync(a, CancellationToken.None))!.ItemCount);
        Assert.Equal(2, (await host.Organization.GetAlbumsContainingAsync(ids[0], CancellationToken.None)).Count);

        await host.Organization.DeleteAlbumAsync(a, CancellationToken.None);
        Assert.Equal(ids.Count, (await host.AllAsync()).Count);
    }

    [Fact]
    public async Task Manual_album_order_can_be_changed()
    {
        var (host, ids) = await SeedAsync();
        await using var _ = host;
        var album = await host.Organization.CreateAlbumAsync("Order", null, ids, CancellationToken.None);
        await host.Organization.MoveInAlbumAsync(album, ids[3], 0, CancellationToken.None);
        var ordered = await host.Assets.QueryAsync(new AssetQuery { AlbumId = album, Sort = AssetSort.AlbumOrder }, CancellationToken.None);
        Assert.Equal([ids[3], ids[0], ids[1], ids[2]], ordered.Select(s => s.Id).ToArray());

        await host.Organization.RemoveFromAlbumAsync(album, [ids[0]], CancellationToken.None);
        Assert.Equal(3, (await host.Assets.QueryAsync(new AssetQuery { AlbumId = album }, CancellationToken.None)).Count);
    }

    [Fact]
    public async Task Folders_nest_refuse_cycles_and_release_children_when_deleted()
    {
        var (host, _) = await SeedAsync(1);
        await using var __ = host;
        var org = host.Organization;
        var parent = await org.CreateFolderAsync("Travel", null, CancellationToken.None);
        var child = await org.CreateFolderAsync("Europe", parent, CancellationToken.None);
        var album = await org.CreateAlbumAsync("Paris", child, null, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => org.MoveFolderAsync(parent, child, CancellationToken.None));

        await org.DeleteFolderAsync(child, CancellationToken.None);
        Assert.Equal(parent, (await org.GetAlbumAsync(album, CancellationToken.None))!.FolderId);
        Assert.Single(await org.GetFoldersAsync(CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => org.CreateAlbumAsync("  ", null, null, CancellationToken.None));
    }

    [Fact]
    public async Task Smart_albums_evaluate_rules_and_reject_manual_additions()
    {
        var (host, ids) = await SeedAsync();
        await using var _ = host;
        await host.Organization.SetFavoriteAsync([ids[1], ids[2]], true, CancellationToken.None);
        await host.Organization.AddKeywordsAsync([ids[2], ids[3]], ["Beach"], CancellationToken.None);

        var definition = new SmartAlbumDefinition(SmartMatch.All, [
            new SmartRule(SmartField.Favorite, SmartOperator.Is, "true"),
            new SmartRule(SmartField.Keyword, SmartOperator.Is, "beach"),
            new SmartRule(SmartField.Camera, SmartOperator.Contains, "z100"),
        ]);
        var smart = await host.Organization.CreateSmartAlbumAsync("Fav beach", null, definition, CancellationToken.None);
        var album = (await host.Organization.GetAlbumAsync(smart, CancellationToken.None))!;
        Assert.Equal(AlbumKind.Smart, album.Kind);
        Assert.Equal(definition.Rules, album.SmartDefinition!.Rules);

        var matches = await host.Assets.QueryAsync(new AssetQuery { Smart = album.SmartDefinition }, CancellationToken.None);
        Assert.Equal([ids[2]], matches.Select(m => m.Id).ToArray());

        var any = await host.Assets.QueryAsync(new AssetQuery { Smart = definition with { Match = SmartMatch.Any } }, CancellationToken.None);
        Assert.Equal(4, any.Count); // every photo matches the camera rule

        var byDate = await host.Assets.QueryAsync(new AssetQuery { Smart = new SmartAlbumDefinition(SmartMatch.All, [new SmartRule(SmartField.CaptureDate, SmartOperator.After, "2021-12-31")]) }, CancellationToken.None);
        Assert.Equal(2, byDate.Count);

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Organization.AddToAlbumAsync(smart, [ids[0]], CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => host.Organization.CreateSmartAlbumAsync("empty", null, SmartAlbumDefinition.Empty, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => host.Organization.CreateSmartAlbumAsync("bad", null,
            new SmartAlbumDefinition(SmartMatch.All, [new SmartRule(SmartField.Rating, SmartOperator.GreaterOrEqual, "lots")]), CancellationToken.None));
    }

    [Fact]
    public async Task Keywords_can_be_added_removed_renamed_and_merged()
    {
        var (host, ids) = await SeedAsync(2);
        await using var _ = host;
        var org = host.Organization;
        await org.AddKeywordsAsync(ids, ["dog", "park"], CancellationToken.None);
        await org.AddKeywordsAsync([ids[0]], ["Dog", "  "], CancellationToken.None); // case-insensitive, blank ignored
        Assert.Equal(["dog", "park"], await org.GetKeywordsForAsync(ids[0], CancellationToken.None));
        await org.RemoveKeywordAsync([ids[1]], "park", CancellationToken.None);
        Assert.Equal(["dog"], await org.GetKeywordsForAsync(ids[1], CancellationToken.None));

        var all = await org.GetKeywordsAsync(CancellationToken.None);
        var park = all.Single(k => k.Name == "park");
        Assert.Equal(1, park.UsageCount);
        await org.RenameKeywordAsync(park.Id, "DOG", CancellationToken.None); // merges into "dog"
        Assert.Single(await org.GetKeywordsAsync(CancellationToken.None));
        Assert.Equal(2, (await host.Assets.QueryAsync(new AssetQuery { Keywords = ["dog"] }, CancellationToken.None)).Count);
    }

    [Fact]
    public async Task Hidden_items_leave_normal_views_and_can_be_shown_again()
    {
        var (host, ids) = await SeedAsync(3);
        await using var _ = host;
        await host.Organization.SetHiddenAsync([ids[0]], true, CancellationToken.None);
        Assert.Equal(2, (await host.AllAsync()).Count);
        Assert.Equal([ids[0]], (await host.AllAsync(LibraryScope.Hidden)).Select(s => s.Id).ToArray());
        await host.Organization.SetHiddenAsync([ids[0]], false, CancellationToken.None);
        Assert.Equal(3, (await host.AllAsync()).Count);
    }

    [Fact]
    public async Task Trash_restore_and_permanent_deletion_respect_storage_modes()
    {
        await using var host = new TestHost();
        var lib = await host.CreateLibraryAsync();
        var managedSrc = Fixtures.Jpeg(Path.Combine(host.SourceDir("m"), "managed.jpg"), new Fixtures.PhotoSpec { Seed = 1 });
        var referenced = Fixtures.Jpeg(Path.Combine(host.SourceDir("r"), "referenced.jpg"), new Fixtures.PhotoSpec { Seed = 2 });
        await host.ImportAsync(Path.Combine(host.Sources, "m"), ImportMode.CopyToLibrary);
        await host.ImportAsync(Path.Combine(host.Sources, "r"), ImportMode.AddInPlace);
        var all = await host.AllAsync();
        var ids = all.Select(s => s.Id).ToList();
        var managedCopy = lib.Resolve((await host.Assets.GetOriginalFileAsync(all.Single(s => s.FileName == "managed.jpg").Id, CancellationToken.None))!);

        var notTrashed = await host.Organization.PlanPermanentDeletionAsync(ids, false, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Organization.DeletePermanentlyAsync(notTrashed, CancellationToken.None));

        await host.Organization.MoveToTrashAsync(ids, CancellationToken.None);
        Assert.Empty(await host.AllAsync());
        Assert.Equal(2, (await host.AllAsync(LibraryScope.RecentlyDeleted)).Count);
        await host.Organization.RestoreAsync([ids[0]], CancellationToken.None);
        Assert.Single(await host.AllAsync());
        await host.Organization.MoveToTrashAsync([ids[0]], CancellationToken.None);

        var plan = await host.Organization.PlanPermanentDeletionAsync(ids, includeReferencedFiles: false, CancellationToken.None);
        Assert.Equal(1, plan.PhysicalFileCount);
        Assert.Equal(1, plan.KeptReferencedFiles);
        Assert.Contains(plan.Files, f => f.StorageMode == StorageMode.Managed && f.WillDelete && f.AbsolutePath == managedCopy);

        var result = await host.Organization.DeletePermanentlyAsync(plan, CancellationToken.None);
        Assert.Equal(2, result.AssetsRemoved);
        Assert.Equal(1, result.FilesDeleted);
        Assert.False(File.Exists(managedCopy));
        Assert.True(File.Exists(referenced));
        Assert.True(File.Exists(managedSrc));
        Assert.Empty(await host.AllAsync(LibraryScope.RecentlyDeleted));
    }

    [Fact]
    public async Task Details_and_capture_date_can_be_edited_without_touching_files()
    {
        var (host, ids) = await SeedAsync(1);
        await using var _ = host;
        var path = host.Get<ILibraryContext>().Current!.Resolve((await host.Assets.GetOriginalFileAsync(ids[0], CancellationToken.None))!);
        var hash = Fixtures.Sha256(path);
        await host.Organization.UpdateDetailsAsync(ids[0], new AssetDetailsUpdate("Title", "Desc", 9, new GeoLocation(10, 20)), CancellationToken.None);
        await host.Organization.SetCaptureDateAsync(ids[0], new DateTime(1999, 12, 31, 23, 59, 0), null, CancellationToken.None);
        var a = (await host.Assets.GetAsync(ids[0], CancellationToken.None))!;
        Assert.Equal(("Title", "Desc", 5), (a.Title, a.Description, a.Rating));
        Assert.Equal(10, a.Location!.Value.Latitude);
        Assert.Equal(DateSource.Manual, a.CaptureDate.Source);
        Assert.Equal(hash, Fixtures.Sha256(path));

        await host.Organization.UpdateDetailsAsync(ids[0], new AssetDetailsUpdate(null, null, 0, null, ClearLocation: true), CancellationToken.None);
        Assert.Null((await host.Assets.GetAsync(ids[0], CancellationToken.None))!.Location);
    }
}
