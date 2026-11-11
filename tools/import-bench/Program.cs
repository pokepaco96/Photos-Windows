using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PhotosWindows.Application.Abstractions;
using PhotosWindows.Application.Import;
using PhotosWindows.Application.Library;
using PhotosWindows.Application.Thumbnails;
using PhotosWindows.Domain;
using PhotosWindows.Imaging;
using PhotosWindows.Infrastructure;
using PhotosWindows.Infrastructure.Settings;
using SkiaSharp;

// Import benchmark. Generates a synthetic, licence-free dataset (no personal photos), imports it with the real
// ImportService and writes a JSON + Markdown report with phase timings, throughput, CPU and memory.
//
//   dotnet run -c Release --project tools/import-bench -- generate --out D:\pwbench\ds --photos 10000 --photo-kb 3000 --videos 100 --video-mb 200 --dup-pct 25 --sidecar-pct 5 --live-pct 5
//   dotnet run -c Release --project tools/import-bench -- run --data D:\pwbench\ds --work D:\pwbench\work --workers 4 --mode copy [--background] [--verify]
var a = Args.Parse(args);
switch (a.Command)
{
    case "generate": Generate(a); break;
    case "run": await RunAsync(a); break;
    case "viewer": await ViewerAsync(a); break;
    default:
        Console.Error.WriteLine("usage: generate --out DIR [--photos N --photo-kb K --videos N --video-mb M --dup-pct P --sidecar-pct P --live-pct P --depth D --per-folder N]\n       run --data DIR --work DIR [--workers N] [--mode copy|inplace] [--background] [--verify] [--report FILE]");
        Environment.Exit(2);
        break;
}

