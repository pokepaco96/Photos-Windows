using System.Diagnostics;
using PhotosWindows.Application.Abstractions;

namespace PhotosWindows.Platform.Windows.Tests;

/// <summary>
/// The video poster frame reader on real Media Foundation, with clips made by ffmpeg for each case: padding cropped,
/// rotation, dark content kept, fade-ins, very short clips, HDR. Skipped (never passed) when ffmpeg is missing.
/// </summary>
public sealed class MediaFoundationFrameReaderTests : IClassFixture<MediaFoundationFrameReaderTests.Clips>
{
    private readonly Clips _clips;

    public MediaFoundationFrameReaderTests(Clips clips) => _clips = clips;

    /// <summary>Generates clips once per test run in a temporary folder, deleted afterwards (only the files it made).</summary>
    public sealed class Clips : IDisposable
    {
        private readonly List<string> _made = [];

        public Clips()
        {
            Folder = Path.Combine(Path.GetTempPath(), "pw-mf-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Folder);
            Ffmpeg = Environment.GetEnvironmentVariable("PW_FFMPEG") is { Length: > 0 } f ? f : "ffmpeg";
            Available = Run("-version") == 0;
        }

        public string Folder { get; }

        public string Ffmpeg { get; }

        public bool Available { get; }

        private const string Bt709 = "-color_primaries bt709 -color_trc bt709 -colorspace bt709 -color_range tv";
        private const string H264 = "-c:v libx264 -pix_fmt yuv420p -preset veryfast " + Bt709;

        /// <summary>Makes (once) and returns a clip; <paramref name="args"/> are ffmpeg arguments before the output.</summary>
        public string Get(string name, string args)
        {
            var path = Path.Combine(Folder, name);
            lock (_made)
            {
                if (File.Exists(path)) return path;
                Skip.IfNot(Available, "ffmpeg is not available (set PW_FFMPEG): clip tests not run");
                var code = Run($"-hide_banner -loglevel error -nostdin -y {args.Replace("{H264}", H264, StringComparison.Ordinal)} \"{path}\"");
                Assert.True(code == 0 && File.Exists(path), $"ffmpeg could not make {name}");
                _made.Add(path);
                return path;
            }
        }

        private int Run(string args)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(Ffmpeg, args) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false })!;
                p.StandardError.ReadToEnd();
                p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                return p.ExitCode;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return -1;
            }
        }

        public void Dispose()
        {
            foreach (var f in _made) File.Delete(f);
            if (Directory.Exists(Folder) && !Directory.EnumerateFileSystemEntries(Folder).Any()) Directory.Delete(Folder);
        }
    }

    // A frame split in quadrants: red top-left, green top-right, blue bottom-left, white bottom-right.
    private const string Quadrants = "-f lavfi -i \"color=c=white:s={0}:r=30:d=3,drawbox=x=0:y=0:w=iw/2:h=ih/2:c=red:t=fill,drawbox=x=iw/2:y=0:w=iw/2:h=ih/2:c=lime:t=fill,drawbox=x=0:y=ih/2:w=iw/2:h=ih/2:c=blue:t=fill\"";

    private static RasterImage Read(string path) => MediaFoundationFrameReader.Read(path);

    /// <summary>The frame as displayed: EXIF orientation 1/3/6/8 applied.</summary>
    private static (byte[] Px, int W, int H) Displayed(RasterImage f)
    {
        if (f.Orientation is not (3 or 6 or 8)) return (f.Pixels, f.Width, f.Height);
        var (w, h) = (f.Width, f.Height);
        var (ow, oh) = f.Orientation == 3 ? (w, h) : (h, w);
        var dst = new byte[f.Pixels.Length];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var (dx, dy) = f.Orientation switch { 6 => (h - 1 - y, x), 8 => (y, w - 1 - x), _ => (w - 1 - x, h - 1 - y) };
                Buffer.BlockCopy(f.Pixels, ((y * w) + x) * 4, dst, ((dy * ow) + dx) * 4, 4);
            }
        }

        return (dst, ow, oh);
    }

    private static string ColourAt((byte[] Px, int W, int H) img, double fx, double fy)
    {
        var o = (((int)(fy * img.H) * img.W) + (int)(fx * img.W)) * 4;
        int b = img.Px[o], g = img.Px[o + 1], r = img.Px[o + 2];
        return (r > 160, g > 160, b > 160) switch
        {
            (true, false, false) => "red",
            (false, true, false) => "green",
            (false, false, true) => "blue",
            (true, true, true) => "white",
            _ => $"rgb({r},{g},{b})",
        };
    }

    [SkippableFact]
    public void A_1080p_clip_is_cropped_to_its_display_size_not_the_1088_coded_lines()
    {
        var clip = _clips.Get("h264-1920x1080.mp4", string.Format(Quadrants, "1920x1080") + " {H264}");
        var frame = Read(clip);
        Assert.Equal((1920, 1080), (frame.Width, frame.Height));
        // The last line is picture (white), not decoder padding.
        Assert.Equal("white", ColourAt(Displayed(frame), 0.9, 1079.0 / 1080));
    }

    [SkippableFact]
    public void An_odd_size_clip_keeps_its_exact_size()
    {
        var frame = Read(_clips.Get("h264-1918x1078.mp4", string.Format(Quadrants, "1918x1078") + " {H264}"));
        Assert.Equal((1918, 1078), (frame.Width, frame.Height));
    }

    [SkippableTheory]
    [InlineData(0, "red", "green", "blue", "white")]
    [InlineData(90, "blue", "red", "white", "green")]
    [InlineData(180, "white", "blue", "green", "red")]
    [InlineData(270, "green", "white", "red", "blue")]
    public void A_rotated_clip_displays_upright(int clockwise, string topLeft, string topRight, string bottomLeft, string bottomRight)
    {
        // ffmpeg's -display_rotation is counterclockwise; the clip is shown turned `clockwise` degrees clockwise.
        var clip = _clips.Get($"rot{clockwise}.mp4", $"-display_rotation {-clockwise} " + string.Format(Quadrants, "640x360") + " {H264}");
        var shown = Displayed(Read(clip));
        Assert.Equal(clockwise % 180 == 0 ? (640, 360) : (360, 640), (shown.W, shown.H));
        Assert.Equal(
            (topLeft, topRight, bottomLeft, bottomRight),
            (ColourAt(shown, 0.25, 0.25), ColourAt(shown, 0.75, 0.25), ColourAt(shown, 0.25, 0.75), ColourAt(shown, 0.75, 0.75)));
    }

    [SkippableFact]
    public void A_really_black_clip_gives_its_black_frame_not_an_error()
    {
        var frame = Read(_clips.Get("black.mp4", "-f lavfi -i color=c=black:s=640x360:r=30:d=4 {H264}"));
        Assert.True(MediaFoundationFrameReader.MeanLuma(frame) < 1);
    }

    [SkippableFact]
    public void A_dark_night_scene_is_kept_as_it_is()
    {
        var frame = Read(_clips.Get("night.mp4", "-f lavfi -i testsrc2=s=640x360:r=30:d=6 -vf eq=brightness=-0.42:contrast=0.35,noise=alls=6:allf=t {H264}"));
        var luma = MediaFoundationFrameReader.MeanLuma(frame);
        Assert.InRange(luma, 20, 90);
        Assert.False(WindowsVideoFrameExtractor.IsBlankFrame(frame));
    }

    [SkippableFact]
    public void A_fade_in_with_one_key_frame_gives_the_faded_frame_not_the_black_key_frame()
    {
        // One GOP for the whole clip: seeking lands on the black first frame; the reader decodes forward to the poster time.
        var frame = Read(_clips.Get("fade-in.mp4", "-f lavfi -i testsrc2=s=640x360:r=30:d=8 -vf fade=t=in:st=0:d=3 -g 300 -sc_threshold 0 {H264}"));
        Assert.True(MediaFoundationFrameReader.MeanLuma(frame) > MediaFoundationFrameReader.NearlyBlackLuma, $"luma {MediaFoundationFrameReader.MeanLuma(frame):0.0}");
    }

    [SkippableFact]
    public void A_clip_black_for_its_first_seconds_gives_a_frame_with_content()
    {
        var frame = Read(_clips.Get("black-first-2s.mp4", "-f lavfi -i testsrc2=s=640x360:r=30:d=8 -vf drawbox=c=black:t=fill:enable='lt(t,2)' -g 300 -sc_threshold 0 {H264}"));
        Assert.True(MediaFoundationFrameReader.MeanLuma(frame) > 60, $"luma {MediaFoundationFrameReader.MeanLuma(frame):0.0}");
    }

    [SkippableTheory]
    [InlineData("half-second.mp4", "-f lavfi -i testsrc2=s=640x360:r=30:d=0.5 {H264}")]
    [InlineData("one-frame.mp4", "-f lavfi -i testsrc2=s=640x360:r=30:d=1 -frames:v 1 {H264}")]
    public void Very_short_clips_still_give_a_frame(string name, string args)
    {
        var frame = Read(_clips.Get(name, args));
        Assert.Equal((640, 360), (frame.Width, frame.Height));
        Assert.True(MediaFoundationFrameReader.MeanLuma(frame) > 60);
    }

    [SkippableTheory]
    [InlineData("hlg.mp4", "arib-std-b67")]
    [InlineData("pq.mp4", "smpte2084")]
    public void An_hdr_hevc_clip_gives_a_visible_frame_of_the_right_size(string name, string transfer)
    {
        var npl = transfer == "smpte2084" ? ":npl=100" : string.Empty;
        var clip = _clips.Get(name,
            $"-f lavfi -i testsrc2=s=1280x720:r=30:d=3 -vf zscale=tin=bt709:t={transfer}:pin=bt709:p=bt2020:min=bt709:m=bt2020nc:rin=tv:r=tv{npl} " +
            $"-c:v libx265 -preset fast -pix_fmt yuv420p10le -tag:v hvc1 -color_primaries bt2020 -color_trc {transfer} -colorspace bt2020nc " +
            $"-x265-params colorprim=bt2020:transfer={transfer}:colormatrix=bt2020nc:log-level=error");
        RasterImage frame;
        try
        {
            frame = Read(clip);
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            Skip.If(true, $"no HEVC decoder on this machine (HEVC Video Extensions): {ex.Message}");
            throw;
        }

        Assert.Equal((1280, 720), (frame.Width, frame.Height));
        // Not tone mapped (see docs): dimmer than SDR, but never black.
        Assert.InRange(MediaFoundationFrameReader.MeanLuma(frame), 60, 200);
        Assert.False(WindowsVideoFrameExtractor.IsBlankFrame(frame));
    }

    /// <summary>Handles gained over <paramref name="calls"/> reads, after warming up (the first reads load Media Foundation: ~250 handles once).</summary>
    private static int HandleGrowth(Action read, int calls)
    {
        for (var i = 0; i < 5; i++) read();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var before = Process.GetCurrentProcess().HandleCount;
        for (var i = 0; i < calls; i++) read();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        Thread.Sleep(500);
        return Process.GetCurrentProcess().HandleCount - before;
    }

    [SkippableFact]
    public void Reading_h264_frames_again_and_again_keeps_no_handle()
    {
        var clip = _clips.Get("h264-small.mp4", "-f lavfi -i testsrc2=s=640x360:r=30:d=3 {H264}");
        var growth = HandleGrowth(() => Read(clip), 100);
        Assert.True(growth < 25, $"+{growth} handles after 100 reads");
    }

    [SkippableFact]
    public void Reading_hevc_frames_keeps_at_most_the_one_semaphore_windows_leaks_per_decoder()
    {
        var clip = _clips.Get("hevc-small.mp4", "-f lavfi -i testsrc2=s=640x360:r=30:d=3 -c:v libx265 -preset fast -pix_fmt yuv420p -tag:v hvc1 -x265-params log-level=error");
        try
        {
            Read(clip);
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            Skip.If(true, $"no HEVC decoder on this machine: {ex.Message}");
        }

        // Windows' HEVC decoder (HEVC Video Extensions) leaves one semaphore per instance: measured exactly +1 per read.
        var growth = HandleGrowth(() => Read(clip), 100);
        Assert.True(growth < 125, $"+{growth} handles after 100 reads (expected about +100 from the codec, nothing more)");
    }

    [SkippableFact]
    public void A_file_that_is_not_a_video_throws_and_keeps_no_more_than_media_foundation_does()
    {
        Skip.IfNot(_clips.Available, "ffmpeg is not available");
        var path = Path.Combine(_clips.Folder, "not-a-video.mp4");
        File.WriteAllText(path, "this is not a video");
        try
        {
            // Media Foundation itself keeps 2 ALPC ports and 2 events per file it cannot parse (measured: exactly +4 per
            // call, the same when the file is opened as a byte stream first). Anything above that would be ours.
            var growth = HandleGrowth(() => Assert.ThrowsAny<Exception>(() => Read(path)), 100);
            Assert.True(growth < 425, $"+{growth} handles after 100 failed reads");
        }
        finally
        {
            File.Delete(path);
        }
    }
}

