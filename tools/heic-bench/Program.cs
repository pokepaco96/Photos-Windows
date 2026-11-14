using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PhotosWindows.Domain;
using PhotosWindows.Platform.Windows;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

// WIC / HEIC benchmark for Windows. Times each WIC stage separately, compares output sizes, checks that the scaled
// output really has the requested size, and tests whether cancelling GetPixelDataAsync stops the work.
//   HeicBench.exe <fixtures dir> [runs=10] [report.json]
var dir = Path.GetFullPath(args.Length > 0 ? args[0] : throw new ArgumentException("fixtures dir required")); // StorageFile needs a normalized Windows path
var runs = args.Length > 1 ? int.Parse(args[1]) : 10;
var reportPath = args.Length > 2 ? args[2] : null;
var proc = Process.GetCurrentProcess();

var codecs = BitmapDecoder.GetDecoderInformationEnumerator()
    .Select(c => new { c.FriendlyName, CodecId = c.CodecId.ToString(), Extensions = string.Join(" ", c.FileExtensions) })
    .ToList();
var heifCodec = codecs.FirstOrDefault(c => c.Extensions.Contains(".heic", StringComparison.OrdinalIgnoreCase));
Console.WriteLine($"HEIF decoder: {(heifCodec is null ? "NOT INSTALLED" : $"{heifCodec.FriendlyName} ({heifCodec.CodecId})")}");

var results = new List<object>();
var wic = new WicRasterDecoder(NullLogger<WicRasterDecoder>.Instance);
foreach (var file in Directory.EnumerateFiles(dir, "*.heic").Select(Path.GetFullPath).Order())
{
    var name = Path.GetFileName(file);
    foreach (var target in new int?[] { 360, 1280, 2048, null })
    {
        var stages = new Dictionary<string, List<double>>();
        void Add(string k, double ms) => (stages.TryGetValue(k, out var l) ? l : stages[k] = []).Add(ms);
        string? error = null;
        int outW = 0, outH = 0;
        long bytes = 0;
        double cpuMs = 0;
        long peakPrivate = 0;
        for (var i = 0; i < runs && error is null; i++)
        {
            var cpu0 = proc.TotalProcessorTime;
            var sw = Stopwatch.StartNew();
            try
            {
                var t = Stopwatch.StartNew();
                var sf = await StorageFile.GetFileFromPathAsync(file);
                Add("getFile", t.Elapsed.TotalMilliseconds); t.Restart();
                using IRandomAccessStream stream = await sf.OpenAsync(FileAccessMode.Read);
                Add("open", t.Elapsed.TotalMilliseconds); t.Restart();
                var decoder = await BitmapDecoder.CreateAsync(stream);
                Add("createDecoder", t.Elapsed.TotalMilliseconds); t.Restart();
                var (w, h) = (decoder.PixelWidth, decoder.PixelHeight);
                var transform = new BitmapTransform { InterpolationMode = BitmapInterpolationMode.Fant };
                if (target is { } tg && Math.Max(w, h) > tg)
                {
                    var s = tg / (double)Math.Max(w, h);
                    transform.ScaledWidth = (uint)Math.Round(w * s);
                    transform.ScaledHeight = (uint)Math.Round(h * s);
                }

                var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, transform,
                    ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.ColorManageToSRgb);
                Add("getPixelData", t.Elapsed.TotalMilliseconds); t.Restart();
                var data = pixels.DetachPixelData();
                Add("detach", t.Elapsed.TotalMilliseconds);
                bytes = data.LongLength;
                outW = (int)(transform.ScaledWidth != 0 ? transform.ScaledWidth : w);
                outH = (int)(transform.ScaledHeight != 0 ? transform.ScaledHeight : h);
                if (bytes != (long)outW * outH * 4) error = $"returned {bytes} bytes for {outW}x{outH}";
            }
            catch (Exception ex)
            {
                error = $"{ex.GetType().Name} 0x{ex.HResult:X8}: {ex.Message.Split('\n')[0].Trim()}";
            }

            Add("total", sw.Elapsed.TotalMilliseconds);
            cpuMs += (proc.TotalProcessorTime - cpu0).TotalMilliseconds;
            proc.Refresh();
            peakPrivate = Math.Max(peakPrivate, proc.PrivateMemorySize64);
        }

        // Same file through the app's decoder (what thumbnails and the viewer call).
        double? appMs = null;
        if (error is null)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                var r = await wic.DecodeAsync(file, FormatFamily.Heif, target, CancellationToken.None);
                appMs = sw.Elapsed.TotalMilliseconds;
                if (target is { } tg && Math.Max(r.Width, r.Height) > tg + 1) error = $"app decoder returned {r.Width}x{r.Height} for {tg}";
            }
            catch (Exception ex)
            {
                error = "app decoder: " + ex.GetType().Name + ": " + ex.Message.Split('\n')[0];
            }
        }

        var entry = new
        {
            file = name,
            target = target?.ToString() ?? "full",
            output = $"{outW}x{outH}",
            rasterMiB = Math.Round(bytes / 1048576.0, 1),
            runs,
            error,
            p50 = stages.ToDictionary(k => k.Key, k => Pct(k.Value, 0.5)),
            p95 = stages.ToDictionary(k => k.Key, k => Pct(k.Value, 0.95)),
            max = stages.ToDictionary(k => k.Key, k => Math.Round(k.Value.Max(), 1)),
            cpuMsPerRun = Math.Round(cpuMs / Math.Max(1, stages.GetValueOrDefault("total")?.Count ?? 1), 1),
            peakPrivateMiB = Math.Round(peakPrivate / 1048576.0),
            appDecoderMs = appMs is { } a ? Math.Round(a, 1) : (double?)null,
        };
        results.Add(entry);
        Console.WriteLine(JsonSerializer.Serialize(entry));
    }
}

