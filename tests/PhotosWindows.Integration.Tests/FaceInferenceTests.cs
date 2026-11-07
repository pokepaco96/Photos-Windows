using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PhotosWindows.Application.Abstractions;
using PhotosWindows.Application.Import;
using PhotosWindows.Application.People;
using PhotosWindows.Domain;
using PhotosWindows.Integration.Tests.Support;
using PhotosWindows.MachineLearning;
using SkiaSharp;
using Xunit.Abstractions;

namespace PhotosWindows.Integration.Tests;

/// <summary>
/// Real inference with the pinned YuNet + SFace models on public-domain NASA portraits. Skipped (blocked) when the
/// models or photos cannot be downloaded.
/// </summary>
[Trait("Category", "Inference")]
public sealed class FaceInferenceTests : IClassFixture<RealModelAssets>
{
    private readonly RealModelAssets _assets;
    private readonly ITestOutputHelper _output;

    public FaceInferenceTests(RealModelAssets assets, ITestOutputHelper output)
    {
        _assets = assets;
        _output = output;
    }

    private (YuNetFaceDetector Detector, SFaceEmbedder Embedder) Models() =>
        (new YuNetFaceDetector(_assets.Models!, NullLogger<YuNetFaceDetector>.Instance), new SFaceEmbedder(_assets.Models!, NullLogger<SFaceEmbedder>.Instance));

    private static async Task<RasterImage> DecodeAsync(string path)
    {
        await using var host = new TestHost();
        return await host.Get<IImageRenderer>().DecodeOrientedAsync(path, FormatFamily.Jpeg, PeopleService.AnalysisLongSide, CancellationToken.None);
    }

    [SkippableFact]
    public async Task Detects_one_face_per_portrait_and_both_faces_of_the_crew_photo()
    {
        _assets.RequireAvailable();
        var (detector, _) = Models();
        foreach (var photo in RealModelAssets.Photos)
        {
            var image = await DecodeAsync(_assets.PathOf(photo));
            var faces = await detector.DetectAsync(image, CancellationToken.None);
            _output.WriteLine($"{photo.Name}: {faces.Count} face(s) {string.Join(", ", faces.Select(f => $"[{f.Box.X:0.00},{f.Box.Y:0.00} {f.Box.Width:0.00}×{f.Box.Height:0.00} {f.Score:0.00}]"))}");
            Assert.Equal(photo == RealModelAssets.Gemini9 ? 2 : 1, faces.Count);
            Assert.All(faces, f => Assert.True(f.Box.IsValid));
            Assert.All(faces, f => Assert.Equal(5, f.Landmarks.Count));
        }
    }

    [SkippableFact]
    public async Task Same_person_years_apart_is_far_more_similar_than_different_people()
    {
        _assets.RequireAvailable();
        var (detector, embedder) = Models();
        var vectors = new Dictionary<string, float[]>();
        foreach (var photo in RealModelAssets.Photos)
        {
            var image = await DecodeAsync(_assets.PathOf(photo));
            var faces = (await detector.DetectAsync(image, CancellationToken.None)).OrderBy(f => f.Box.X).ToList();
            for (var i = 0; i < faces.Count; i++)
            {
                vectors[faces.Count == 1 ? photo.Name : $"{photo.Name}#{i}"] = await embedder.EmbedAsync(image, faces[i], CancellationToken.None);
            }
        }

        float Sim(string a, string b) => FaceClustering.Similarity(vectors[a], vectors[b]);
        var lee = Sim("lee_1984.jpg", "lee_1993.jpg");
        var cernan = Sim("cernan_1964.jpg", "gemini9_crew.jpg#1"); // Cernan is on the right
        _output.WriteLine($"Lee 1984 vs 1993: {lee:0.000}; Cernan 1964 vs 1966: {cernan:0.000}");
        Assert.True(lee > 0.6f, $"Lee {lee}");
        Assert.True(cernan > 0.6f, $"Cernan {cernan}");

        var different = new[]
        {
            ("lee_1984.jpg", "cernan_1964.jpg"), ("lee_1993.jpg", "thornton_1992.jpg"), ("cernan_1964.jpg", "gemini9_crew.jpg#0"),
            ("gemini9_crew.jpg#0", "gemini9_crew.jpg#1"), ("thornton_1992.jpg", "gemini9_crew.jpg#0"), ("lee_1984.jpg", "gemini9_crew.jpg#1"),
        };
        foreach (var (a, b) in different)
        {
            var s = Sim(a, b);
            _output.WriteLine($"{a} vs {b}: {s:0.000}");
            Assert.True(s < new ClusteringOptions().JoinExistingThreshold, $"{a} vs {b}: {s}");
        }
    }

