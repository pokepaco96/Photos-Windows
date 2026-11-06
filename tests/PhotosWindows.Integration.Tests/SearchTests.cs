using PhotosWindows.Domain;
using PhotosWindows.Integration.Tests.Support;

namespace PhotosWindows.Integration.Tests;

public class SearchTests
{
    private static async Task<TestHost> SeedAsync()
    {
        var host = new TestHost();
        await host.CreateLibraryAsync();
        var dir = host.SourceDir("s");
        Fixtures.Jpeg(Path.Combine(dir, "IMG_1234.jpg"), new Fixtures.PhotoSpec { Seed = 1, Taken = new DateTime(2021, 8, 3, 10, 0, 0), Model = "Pixelmaker 7", Make = "Pix" });
        Fixtures.Jpeg(Path.Combine(dir, "DSC_0001.jpg"), new Fixtures.PhotoSpec { Seed = 2, Taken = new DateTime(2023, 1, 9, 9, 0, 0), Keywords = ["montaña", "nieve"], Title = "Pico de Europa" });
        Fixtures.QuickTime(Path.Combine(dir, "clip.mov"), TimeSpan.FromSeconds(20), new DateTime(2023, 1, 9, 12, 0, 0, DateTimeKind.Utc));
        await host.ImportAsync(dir, ImportMode.AddInPlace);
        return host;
    }

    private static async Task<string[]> NamesAsync(TestHost host, string text) =>
        (await host.Search.SearchAsync(text, null, CancellationToken.None)).Select(s => s.FileName).Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public async Task Finds_by_file_name_keyword_title_camera_year_and_type()
    {
        await using var host = await SeedAsync();
        Assert.Equal(["IMG_1234.jpg"], await NamesAsync(host, "IMG_1234"));
        Assert.Equal(["IMG_1234.jpg"], await NamesAsync(host, "1234"));
        Assert.Equal(["DSC_0001.jpg"], await NamesAsync(host, "montana")); // diacritics-insensitive
        Assert.Equal(["DSC_0001.jpg"], await NamesAsync(host, "pico eur")); // prefix terms, AND
        Assert.Equal(["IMG_1234.jpg"], await NamesAsync(host, "pixelmaker"));
        Assert.Equal(["DSC_0001.jpg", "clip.mov"], await NamesAsync(host, "2023"));
        Assert.Equal(["clip.mov"], await NamesAsync(host, "video 2023"));
        Assert.Equal(["DSC_0001.jpg"], await NamesAsync(host, "fotos 2023-01"));
        Assert.Equal(["DSC_0001.jpg", "clip.mov"], await NamesAsync(host, "2023-01-09"));
        Assert.Empty(await NamesAsync(host, "nonexistentword"));
    }

    [Fact]
    public async Task Album_names_and_edited_titles_are_searchable()
    {
        await using var host = await SeedAsync();
        var img = (await host.AllAsync()).Single(s => s.FileName == "IMG_1234.jpg");
        await host.Organization.CreateAlbumAsync("Verano Asturias", null, [img.Id], CancellationToken.None);
        Assert.Equal(["IMG_1234.jpg"], await NamesAsync(host, "asturias"));
        await host.Organization.UpdateDetailsAsync(img.Id, new(Title: "Faro al atardecer", null, 0, null), CancellationToken.None);
        Assert.Equal(["IMG_1234.jpg"], await NamesAsync(host, "atardecer"));
    }

    [Theory]
    [InlineData("\"")]
    [InlineData("* OR NEAR(")]
    [InlineData("a:b")]
    [InlineData("'; DROP TABLE assets; --")]
    [InlineData("   ")]
    public async Task Hostile_or_odd_input_never_throws(string text)
    {
        await using var host = await SeedAsync();
        var results = await host.Search.SearchAsync(text, null, CancellationToken.None);
        Assert.NotNull(results);
        Assert.Equal(3, (await host.AllAsync()).Count);
    }

    [Fact]
    public async Task Filters_combine_with_text()
    {
        await using var host = await SeedAsync();
        var favorites = await host.Search.SearchAsync("2023", new AssetQuery { Favorite = true }, CancellationToken.None);
        Assert.Empty(favorites);
        var cameras = await host.Search.GetFilterValuesAsync(SmartField.Camera, CancellationToken.None);
        Assert.Contains("Pixelmaker 7", cameras.Select(c => c.Replace("Pix ", "", StringComparison.Ordinal)));
        var byCamera = await host.Search.SearchAsync(null, new AssetQuery { Camera = "pixelmaker" }, CancellationToken.None);
        Assert.Single(byCamera);
    }
}