// Cancellation: start a full decode of the largest file, cancel after 50 ms, and measure when the operation really
// ends and how much CPU it used after the cancel.
object? cancellation = null;
var largest = Directory.EnumerateFiles(dir, "*.heic").Select(Path.GetFullPath).Where(f => !f.Contains("truncated")).OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();
if (largest is not null && heifCodec is not null)
{
    var sf = await StorageFile.GetFileFromPathAsync(largest);
    using IRandomAccessStream stream = await sf.OpenAsync(FileAccessMode.Read);
    var decoder = await BitmapDecoder.CreateAsync(stream);
    var full = Stopwatch.StartNew();
    var op = decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.ColorManageToSRgb);
    using var cts = new CancellationTokenSource(50);
    var cpu0 = proc.TotalProcessorTime;
    string outcome;
    try
    {
        await op.AsTask(cts.Token);
        outcome = "completed (not cancelled)";
    }
    catch (OperationCanceledException)
    {
        outcome = "cancelled";
    }
    catch (Exception ex)
    {
        outcome = $"failed {ex.GetType().Name} 0x{ex.HResult:X8}";
    }

    var observed = full.Elapsed.TotalMilliseconds;
    await Task.Delay(3000); // does CPU keep being used after the cancel?
    var cpuAfter = (proc.TotalProcessorTime - cpu0).TotalMilliseconds;
    cancellation = new { file = Path.GetFileName(largest), outcome, awaitEndedMs = Math.Round(observed), status = op.Status.ToString(), cpuMsFromCancelTo3s = Math.Round(cpuAfter) };
    Console.WriteLine(JsonSerializer.Serialize(cancellation));
}

var report = new
{
    os = Environment.OSVersion.VersionString,
    cpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"),
    cores = Environment.ProcessorCount,
    heifCodec,
    codecs = codecs.Select(c => $"{c.FriendlyName} [{c.Extensions}]").ToList(),
    heifPackage = Environment.GetEnvironmentVariable("PW_HEIF_PACKAGE"),
    results,
    cancellation,
};
if (reportPath is not null) File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

static double Pct(List<double> v, double q)
{
    var s = v.Order().ToList();
    return Math.Round(s[Math.Min(s.Count - 1, (int)Math.Ceiling(q * s.Count) - 1)], 1);
}
