using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Xmp;
using PhotosWindows.Application.Abstractions;
using PhotosWindows.Application.Export;
using PhotosWindows.Application.Thumbnails;
using PhotosWindows.Domain;
using PhotosWindows.Integration.Tests.Support;
using SkiaSharp;

namespace PhotosWindows.Integration.Tests;

public class EditExportTests
{
    private static async Task<(TestHost Host, long Id, string Original)> SeedAsync(Fixtures.PhotoSpec? spec = null)
    {
        var host = new TestHost();
        await host.CreateLibraryAsync();
        var path = Fixtures.Jpeg(Path.Combine(host.SourceDir("e"), "edit_me.jpg"), spec ?? new Fixtures.PhotoSpec { Width = 400, Height = 300, Location = new PhotosWindows.Domain.GeoLocation(43.36, -5.85), Title = "Original title" });
        await host.ImportAsync(Path.Combine(host.Sources, "e"), ImportMode.AddInPlace);
        return (host, (await host.AllAsync()).Single().Id, path);
    }

    [Fact]
    public async Task Edits_persist_across_reopen_and_never_change_the_original()
    {
        var (host, id, original) = await SeedAsync();
        await using var _ = host;
        var hash = Fixtures.Sha256(original);
        var modified = File.GetLastWriteTimeUtc(original);
        var recipe = new EditRecipe { Exposure = 0.5, Crop = new NormalizedRect(0, 0, 0.5, 0.5), BlackAndWhite = 1 };

        var v1 = await host.Editing.SaveAsync(id, recipe, CancellationToken.None);
        var v2 = await host.Editing.SaveAsync(id, recipe with { Exposure = 0.7 }, CancellationToken.None);
        Assert.True(v2 > v1);

        var root = host.Get<ILibraryContext>().Current!.RootPath;
        host.Libs.Close();
        await host.Libs.OpenAsync(root, CancellationToken.None);

        Assert.Equal(recipe with { Exposure = 0.7 }, await host.Editing.GetRecipeAsync(id, CancellationToken.None));
        var summary = (await host.AllAsync()).Single();
        Assert.True(summary.HasEdits);
        Assert.Equal(v2, summary.EditVersion);
        Assert.Equal(2, (await host.Editing.GetHistoryAsync(id, CancellationToken.None)).Count);
        Assert.Equal(hash, Fixtures.Sha256(original));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(original));

