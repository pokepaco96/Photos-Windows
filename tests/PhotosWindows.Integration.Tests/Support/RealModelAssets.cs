using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using PhotosWindows.Application.Abstractions;
using PhotosWindows.Infrastructure.Settings;
using PhotosWindows.MachineLearning;

namespace PhotosWindows.Integration.Tests.Support;

/// <summary>A public-domain test photo, downloaded at test time and checked against its SHA-256.</summary>
public sealed record TestPhoto(string Name, Uri Source, string Sha256, string Description);

/// <summary>
/// Real models and real faces for inference tests. Nothing here is committed to the repository: the face models come
/// from their pinned sources (same as the app) and the photos are NASA portraits, works of the US federal government in
/// the public domain (https://www.nasa.gov/nasa-brand-center/images-and-media/). Used only to test detection and
/// grouping, never stored outside the test's temporary folder.
/// When the files cannot be downloaded the tests are skipped and reported as blocked: never as passed.
/// </summary>
public sealed class RealModelAssets : IAsyncLifetime
{
    public static readonly TestPhoto Lee1984 = new("lee_1984.jpg", new Uri("https://images-assets.nasa.gov/image/S84-40242/S84-40242~medium.jpg"),
        "04174570dbdaefce63e17002486a7bd784186b875b187a9a7766c7db78407a26", "NASA S84-40242, astronaut Mark C. Lee, 1984");

    public static readonly TestPhoto Lee1993 = new("lee_1993.jpg", new Uri("https://images-assets.nasa.gov/image/S93-48446/S93-48446~medium.jpg"),
        "44b2a3be6d243a1bdaa66b7c8cf71be4c46c2ae8eca620b25323232afc00f26e", "NASA S93-48446, astronaut Mark C. Lee, 1993");

    public static readonly TestPhoto Cernan1964 = new("cernan_1964.jpg", new Uri("https://images-assets.nasa.gov/image/s64-31845/s64-31845~medium.jpg"),
        "2e6f22935d79525080ec9d217ccab80d520c0b61d01758415be5456bc23159cd", "NASA S64-31845, astronaut Eugene A. Cernan, 1964");

    public static readonly TestPhoto Gemini9 = new("gemini9_crew.jpg", new Uri("https://images-assets.nasa.gov/image/s66-15621/s66-15621~medium.jpg"),
        "a87f1c698d3b3ef65b6b316339c60fd2dce3077cf2aa01e1b17159b642016d98", "NASA S66-15621, Gemini 9 crew: Thomas P. Stafford (left) and Eugene A. Cernan (right), 1966");

    public static readonly TestPhoto Thornton1992 = new("thornton_1992.jpg", new Uri("https://images-assets.nasa.gov/image/S92-42897/S92-42897~medium.jpg"),
        "0753e8912a68b3e6d5870008b1df41dba272ef675e43bfffb1d47eae2bc8f51e", "NASA S92-42897, astronaut Kathryn C. Thornton, 1992");

    public static IReadOnlyList<TestPhoto> Photos { get; } = [Lee1984, Lee1993, Cernan1964, Gemini9, Thornton1992];

    /// <summary>Shared between test runs on the same machine to avoid downloading again.</summary>
    public string CacheRoot { get; } = Environment.GetEnvironmentVariable("PW_TEST_ASSETS") is { Length: > 0 } dir
        ? dir
        : Path.Combine(Path.GetTempPath(), "photoswindows-test-assets");

    public string AppData => Path.Combine(CacheRoot, "appdata");

    public string PhotoFolder => Path.Combine(CacheRoot, "nasa");

    public string? BlockedReason { get; private set; }

    public FaceModelManager? Models { get; private set; }

    public string PathOf(TestPhoto photo) => Path.Combine(PhotoFolder, photo.Name);

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(PhotoFolder);
        try
        {
            Models = new FaceModelManager(new DefaultAppPaths(AppData), NullLogger<FaceModelManager>.Instance);
            if (!Models.AllInstalled) await Models.InstallAsync(null, CancellationToken.None);
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("PhotosWindows-tests/0.1");
            foreach (var photo in Photos)
            {
                var path = PathOf(photo);
                if (File.Exists(path) && Sha(path) == photo.Sha256) continue;
                var bytes = await http.GetByteArrayAsync(photo.Source);
                if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != photo.Sha256)
                {
                    BlockedReason = $"{photo.Name} downloaded with an unexpected checksum.";
                    return;
                }

                await File.WriteAllBytesAsync(path, bytes);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidDataException)
        {
            BlockedReason = $"Blocked: models or test photos could not be downloaded ({ex.GetType().Name}: {ex.Message}).";
        }
    }

    public Task DisposeAsync()
    {
        Models?.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Skips (blocked) when the assets are not available.</summary>
    public void RequireAvailable() => Skip.If(BlockedReason is not null || Models is null || !Models.AllInstalled, BlockedReason ?? "Blocked: face models are not installed.");

    private static string Sha(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(s));
    }
}