/// <summary>Pure helpers of the reader and the extractor (no Media Foundation needed).</summary>
public sealed class FrameHelpersTests
{
    [Theory]
    [InlineData(0u, 1)]
    [InlineData(90u, 6)]
    [InlineData(180u, 3)]
    [InlineData(270u, 8)]
    [InlineData(45u, 1)]
    [InlineData(360u, 1)]
    public void Video_rotation_maps_to_exif_orientation(uint rotation, int orientation) =>
        Assert.Equal(orientation, MediaFoundationFrameReader.OrientationFor(rotation));

    [Fact]
    public void Poster_time_is_a_tenth_of_the_video_at_most_one_second()
    {
        Assert.Equal(TimeSpan.Zero, MediaFoundationFrameReader.PosterTime(TimeSpan.Zero));
        Assert.Equal(TimeSpan.FromMilliseconds(500), MediaFoundationFrameReader.PosterTime(TimeSpan.FromSeconds(5)));
        Assert.Equal(TimeSpan.FromSeconds(1), MediaFoundationFrameReader.PosterTime(TimeSpan.FromMinutes(3)));
    }

    [Fact]
    public void The_display_aperture_is_used_only_when_it_lies_inside_the_frame()
    {
        Assert.Equal((0, 0, 1920, 1080), MediaFoundationFrameReader.Crop((0, 0, 1920, 1080), 1920, 1088));
        Assert.Equal((8, 4, 100, 50), MediaFoundationFrameReader.Crop((8, 4, 100, 50), 1920, 1088));
        Assert.Equal((0, 0, 1920, 1088), MediaFoundationFrameReader.Crop(null, 1920, 1088));
        Assert.Equal((0, 0, 1920, 1088), MediaFoundationFrameReader.Crop((0, 0, 1921, 1080), 1920, 1088));
        Assert.Equal((0, 0, 1920, 1088), MediaFoundationFrameReader.Crop((-2, 0, 100, 100), 1920, 1088));
        Assert.Equal((0, 0, 1920, 1088), MediaFoundationFrameReader.Crop((0, 0, 0, 100), 1920, 1088));
    }