        await host.Editing.RevertToOriginalAsync(id, CancellationToken.None);
        Assert.False((await host.AllAsync()).Single().HasEdits);
        Assert.Equal(3, (await host.Editing.GetHistoryAsync(id, CancellationToken.None)).Count);
    }

    [Fact]
    public async Task Preview_source_and_render_reflect_the_recipe()
    {
        var (host, id, _) = await SeedAsync();
        await using var __ = host;
        var source = await host.Editing.LoadPreviewSourceAsync(id, 200, CancellationToken.None);
        Assert.NotNull(source);
        Assert.Equal(200, Math.Max(source.Width, source.Height));
        var preview = await host.Editing.RenderPreviewAsync(source, new EditRecipe { QuarterTurns = 1 }, 200, CancellationToken.None);
        Assert.True(preview.Height > preview.Width);
    }

    [Fact]
    public async Task Thumbnails_are_generated_cached_and_versioned_by_edit()
    {
        var (host, id, _) = await SeedAsync();
        await using var __ = host;
        Assert.Equal(1, await host.Thumbnails.ProcessPendingAsync(10, CancellationToken.None) - 1); // thumb + perceptual hash
        var summary = (await host.AllAsync()).Single();
        Assert.Equal(ThumbnailState.Ready, summary.ThumbnailState);
        var path = host.Thumbnails.TryGetCached(summary, ThumbnailSize.Small);
        Assert.NotNull(path);
        Assert.NotNull((await host.Assets.GetAsync(id, CancellationToken.None))!.PerceptualHash);

        await host.Editing.SaveAsync(id, new EditRecipe { QuarterTurns = 1 }, CancellationToken.None);
        var edited = (await host.AllAsync()).Single();
        Assert.Null(host.Thumbnails.TryGetCached(edited, ThumbnailSize.Small));
        var newPath = await host.Thumbnails.GetOrCreateAsync(id, ThumbnailSize.Small, CancellationToken.None);
        Assert.NotEqual(path, newPath);
        Assert.False(File.Exists(path)); // stale version cleaned up
        using var bmp = SKBitmap.Decode(newPath);
        Assert.True(bmp.Height > bmp.Width);
    }

    [Fact]
    public async Task Missing_original_marks_thumbnail_unavailable_without_throwing()
    {
        var (host, id, original) = await SeedAsync();
        await using var _ = host;
        File.Delete(original);
        Assert.Null(await host.Thumbnails.GetOrCreateAsync(id, ThumbnailSize.Small, CancellationToken.None));
        Assert.False((await host.AllAsync()).Single().IsAvailable);
    }

    [Fact]
    public async Task Edited_export_applies_recipe_and_writes_clean_metadata()
    {
        var (host, id, original) = await SeedAsync();
        await using var _ = host;
        var hash = Fixtures.Sha256(original);
        await host.Editing.SaveAsync(id, new EditRecipe { Crop = new NormalizedRect(0, 0, 0.5, 1) }, CancellationToken.None);
        await host.Organization.AddKeywordsAsync([id], ["costa"], CancellationToken.None);
        var outDir = Path.Combine(host.Root, "export");

        var results = await host.Export.ExportAsync([id], outDir, new ExportOptions(), null, CancellationToken.None);
        var file = results.Single().OutputPaths.Single();
        Assert.Null(results.Single().Error);
        Assert.EndsWith("edit_me.jpg", file);
        using (var bmp = SKBitmap.Decode(file)) Assert.Equal((200, 300), (bmp.Width, bmp.Height));

        var dirs = ImageMetadataReader.ReadMetadata(file);
        var ifd0 = dirs.OfType<ExifIfd0Directory>().Single();
        Assert.Equal(1, ifd0.GetInt32(ExifDirectoryBase.TagOrientation));
        Assert.Equal("Acme Z100", ifd0.GetString(ExifDirectoryBase.TagModel));
        Assert.Equal("2023:07:14 18:30:05", dirs.OfType<ExifSubIfdDirectory>().Single().GetString(ExifDirectoryBase.TagDateTimeOriginal));
        Assert.Empty(dirs.OfType<GpsDirectory>()); // location is opt-in
        var xmp = dirs.OfType<XmpDirectory>().Single().GetXmpProperties();
        Assert.Contains(xmp, p => p.Value == "Original title");
        Assert.Contains(xmp, p => p.Value == "costa");
        Assert.Equal(hash, Fixtures.Sha256(original));

        // Second export never overwrites the first one.
        var again = await host.Export.ExportAsync([id], outDir, new ExportOptions { IncludeLocation = true, Format = OutputFormat.Jpeg }, null, CancellationToken.None);
        var second = again.Single().OutputPaths.Single();
        Assert.EndsWith("edit_me (1).jpg", second);
        var gps = ImageMetadataReader.ReadMetadata(second).OfType<GpsDirectory>().Single();
        Assert.True(gps.TryGetGeoLocation(out var loc));
        Assert.Equal(43.36, loc.Latitude, 3);
        Assert.Equal(-5.85, loc.Longitude, 3);
    }

    [Fact]
    public async Task Export_without_metadata_strips_it_and_resizes()
    {
        var (host, id, _) = await SeedAsync();
        await using var __ = host;
        var results = await host.Export.ExportAsync([id], Path.Combine(host.Root, "x"), new ExportOptions { IncludeMetadata = false, IncludeDescriptive = false, MaxLongSide = 100 }, null, CancellationToken.None);
        var file = results.Single().OutputPaths.Single();
        var dirs = ImageMetadataReader.ReadMetadata(file);
        Assert.Empty(dirs.OfType<ExifIfd0Directory>());
        Assert.Empty(dirs.OfType<XmpDirectory>());
        using var bmp = SKBitmap.Decode(file);
        Assert.Equal(100, bmp.Width);
    }

    [Fact]
    public async Task Original_export_is_byte_identical_and_png_export_works()
    {
        var (host, id, original) = await SeedAsync();
        await using var _ = host;
        var outDir = Path.Combine(host.Root, "orig");
        var results = await host.Export.ExportAsync([id], outDir, new ExportOptions { Kind = ExportKind.Original }, null, CancellationToken.None);
        Assert.Equal(Fixtures.Sha256(original), Fixtures.Sha256(results.Single().OutputPaths.Single()));

        var png = await host.Export.ExportAsync([id], outDir, new ExportOptions { Format = OutputFormat.Png }, null, CancellationToken.None);
        Assert.EndsWith(".png", png.Single().OutputPaths.Single());
    }

    [Fact]
    public async Task Export_reports_per_item_errors_and_refuses_library_folder()
    {
        var (host, id, original) = await SeedAsync();
        await using var _ = host;
        await Assert.ThrowsAsync<ArgumentException>(() => host.Export.ExportAsync([id], host.Get<ILibraryContext>().Current!.RootPath, new ExportOptions(), null, CancellationToken.None));
        File.Delete(original);
        var results = await host.Export.ExportAsync([id, 9999], Path.Combine(host.Root, "y"), new ExportOptions(), null, CancellationToken.None);
        Assert.All(results, r => Assert.NotNull(r.Error));
    }
}