    [SkippableFact]
    public async Task Rotated_photo_gives_the_same_face_in_display_coordinates()
    {
        _assets.RequireAvailable();
        var (detector, _) = Models();
        var upright = await DecodeAsync(_assets.PathOf(RealModelAssets.Thornton1992));
        var reference = Assert.Single(await detector.DetectAsync(upright, CancellationToken.None));

        // Store the pixels turned 90° counter-clockwise with EXIF orientation 6 ("rotate 90° clockwise to display").
        var rotatedPath = Path.Combine(Path.GetTempPath(), $"pw-rot-{Guid.NewGuid():N}.jpg");
        try
        {
            using (var source = SKBitmap.Decode(_assets.PathOf(RealModelAssets.Thornton1992)))
            using (var stored = new SKBitmap(source.Height, source.Width))
            {
                using (var canvas = new SKCanvas(stored))
                {
                    canvas.Translate(0, source.Width);
                    canvas.RotateDegrees(-90);
                    using var img = SKImage.FromBitmap(source);
                    canvas.DrawImage(img, 0, 0, new SKSamplingOptions(SKFilterMode.Linear));
                }

                using var data = SKImage.FromBitmap(stored).Encode(SKEncodedImageFormat.Jpeg, 95);
                var bytes = PhotosWindows.Application.Export.JpegMetadataWriter.Inject(data.ToArray(),
                    new PhotosWindows.Application.Export.ExportMetadata(null, new CameraInfo(null, null, null, null, null, null, null), null, stored.Width, stored.Height, null, null, [], 0),
                    includeLocation: false, includeDescriptive: false, orientation: 6);
                await File.WriteAllBytesAsync(rotatedPath, bytes);
            }

            var rotated = await DecodeAsync(rotatedPath);
            Assert.Equal(upright.Width, rotated.Width);
            var face = Assert.Single(await detector.DetectAsync(rotated, CancellationToken.None));
            Assert.True(face.Box.IntersectionOverUnion(reference.Box) > 0.8f, $"IoU {face.Box.IntersectionOverUnion(reference.Box)}");
        }
        finally
        {
            File.Delete(rotatedPath);
        }
    }

    [SkippableFact]
    public async Task No_face_in_a_synthetic_photo_and_a_partial_face_does_not_break_analysis()
    {
        _assets.RequireAvailable();
        var (detector, embedder) = Models();
        var empty = Path.Combine(Path.GetTempPath(), $"pw-empty-{Guid.NewGuid():N}.jpg");
        try
        {
            Fixtures.Jpeg(empty, new Fixtures.PhotoSpec { Width = 800, Height = 600, Seed = 3 });
            Assert.Empty(await detector.DetectAsync(await DecodeAsync(empty), CancellationToken.None));
        }
        finally
        {
            File.Delete(empty);
        }

        // Left half of a portrait: whatever is found must be valid and embeddable.
        var full = await DecodeAsync(_assets.PathOf(RealModelAssets.Lee1993));
        var half = new RasterImage(full.Width / 2, full.Height, CropColumns(full, full.Width / 2));
        foreach (var face in await detector.DetectAsync(half, CancellationToken.None))
        {
            Assert.True(face.Box.IsValid);
            Assert.Equal(128, (await embedder.EmbedAsync(half, face, CancellationToken.None)).Length);
        }
    }