static void Generate(Args a)
{
    var outDir = a.Req("out");
    var photos = a.Int("photos", 1000);
    var photoKb = a.Int("photo-kb", 3000);
    var videos = a.Int("videos", 0);
    var videoMb = a.Int("video-mb", 50);
    var dupPct = a.Int("dup-pct", 0);
    var sidecarPct = a.Int("sidecar-pct", 0);
    var livePct = a.Int("live-pct", 0);
    var perFolder = a.Int("per-folder", 400);
    Directory.CreateDirectory(outDir);

    // A few real JPEG bases at camera-like size; each file gets a unique trailer after EOI so its SHA-256 differs.
    var side = (int)Math.Sqrt(photoKb * 1024 / 0.25 / 1.33); // ~0.25 bytes/pixel at q90 for the pattern
    var bases = Enumerable.Range(0, 8).Select(i => MakeJpeg(side * 4 / 3, side, i)).ToArray();
    var rnd = new Random(42);
    var bytes = 0L;
    var files = 0;
    var uniquePhotos = photos * (100 - dupPct) / 100;
    string FolderFor(int i) => Path.Combine(outDir, $"{2015 + i / 4000 % 10}", $"Álbum {i / perFolder:000}");
    var written = new List<string>();
    for (var i = 0; i < photos; i++)
    {
        var folder = FolderFor(i);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"IMG_{i:000000}.jpg");
        if (i >= uniquePhotos)
        {
            // Exact duplicate of an earlier photo under another name/folder.
            File.Copy(written[rnd.Next(written.Count)], path);
        }
        else
        {
            var b = bases[i % bases.Length];
            var trailer = new byte[16];
            BinaryPrimitives.WriteInt64LittleEndian(trailer, i);
            BinaryPrimitives.WriteInt64LittleEndian(trailer.AsSpan(8), 0x5057_4245_4e43_48);
            using var f = File.Create(path);
            f.Write(b);
            f.Write(trailer);
            written.Add(path);
        }

        bytes += new FileInfo(path).Length;
        files++;
        if (sidecarPct > 0 && i % (100 / sidecarPct) == 0)
        {
            File.WriteAllText(Path.ChangeExtension(path, ".xmp"), $"<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description rdf:about=\"\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" xmp:Rating=\"3\"/></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>");
            files++;
        }

        if (livePct > 0 && i < uniquePhotos && i % (100 / livePct) == 1)
        {
            bytes += WriteQuickTime(Path.ChangeExtension(path, ".mov"), TimeSpan.FromSeconds(3), 3 * 1024 * 1024, i);
            files++;
        }
    }

    for (var v = 0; v < videos; v++)
    {
        var folder = Path.Combine(outDir, "Vídeos", $"{v / 50:00}");
        Directory.CreateDirectory(folder);
        bytes += WriteQuickTime(Path.Combine(folder, $"VID_{v:0000}.mov"), TimeSpan.FromMinutes(2), (long)videoMb * 1024 * 1024, 1_000_000 + v);
        files++;
    }

    var manifest = new { photos, photoKb, videos, videoMb, dupPct, sidecarPct, livePct, files, bytes, note = "Synthetic: generated JPEG patterns and QuickTime containers with random payload. No personal data." };
    File.WriteAllText(Path.Combine(outDir, "dataset.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"Generated {files:N0} files, {bytes / 1048576.0:N0} MiB in {outDir}");
}

static byte[] MakeJpeg(int w, int h, int seed)
{
    using var bmp = new SKBitmap(w, h);
    var r = new Random(seed);
    using (var c = new SKCanvas(bmp))
    {
        c.Clear(new SKColor((byte)r.Next(256), (byte)r.Next(256), (byte)r.Next(256)));
        using var p = new SKPaint { IsAntialias = true };
        for (var i = 0; i < 400; i++)
        {
            p.Color = new SKColor((byte)r.Next(256), (byte)r.Next(256), (byte)r.Next(256));
            c.DrawCircle(r.Next(w), r.Next(h), r.Next(4, w / 6), p);
        }
    }

    // Noise so the JPEG has camera-like entropy and size.
    var px = bmp.GetPixelSpan();
    var noise = new byte[px.Length];
    r.NextBytes(noise);
    var span = bmp.GetPixelSpan().ToArray();
    for (var i = 0; i < span.Length; i++) if ((i & 3) != 3) span[i] = (byte)(span[i] ^ (noise[i] & 0x1F));
    System.Runtime.InteropServices.Marshal.Copy(span, 0, bmp.GetPixels(), span.Length);
    using var img = SKImage.FromBitmap(bmp);
    using var data = img.Encode(SKEncodedImageFormat.Jpeg, 90);
    return data.ToArray();
}

static long WriteQuickTime(string path, TimeSpan duration, long payload, int seed)
{
    static byte[] Atom(string type, byte[] body)
    {
        var b = new byte[8 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(b, (uint)b.Length);
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(b, 4);
        body.CopyTo(b, 8);
        return b;
    }

    var ftyp = Atom("ftyp", [.. "qt  "u8.ToArray(), 0, 0, 0, 0, .. "qt  "u8.ToArray()]);
    var mvhd = new byte[100];
    var created = (uint)(new DateTime(2022, 5, 1, 12, 0, 0, DateTimeKind.Utc).AddMinutes(seed) - new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
    BinaryPrimitives.WriteUInt32BigEndian(mvhd.AsSpan(4), created);
    BinaryPrimitives.WriteUInt32BigEndian(mvhd.AsSpan(8), created);
    BinaryPrimitives.WriteUInt32BigEndian(mvhd.AsSpan(12), 600);
    BinaryPrimitives.WriteUInt32BigEndian(mvhd.AsSpan(16), (uint)(duration.TotalSeconds * 600));
    BinaryPrimitives.WriteUInt32BigEndian(mvhd.AsSpan(20), 0x00010000);
    BinaryPrimitives.WriteUInt16BigEndian(mvhd.AsSpan(24), 0x0100);
    var moov = Atom("moov", Atom("mvhd", mvhd));
    using var f = File.Create(path);
    f.Write(ftyp);
    f.Write(moov);
    var header = new byte[8];
    BinaryPrimitives.WriteUInt32BigEndian(header, payload + 8 > uint.MaxValue ? 1u : (uint)(payload + 8));
    "mdat"u8.CopyTo(header.AsSpan(4));
    f.Write(header);
    var chunk = new byte[1 << 20];
    new Random(seed).NextBytes(chunk);
    for (long left = payload; left > 0; left -= chunk.Length)
    {
        BinaryPrimitives.WriteInt64LittleEndian(chunk, left ^ seed);
        f.Write(chunk, 0, (int)Math.Min(chunk.Length, left));
    }

    return f.Length;
}

static async Task RunAsync(Args a)
{
    var data = a.Req("data");
    var work = a.Req("work");
    var workers = a.Int("workers", Math.Clamp(Environment.ProcessorCount / 2, 1, 4));
    var mode = a.Str("mode", "copy") == "inplace" ? ImportMode.AddInPlace : ImportMode.CopyToLibrary;
    var background = a.Flag("background");
    if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
    Directory.CreateDirectory(work);

    var services = new ServiceCollection();
    services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
    services.AddPhotosWindowsCore(new DefaultAppPaths(Path.Combine(work, "appdata")));
    services.AddPhotosWindowsImaging();
    await using var provider = services.BuildServiceProvider();
    var settings = provider.GetRequiredService<ISettingsStore>();
    settings.Save(settings.Load() with { MaxBackgroundWorkers = workers });
    var libs = provider.GetRequiredService<LibraryManager>();
    await libs.CreateAsync(work, "bench", CancellationToken.None);
    var import = provider.GetRequiredService<ImportService>();
    var thumbs = provider.GetRequiredService<ThumbnailService>();

    var process = Process.GetCurrentProcess();
    var cpu0 = process.TotalProcessorTime;
    var total = Stopwatch.StartNew();
    TimeSpan? firstReport = null, firstAsset = null, firstJobReport = null;
    var reports = 0;
    import.AssetImported += (_, _) => firstAsset ??= total.Elapsed;
    var progress = new InlineProgress(p =>
    {
        Interlocked.Increment(ref reports);
        firstReport ??= total.Elapsed;
        if (p.Total > 0) firstJobReport ??= total.Elapsed;
    });

    // Sample peak memory and CPU while running.
    var peakWs = 0L;
    var cpuSamples = new List<double>();
    using var sampler = new CancellationTokenSource();
    var sampling = Task.Run(async () =>
    {
        var last = process.TotalProcessorTime;
        var lastT = total.Elapsed;
        while (!sampler.IsCancellationRequested)
        {
            try { await Task.Delay(500, sampler.Token); } catch (OperationCanceledException) { break; }
            process.Refresh();
            peakWs = Math.Max(peakWs, process.WorkingSet64);
            var now = process.TotalProcessorTime;
            var t = total.Elapsed;
            cpuSamples.Add((now - last).TotalMilliseconds / (t - lastT).TotalMilliseconds / Environment.ProcessorCount * 100);
            (last, lastT) = (now, t);
        }
    });

    var bgThumbs = 0;
    using var bgStop = new CancellationTokenSource();
    var bg = background
        ? Task.Run(async () =>
        {
            // Mirrors BackgroundProcessing in the app: polls pending thumbnails/perceptual hashes while importing.
            while (!bgStop.IsCancellationRequested)
            {
                try
                {
                    if (import.IsRunning && !a.Flag("no-yield")) { await Task.Delay(200, bgStop.Token); continue; }
                    var n = await thumbs.ProcessPendingAsync(40, bgStop.Token);
                    bgThumbs += n;
                    if (n == 0) await Task.Delay(200, bgStop.Token);
                }
                catch (OperationCanceledException) { break; }
            }
        })
        : Task.CompletedTask;

    var result = await import.ImportAsync(new ImportRequest([data], mode), progress, CancellationToken.None);
    var importTime = total.Elapsed;
    var importCpu = process.TotalProcessorTime - cpu0;
    bgStop.Cancel();
    await bg;

    // Drain remaining thumbnails/perceptual hashes (the work the app does after an import).
    var drain = Stopwatch.StartNew();
    var drained = 0;
    if (a.Flag("drain"))
    {
        int n;
        while ((n = await thumbs.ProcessPendingAsync(200, CancellationToken.None)) > 0) drained += n;
    }

    drain.Stop();
    sampler.Cancel();
    await sampling;

    var verified = 0;
    var mismatches = 0;
    if (a.Flag("verify"))
    {
        // Every catalogued file must hash to its recorded SHA-256, and every source must be unchanged (checked by
        // re-hashing sources against the manifest written before the run, if present).
        var assets = provider.GetRequiredService<IAssetRepository>();
        var lib = provider.GetRequiredService<ILibraryContext>().RequireCurrent();
        foreach (var row in await assets.GetAllOriginalFilesAsync(CancellationToken.None))
        {
            var sha = await PhotosWindows.Application.Common.FileHashing.Sha256Async(lib.Resolve(row.File), CancellationToken.None);
            verified++;
            if (!string.Equals(sha, row.File.Sha256, StringComparison.OrdinalIgnoreCase)) mismatches++;
        }
    }

    var d = import.Diagnostics;
    var bytes = d.BytesHashed;
    var report = new
    {
        commit = Environment.GetEnvironmentVariable("PW_COMMIT") ?? "unknown",
        os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
        cpus = Environment.ProcessorCount,
        dataset = File.Exists(Path.Combine(data, "dataset.json")) ? JsonDocument.Parse(File.ReadAllText(Path.Combine(data, "dataset.json"))).RootElement : default,
        workers,
        mode = mode.ToString(),
        background,
        status = result.Status.ToString(),
        imported = result.Imported,
        duplicates = result.Duplicates,
        skipped = result.Skipped,
        failed = result.Failed,
        importSeconds = Math.Round(importTime.TotalSeconds, 2),
        firstProgressSeconds = firstReport is { } fr ? Math.Round(fr.TotalSeconds, 3) : (double?)null,
        firstCountedProgressSeconds = firstJobReport is { } fj ? Math.Round(fj.TotalSeconds, 3) : (double?)null,
        firstAssetSeconds = firstAsset is { } fa ? Math.Round(fa.TotalSeconds, 3) : (double?)null,
        progressReports = reports,
        mibPerSecond = Math.Round(bytes / 1048576.0 / importTime.TotalSeconds, 1),
        filesPerSecond = Math.Round((result.Imported + result.Duplicates + result.Skipped + result.Failed) / importTime.TotalSeconds, 1),
        cpuSecondsImport = Math.Round(importCpu.TotalSeconds, 1),
        cpuAvgPercent = cpuSamples.Count > 0 ? Math.Round(cpuSamples.Average(), 0) : 0,
        cpuPeakPercent = cpuSamples.Count > 0 ? Math.Round(cpuSamples.Max(), 0) : 0,
        peakWorkingSetMiB = Math.Round(Math.Max(peakWs, process.PeakWorkingSet64) / 1048576.0, 0),
        thumbnailsDuringImport = bgThumbs,
        drainedAfter = drained,
        drainSeconds = Math.Round(drain.Elapsed.TotalSeconds, 2),
        verifiedFiles = verified,
        hashMismatches = mismatches,
        phases = d.Snapshot().ToDictionary(p => p.Phase.ToString(), p => new { ms = Math.Round(p.Time.TotalMilliseconds), count = p.Count }),
    };

    var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
    Console.WriteLine(json);
    if (a.Str("report", "") is { Length: > 0 } file) File.WriteAllText(file, json);
    if (mismatches > 0) Environment.Exit(1);
}

// Opens photos the way the viewer does (cached small → large preview) while the background thumbnail pass runs over
// the whole library, and reports p50/p95/max time to the first preview.
static async Task ViewerAsync(Args a)
{
    var data = a.Req("data");
    var work = a.Req("work");
    var workers = a.Int("workers", 2);
    var opens = a.Int("opens", 100);
    var warm = a.Int("background-seconds", 10);
    if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
    Directory.CreateDirectory(work);
    var services = new ServiceCollection();
    services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
    services.AddPhotosWindowsCore(new DefaultAppPaths(Path.Combine(work, "appdata")));
    services.AddPhotosWindowsImaging();
    await using var provider = services.BuildServiceProvider();
    var settings = provider.GetRequiredService<ISettingsStore>();
    settings.Save(settings.Load() with { MaxBackgroundWorkers = workers });
    await provider.GetRequiredService<LibraryManager>().CreateAsync(work, "bench", CancellationToken.None);
    var import = provider.GetRequiredService<ImportService>();
    var thumbs = provider.GetRequiredService<ThumbnailService>();
    var assets = provider.GetRequiredService<IAssetRepository>();
    await import.ImportAsync(new ImportRequest([data], ImportMode.AddInPlace), null, CancellationToken.None);
    var ids = (await assets.QueryAsync(new AssetQuery(), CancellationToken.None)).Select(r => r.Id).ToList();
    Console.Error.WriteLine($"library: {ids.Count} assets");

    using var stop = new CancellationTokenSource();
    var bgDone = 0;
    var background = Task.Run(async () =>
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var n = await thumbs.ProcessPendingAsync(40, stop.Token);
                Interlocked.Add(ref bgDone, n);
                if (n == 0) break;
            }
            catch (OperationCanceledException) { break; }
        }
    });
    await Task.Delay(TimeSpan.FromSeconds(warm));

    var targets = new List<int> { 0, Math.Min(528, ids.Count - 1), ids.Count - 1 };
    var rnd = new Random(7);
    while (targets.Count < opens) targets.Add(rnd.Next(ids.Count));
    var times = new List<double>();
    var timeouts = 0;
    foreach (var index in targets)
    {
        var sw = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(a.Int("timeout", 60)));
        try
        {
            var path = await thumbs.GetOrCreateAsync(ids[index], ThumbnailSize.Large, cts.Token);
            if (path is null) timeouts++;
        }
        catch (OperationCanceledException) { timeouts++; }
        times.Add(sw.Elapsed.TotalMilliseconds);
        if (times.Count <= 3) Console.Error.WriteLine($"open #{index + 1}: {sw.Elapsed.TotalMilliseconds:0} ms");
    }

    stop.Cancel();
    await background;
    times.Sort();
    double P(double q) => times[(int)Math.Min(times.Count - 1, Math.Ceiling(q * times.Count) - 1)];
    var report = new
    {
        commit = Environment.GetEnvironmentVariable("PW_COMMIT") ?? "unknown",
        assets = ids.Count,
        workers,
        backgroundSecondsBeforeOpening = warm,
        backgroundItemsDone = bgDone,
        opens = times.Count,
        p50ms = Math.Round(P(0.5)),
        p95ms = Math.Round(P(0.95)),
        maxms = Math.Round(times[^1]),
        timedOut = timeouts,
    };
    var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
    Console.WriteLine(json);
    if (a.Str("report", "") is { Length: > 0 } file) File.WriteAllText(file, json);
}

sealed class InlineProgress(Action<ImportProgress> report) : IProgress<ImportProgress>
{
    public void Report(ImportProgress value) => report(value);
}

sealed record Args(string Command, Dictionary<string, string?> Options)
{
    public static Args Parse(string[] args)
    {
        var o = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) continue;
            var key = args[i][2..];
            o[key] = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : null;
        }

        return new Args(args.Length > 0 ? args[0] : "", o);
    }

    public string Req(string k) => Options.TryGetValue(k, out var v) && v is not null ? v : throw new ArgumentException("--" + k + " is required");
    public string Str(string k, string d) => Options.TryGetValue(k, out var v) && v is not null ? v : d;
    public int Int(string k, int d) => Options.TryGetValue(k, out var v) && v is not null ? int.Parse(v, CultureInfo.InvariantCulture) : d;
    public bool Flag(string k) => Options.ContainsKey(k);
}
