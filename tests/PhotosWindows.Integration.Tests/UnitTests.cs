using PhotosWindows.Application.Common;
using PhotosWindows.Application.Duplicates;
using PhotosWindows.Application.Editing;
using PhotosWindows.Application.Export;
using PhotosWindows.Application.Import;
using PhotosWindows.Application.Search;
using PhotosWindows.Domain;
using PhotosWindows.Infrastructure.Metadata;
using PhotosWindows.Infrastructure.Persistence;

namespace PhotosWindows.Integration.Tests;

public class DateParsingTests
{
    [Theory]
    [InlineData("2023:07:14 18:30:05", 2023, 7, 14, 18, 30, 5, 0)]
    [InlineData("2023-07-14T18:30:05", 2023, 7, 14, 18, 30, 5, 0)]
    [InlineData("2023:07:14 18:30:05.250", 2023, 7, 14, 18, 30, 5, 250)]
    [InlineData("2023:07:14 18:30", 2023, 7, 14, 18, 30, 0, 0)]
    [InlineData("2023:07:14 18:30:05\0", 2023, 7, 14, 18, 30, 5, 0)]
    public void Parses_exif_variants(string text, int y, int mo, int d, int h, int mi, int s, int ms)
    {
        Assert.True(DateParsing.TryParseExifDateTime(text, out var dt));
        Assert.Equal(new DateTime(y, mo, d, h, mi, s, ms), dt);
        Assert.Equal(DateTimeKind.Unspecified, dt.Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0000:00:00 00:00:00")]
    [InlineData("    :  :     :  :  ")]
    [InlineData("2023:13:40 99:00:00")]
    [InlineData("1700:01:01 00:00:00")]
    public void Rejects_invalid_or_placeholder_dates(string? text) => Assert.False(DateParsing.TryParseExifDateTime(text, out _));

    [Theory]
    [InlineData("+02:00", 120)]
    [InlineData("-05:30", -330)]
    [InlineData("+0100", 60)]
    [InlineData("Z", 0)]
    public void Parses_offsets(string text, int minutes)
    {
        Assert.True(DateParsing.TryParseOffset(text, out var off));
        Assert.Equal(TimeSpan.FromMinutes(minutes), off);
    }

    [Fact]
    public void Rejects_bad_offsets()
    {
        Assert.False(DateParsing.TryParseOffset("+25:00", out _));
        Assert.False(DateParsing.TryParseOffset("02:00", out _));
    }

    [Fact]
    public void Infers_offset_from_gps_utc_and_rejects_drift()
    {
        Assert.Equal(TimeSpan.FromHours(2), DateParsing.InferOffset(new DateTime(2023, 7, 14, 18, 30, 5), new DateTime(2023, 7, 14, 16, 31, 0)));
        Assert.Equal(TimeSpan.FromMinutes(330), DateParsing.InferOffset(new DateTime(2023, 1, 1, 12, 0, 0), new DateTime(2023, 1, 1, 6, 30, 0)));
        Assert.Null(DateParsing.InferOffset(new DateTime(2023, 1, 1, 12, 0, 0), new DateTime(2023, 1, 1, 11, 37, 0)));
    }

    [Theory]
    [InlineData("IMG_20230415_142233.jpg", "2023-04-15 14:22:33")]
    [InlineData("PXL_20230415_142233123.jpg", "2023-04-15 14:22:33")]
    [InlineData("2023-04-15 14.22.33.jpg", "2023-04-15 14:22:33")]
    [InlineData("Screenshot_20230415-142233.png", "2023-04-15 14:22:33")]
    [InlineData("IMG-20230415-WA0001.jpg", "2023-04-15 12:00:00")]
    public void Parses_file_name_dates(string name, string expected)
    {
        Assert.True(DateParsing.TryParseFromFileName(name, out var dt));
        Assert.Equal(DateTime.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), dt);
    }

    [Fact]
    public void Ignores_names_without_dates() => Assert.False(DateParsing.TryParseFromFileName("IMG_1234.jpg", out _));
}

public class SearchParserTests
{
    [Fact]
    public void Extracts_structured_tokens()
    {
        var q = SearchService.Parse("vídeos 2021 playa favoritos");
        Assert.Equal(MediaType.Video, q.MediaType);
        Assert.True(q.Favorite);
        Assert.Equal(new DateTime(2021, 1, 1), q.From);
        Assert.Equal(new DateTime(2022, 1, 1), q.To);
        Assert.Equal("playa", q.Text);
    }

    [Fact]
    public void Month_and_day_tokens_and_invalid_dates()
    {
        Assert.Equal(new DateTime(2022, 2, 1), SearchService.Parse("2022-02").From);
        Assert.Equal(new DateTime(2022, 2, 28), SearchService.Parse("2022-02-28").From);
        Assert.Equal("2022-02-30", SearchService.Parse("2022-02-30").Text);
        Assert.Null(SearchService.Parse("  ").Text);
    }