    private static RasterImage Filled(byte b, byte g, byte r, int w = 64, int h = 36)
    {
        var px = new byte[w * h * 4];
        for (var i = 0; i < px.Length; i += 4) (px[i], px[i + 1], px[i + 2], px[i + 3]) = (b, g, r, 255);
        return new RasterImage(w, h, px);
    }

    [Fact]
    public void Mean_luma_of_black_and_white()
    {
        Assert.Equal(0, MediaFoundationFrameReader.MeanLuma(Filled(0, 0, 0)), 3);
        Assert.Equal(255, MediaFoundationFrameReader.MeanLuma(Filled(255, 255, 255)), 3);
    }

    [Fact]
    public void Only_a_frame_with_no_signal_at_all_counts_as_blank()
    {
        Assert.True(WindowsVideoFrameExtractor.IsBlankFrame(Filled(0, 0, 0)));
        Assert.True(WindowsVideoFrameExtractor.IsBlankFrame(Filled(2, 1, 2)));

        // A single lit pixel anywhere (not on the old every-7th-pixel grid) is enough to keep the frame.
        var dark = Filled(0, 0, 0);
        dark.Pixels[(((17 * 64) + 33) * 4) + 1] = 9;
        Assert.False(WindowsVideoFrameExtractor.IsBlankFrame(dark));

        // A very dark scene (values up to 8, which the old rule called black) is kept.
        Assert.False(WindowsVideoFrameExtractor.IsBlankFrame(Filled(6, 8, 7)));
    }
}