    [SkippableFact]
    public async Task End_to_end_groups_the_same_people_and_measures_speed()
    {
        _assets.RequireAvailable();
        var models = _assets.Models!;
        await using var host = new TestHost(s =>
        {
            s.AddSingleton(models);
            s.AddSingleton<IFaceModelManager>(models);
            s.AddSingleton<IFaceDetector, YuNetFaceDetector>();
            s.AddSingleton<IFaceEmbedder, SFaceEmbedder>();
        });
        await host.CreateLibraryAsync();
        var dir = host.SourceDir("nasa");
        foreach (var photo in RealModelAssets.Photos) File.Copy(_assets.PathOf(photo), Path.Combine(dir, photo.Name));
        await host.ImportAsync(dir, ImportMode.AddInPlace);
        var people = host.Get<PeopleService>();
        Assert.True(people.ModelsReady);
        await people.EnableAsync(CancellationToken.None);

        var watch = Stopwatch.StartNew();
        var result = await people.AnalyzeAsync(null, null, CancellationToken.None);
        watch.Stop();
        _output.WriteLine($"Analysed {result.Analyzed} photo(s), {result.FacesFound} face(s) in {watch.ElapsedMilliseconds} ms " +
                          $"({watch.ElapsedMilliseconds / Math.Max(1, result.Analyzed)} ms/photo incl. decoding at {PeopleService.AnalysisLongSide} px, {Environment.ProcessorCount} logical CPUs).");

        Assert.Equal(5, result.Analyzed);
        Assert.Equal(6, result.FacesFound);
        var groups = await people.GetPeopleAsync(CancellationToken.None);
        var assets = (await host.AllAsync()).ToDictionary(a => a.Id, a => a.FileName);
        var members = new List<string[]>();
        foreach (var g in groups)
        {
            members.Add((await people.GetFacesOfPersonAsync(g.Id, CancellationToken.None)).Select(f => assets[f.AssetId]).Order().ToArray());
        }

        _output.WriteLine("Groups: " + string.Join(" | ", members.Select(m => string.Join(", ", m))));
        Assert.Equal(2, groups.Count);
        Assert.All(groups, g => Assert.Null(g.Name));
        Assert.Contains(members, m => m.SequenceEqual(["lee_1984.jpg", "lee_1993.jpg"]));
        Assert.Contains(members, m => m.SequenceEqual(["cernan_1964.jpg", "gemini9_crew.jpg"]));
        Assert.Equal(2, (await people.GetStatsAsync(CancellationToken.None)).Ungrouped); // Stafford, Thornton

        // The originals are read only.
        foreach (var photo in RealModelAssets.Photos) Assert.Equal(photo.Sha256, Fixtures.Sha256(Path.Combine(dir, photo.Name)));
    }

    [Fact]
    public async Task Model_files_that_do_not_match_their_checksum_are_never_used()
    {
        var appData = Path.Combine(Path.GetTempPath(), "pw-models-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var manager = new FaceModelManager(new PhotosWindows.Infrastructure.Settings.DefaultAppPaths(appData), NullLogger<FaceModelManager>.Instance);
            Directory.CreateDirectory(manager.Folder);
            var fake = Path.Combine(manager.Folder, FaceModelCatalog.YuNet.FileName);
            await File.WriteAllBytesAsync(fake, new byte[FaceModelCatalog.YuNet.SizeBytes]);
            Assert.Equal(ModelState.Invalid, manager.GetStatus(FaceModelCatalog.YuNet).State);
            Assert.False(manager.AllInstalled);
            Assert.False(new YuNetFaceDetector(manager, NullLogger<YuNetFaceDetector>.Instance).IsReady);

            var other = Path.Combine(appData, "random.onnx");
            await File.WriteAllBytesAsync(other, [1, 2, 3]);
            Assert.Empty(await manager.InstallFromFilesAsync([other], CancellationToken.None));

            manager.Uninstall();
            Assert.False(File.Exists(fake));
        }
        finally
        {
            Directory.Delete(appData, recursive: true);
        }
    }

    [SkippableFact]
    public async Task Models_can_be_installed_from_local_files_offline()
    {
        _assets.RequireAvailable();
        var appData = Path.Combine(Path.GetTempPath(), "pw-models-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var manager = new FaceModelManager(new PhotosWindows.Infrastructure.Settings.DefaultAppPaths(appData), NullLogger<FaceModelManager>.Instance);
            var sources = _assets.Models!.GetStatus().Select(s => s.Path!).ToList();
            var installed = await manager.InstallFromFilesAsync(sources, CancellationToken.None);
            Assert.Equal(["yunet-2023mar", "sface-2021dec"], installed);
            Assert.True(manager.AllInstalled);
        }
        finally
        {
            Directory.Delete(appData, recursive: true);
        }
    }

    private static byte[] CropColumns(RasterImage image, int width)
    {
        var pixels = new byte[width * image.Height * 4];
        for (var y = 0; y < image.Height; y++) Buffer.BlockCopy(image.Pixels, y * image.Width * 4, pixels, y * width * 4, width * 4);
        return pixels;
    }
}