    [Fact]
    public void Fts_expression_is_sanitized()
    {
        Assert.Equal("\"img\"* \"1234\"*", AssetQuerySql.BuildFtsExpression("IMG_1234".ToLowerInvariant()));
        Assert.Null(AssetQuerySql.BuildFtsExpression("\"*()"));
        Assert.Equal("\"near\"*", AssetQuerySql.BuildFtsExpression("NEAR(".ToLowerInvariant()));
        Assert.Equal("%50\\%\\_x%", AssetQuerySql.Like("50%_x"));
    }
}

public class PlannerAndSessionTests
{
    [Fact]
    public void Planner_groups_companions_and_prefers_processed_primary()
    {
        var units = ImportPlanner.Plan([
            "/d/IMG_1.CR2", "/d/IMG_1.JPG", "/d/IMG_1.MOV", "/d/IMG_1.CR2.xmp",
            "/d/IMG_2.HEIC", "/d/IMG_2.MOV",
            "/d/VID_3.mp4",
            "/d/RAW_4.NEF",
            "/e/IMG_1.JPG",
        ]);
        var first = units.Single(u => u.Primary == "/d/IMG_1.JPG");
        Assert.Equal(("/d/IMG_1.CR2", "/d/IMG_1.MOV", "/d/IMG_1.CR2.xmp"), (first.RawPair, first.LiveVideoCandidate, first.Sidecar));
        Assert.Equal("/d/IMG_2.MOV", units.Single(u => u.Primary == "/d/IMG_2.HEIC").LiveVideoCandidate);
        Assert.Contains(units, u => u.Primary == "/d/VID_3.mp4" && u.LiveVideoCandidate is null);
        Assert.Contains(units, u => u.Primary == "/d/RAW_4.NEF" && u.RawPair is null);
        Assert.Contains(units, u => u.Primary == "/e/IMG_1.JPG" && u.RawPair is null);
        Assert.Equal(5, units.Count);
    }

    [Fact]
    public void Edit_session_undo_redo_and_coalescing()
    {
        var s = new EditSession(1, EditRecipe.Identity);
        s.Apply(new EditRecipe { Exposure = 0.1 }, "exposure");
        s.Apply(new EditRecipe { Exposure = 0.2 }, "exposure");
        s.Apply(new EditRecipe { Exposure = 0.3 }, "exposure");
        s.EndGesture();
        s.Apply(s.Current with { Contrast = 0.5 }, "contrast");
        Assert.True(s.IsDirty);
        Assert.True(s.Undo());
        Assert.Equal(0.3, s.Current.Exposure);
        Assert.Equal(0, s.Current.Contrast);
        Assert.True(s.Undo());
        Assert.True(s.Current.IsIdentity);
        Assert.False(s.Undo());
        Assert.True(s.Redo());
        Assert.Equal(0.3, s.Current.Exposure);
        s.Apply(s.Current with { Saturation = 0.2 });
        Assert.False(s.CanRedo);
    }

    [Fact]
    public void Similar_clusters_respect_threshold_and_dismissals()
    {
        var hashes = new List<(long, ulong)> { (1, 0b0000UL), (2, 0b0111UL), (3, ulong.MaxValue), (4, 0b1UL) };
        var clusters = DuplicateService.FindSimilarClusters(hashes, 3, new HashSet<(long, long)>(), CancellationToken.None);
        var c = Assert.Single(clusters);
        Assert.Equal([1L, 2L, 4L], c.Ids);
        var dismissed = new HashSet<(long, long)> { (1, 2), (1, 4), (2, 4) };
        Assert.Empty(DuplicateService.FindSimilarClusters(hashes, 3, dismissed, CancellationToken.None));
    }

    [Fact]
    public void Path_helpers_sanitize_and_never_reuse_names()
    {
        Assert.Equal("a_b_c", PathSafety.SanitizeFileName("a:b?c"));
        Assert.Equal("untitled", PathSafety.SanitizeFileName("..."));
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "x.jpg"), "");
            File.WriteAllText(Path.Combine(dir, "x (1).jpg.pwpartial"), "");
            Assert.Equal(Path.Combine(dir, "x (2).jpg"), PathSafety.UniquePath(dir, "x.jpg"));
            Assert.True(PathSafety.IsUnder(Path.Combine(dir, "a", "..", "b"), dir));
            Assert.False(PathSafety.IsUnder(dir + "-other", dir));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Iso6709_locations_are_parsed()
    {
        Assert.True(MetadataExtractorReader.TryParseIso6709("+40.4168-003.7038+650.000/", out var loc));
        Assert.Equal(40.4168, loc.Latitude, 4);
        Assert.Equal(-3.7038, loc.Longitude, 4);
        Assert.Equal(650, loc.Altitude);
        Assert.False(MetadataExtractorReader.TryParseIso6709("garbage", out _));
    }

    [Fact]
    public void Jpeg_metadata_writer_rejects_non_jpeg_and_strip_keeps_image()
    {
        Assert.Throws<ArgumentException>(() => JpegMetadataWriter.Inject([1, 2, 3, 4], new ExportMetadata(null, CameraInfo.Empty, null, 1, 1, null, null, [], 0), false, false));
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE1, 0, 4, 1, 2, 0xFF, 0xDA, 0, 2, 9, 9, 0xFF, 0xD9];
        Assert.Equal(new byte[] { 0xFF, 0xD8, 0xFF, 0xDA, 0, 2, 9, 9, 0xFF, 0xD9 }, JpegMetadataWriter.StripMetadata(jpeg));
        Assert.Null(JpegMetadataWriter.BuildXmp(new ExportMetadata(null, CameraInfo.Empty, null, 1, 1, null, null, [], 0)));
    }
}
