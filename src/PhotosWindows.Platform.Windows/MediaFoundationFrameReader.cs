using System.Runtime.InteropServices;
using PhotosWindows.Application.Abstractions;

namespace PhotosWindows.Platform.Windows;

/// <summary>
/// Reads one video frame with the Media Foundation Source Reader: software decoding (no Direct3D device, no hardware
/// decoders), the reader's own colour conversion to RGB32, and every COM object released as soon as the frame is copied.
/// <para>
/// Replaces <c>MediaComposition.GetThumbnailAsync</c> for videos the shell cannot thumbnail. Measured on Windows 11
/// (HEVC Video Extensions 2.4.13, 40 iPhone videos, 2026-11-21): MediaComposition returned an all-black frame for every
/// HDR HEVC iPhone video it was given (11 of 40 tiles black in the app), took ~920 ms and 1.3 GB for some H.264
/// 1920×1440 clips, and kept up to 1.2 GB after it returned. This reader gave the right picture for all 40, in
/// 90–370 ms, ~150 MB for those H.264 clips and ~300 MB for 1080p HEVC (the HEVC decoder itself), released at once.
/// </para>
/// </summary>
internal static class MediaFoundationFrameReader
{
    private const uint FirstVideoStream = 0xFFFFFFFC;
    private const uint AllStreams = 0xFFFFFFFE;
    private const uint MediaSource = 0xFFFFFFFF;
    private static readonly Guid DurationKey = new("6c990d33-bb8e-477a-8598-0d5d96fcd88a");
    private const uint EndOfStream = 0x2;
    private const uint ReaderError = 0x1;
    private static readonly Guid EnableVideoProcessing = new("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");
    private static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid DefaultStride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    private static readonly Guid VideoRotation = new("c380465d-2271-428c-9b83-ecea3b4a85c1");
    private static readonly Guid MinimumDisplayAperture = new("d7388766-18fe-48c6-a177-ee894867c8c4");
    private static readonly Guid GeometricAperture = new("66758743-7e5f-400d-980a-aa8596c85696");
    private static readonly Guid MediaTypeVideo = new("73646976-0000-0010-8000-00AA00389B71");
    private static readonly Guid VideoFormatRgb32 = new("00000016-0000-0010-8000-00AA00389B71");
    private static readonly Lazy<int> Startup = new(() => MFStartup(0x00020070, 0), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// A poster frame as BGRA, with the rotation stored in the video carried as an EXIF orientation. Seeking lands on the
    /// key frame before the poster time (a tenth of the video, at most 1 s in), which is used as is unless it is nearly
    /// black; then the reader decodes forward to the poster time, and if that is still nearly black, to the middle of the
    /// video, keeping the brightest. Each step decodes at most <see cref="MaxForwardFrames"/> frames, so the cost stays
    /// bounded, and a really dark video still gets its own dark frame. Throws <see cref="COMException"/> when Media Foundation cannot read the file and
    /// <see cref="InvalidDataException"/> when it has no decodable frame.
    /// </summary>
    public static RasterImage Read(string path)
    {
        Marshal.ThrowExceptionForHR(Startup.Value);
        IMFAttributes? attributes = null, type = null, native = null;
        IMFSourceReader? reader = null;
        try
        {
            Marshal.ThrowExceptionForHR(MFCreateAttributes(out attributes, 1));
            attributes.SetUINT32(EnableVideoProcessing, 1);
            // Known Windows behaviour (measured on Windows 11, 2026-11-21): every file Media Foundation cannot parse leaves
            // 2 ALPC ports and 2 events open for good (+4 handles per failed call), also when the file is opened as a byte
            // stream first; and every HEVC decoder instance leaves one semaphore. Neither is ours to release.
            Marshal.ThrowExceptionForHR(MFCreateSourceReaderFromURL(path, attributes, out reader));
            reader.SetStreamSelection(AllStreams, false);
            reader.SetStreamSelection(FirstVideoStream, true);

            reader.GetNativeMediaType(FirstVideoStream, 0, out native);
            var rotation = TryGetUInt32(native, VideoRotation) ?? 0;
            var nativeAperture = TryGetAperture(native);

            Marshal.ThrowExceptionForHR(MFCreateMediaType(out type));
            type.SetGUID(MajorType, MediaTypeVideo);
            type.SetGUID(Subtype, VideoFormatRgb32);
            SetCurrentMediaType(reader, type);

            var duration = Duration(reader);
            var poster = PosterTime(duration);
            var frame = ReadFrameAt(reader, poster, exact: false, nativeAperture)
                ?? (duration > TimeSpan.Zero ? ReadFrameAt(reader, TimeSpan.Zero, exact: false, nativeAperture) : null) // seek past the end
                ?? throw new InvalidDataException("The video has no decodable frame.");
            var luma = MeanLuma(frame);
            TimeSpan[] retries = duration > TimeSpan.FromSeconds(2) ? [poster, TimeSpan.FromTicks(duration.Ticks / 2)] : [poster];
            foreach (var at in retries)
            {
                if (luma >= NearlyBlackLuma || at <= TimeSpan.Zero) break;
                if (ReadFrameAt(reader, at, exact: true, nativeAperture) is { } later && MeanLuma(later) is var l && l > luma) (frame, luma) = (later, l);
            }

            return new RasterImage(frame.Width, frame.Height, frame.Pixels, OrientationFor(rotation));
        }
        finally
        {
            Release(type);
            Release(native);
            Release(reader);
            Release(attributes);
        }
    }

    /// <summary>Mean luma (0-255) under which a poster frame counts as nearly black.</summary>
    internal const double NearlyBlackLuma = 12;

    /// <summary>Approximate mean luma (Rec. 709 weights) of a BGRA frame from about 4,096 evenly spread pixels.</summary>
    internal static double MeanLuma(RasterImage image)
    {
        var p = image.Pixels;
        var count = image.Width * image.Height;
        if (count == 0) return 0;
        var step = Math.Max(1, count / 4096);
        double sum = 0;
        var n = 0;
        for (var i = 0; i < count; i += step)
        {
            var o = i * 4;
            sum += (0.0722 * p[o]) + (0.7152 * p[o + 1]) + (0.2126 * p[o + 2]);
            n++;
        }

        return sum / n;
    }

    /// <summary>
    /// Seeks and copies a decoded frame, cropped to the display aperture: the first one after the seek (the key frame at or
    /// before <paramref name="at"/>), or with <paramref name="exact"/> the first at or after <paramref name="at"/>, decoding
    /// at most <see cref="MaxForwardFrames"/> frames forward. Null at the end of the stream. The output size and stride are
    /// read again for the frame, as they may change mid-stream (MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED).
    /// </summary>
    private static RasterImage? ReadFrameAt(IMFSourceReader reader, TimeSpan at, bool exact, (int X, int Y, int Width, int Height)? nativeAperture)
    {
        var position = new PropVariantInt64 { VarType = 20 /* VT_I8 */, Value = at.Ticks };
        try
        {
            reader.SetCurrentPosition(Guid.Empty, ref position);
        }
        catch (COMException)
        {
            // Not seekable: the next frame will do.
        }

        IMFSample? sample = null;
        try
        {
            var forward = 0;
            for (var i = 0; i < MaxReads; i++)
            {
                reader.ReadSample(FirstVideoStream, 0, out _, out var flags, out var timestamp, out sample);
                if ((flags & ReaderError) != 0) throw new InvalidDataException("The video stream reported an error.");
                if (sample is null)
                {
                    if ((flags & EndOfStream) != 0) break;
                    continue; // a stream tick (a gap): read on
                }

                // Frames before the requested time: decoded on the way from the key frame, skipped (bounded).
                if (exact && timestamp + HalfFrame < at.Ticks && ++forward < MaxForwardFrames && (flags & EndOfStream) == 0)
                {
                    Release(sample);
                    sample = null;
                    continue;
                }

                break;
            }

            return sample is null ? null : Copy(reader, sample, nativeAperture);
        }
        finally
        {
            Release(sample);
        }
    }

    /// <summary>Most ReadSample calls for one frame (gaps and stream ticks give no sample).</summary>
    private const int MaxReads = 300;

    /// <summary>Most frames decoded forward from a key frame to reach a time (4 s at 30 fps).</summary>
    internal const int MaxForwardFrames = 120;

    /// <summary>Tolerance when comparing a frame's time with the requested one (half a 60 fps frame, in 100 ns units).</summary>
    private const long HalfFrame = 83_333;

    private static RasterImage Copy(IMFSourceReader reader, IMFSample sample, (int X, int Y, int Width, int Height)? nativeAperture)
    {
        IMFAttributes? current = null;
        IMFMediaBuffer? buffer = null;
        try
        {
            reader.GetCurrentMediaType(FirstVideoStream, out current);
            current.GetUINT64(FrameSize, out var packed);
            int width = (int)(packed >> 32), height = (int)(packed & 0xFFFFFFFF);
            if (width <= 0 || height <= 0 || (long)width * height > DecodeLimits.MaxPixels) throw new InvalidDataException("Unsupported video frame size.");
            var stride = TryGetUInt32(current, DefaultStride) is { } rawStride ? unchecked((int)rawStride) : width * 4;
            if (Math.Abs((long)stride) < width * 4L) throw new InvalidDataException("Unexpected frame stride.");
            // The decoder states the display area on its output type (H.264 pads 1080 lines to 1088); some containers (MOV
            // 'clap') state it on the native type.
            var (x0, y0, outWidth, outHeight) = Crop(TryGetAperture(current) ?? TryGetGeometricAperture(current) ?? nativeAperture, width, height);

            sample.ConvertToContiguousBuffer(out buffer);
            buffer.Lock(out var data, out _, out var length);
            try
            {
                // Every byte read must lie inside the buffer. A negative stride means a bottom-up image: the first
                // displayed row is the last row in memory.
                var rowBytes = Math.Abs((long)stride);
                var lastMemoryRow = stride > 0 ? y0 + outHeight - 1 : height - 1 - y0;
                if ((lastMemoryRow * rowBytes) + ((x0 + outWidth) * 4L) > length) throw new InvalidDataException("Unexpected frame layout.");
                var pixels = new byte[outWidth * outHeight * 4];
                for (var y = 0; y < outHeight; y++)
                {
                    var memoryRow = stride > 0 ? y0 + y : height - 1 - (y0 + y);
                    Marshal.Copy(data + (nint)((memoryRow * rowBytes) + (x0 * 4L)), pixels, y * outWidth * 4, outWidth * 4);
                }

                for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255; // RGB32 leaves the fourth byte undefined
                return new RasterImage(outWidth, outHeight, pixels);
            }
            finally
            {
                buffer.Unlock();
            }
        }
        finally
        {
            Release(buffer);
            Release(current);
        }
    }

    /// <summary>
    /// The displayed area of a decoded frame: the minimum display aperture when it lies inside the frame (decoders pad
    /// 1080 lines to 1088 and odd sizes to whole blocks), else the whole frame.
    /// </summary>
    internal static (int X, int Y, int Width, int Height) Crop((int X, int Y, int Width, int Height)? aperture, int width, int height) =>
        aperture is { } a && a.X >= 0 && a.Y >= 0 && a.Width > 0 && a.Height > 0 && a.X + a.Width <= width && a.Y + a.Height <= height
            ? a
            : (0, 0, width, height);

    /// <summary>
    /// Passes the media type as IMFMediaType* (its own interface pointer, from QueryInterface) instead of relying on its
    /// IMFAttributes pointer being the same one.
    /// </summary>
    private static void SetCurrentMediaType(IMFSourceReader reader, IMFAttributes type)
    {
        var unknown = Marshal.GetIUnknownForObject(type);
        try
        {
            var iid = MediaTypeInterface;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in iid, out var mediaType));
            try
            {
                reader.SetCurrentMediaType(FirstVideoStream, IntPtr.Zero, mediaType);
            }
            finally
            {
                Marshal.Release(mediaType);
            }
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    private static readonly Guid MediaTypeInterface = new("44ae0fa8-ea31-4109-8d2e-4cae4997c555");

    /// <summary>MF_PD_DURATION of the file; zero when unknown.</summary>
    private static TimeSpan Duration(IMFSourceReader reader)
    {
        try
        {
            reader.GetPresentationAttribute(MediaSource, DurationKey, out var value);
            return value.VarType == 21 /* VT_UI8 */ && value.Value > 0 ? TimeSpan.FromTicks(value.Value) : TimeSpan.Zero;
        }
        catch (COMException)
        {
            return TimeSpan.Zero;
        }
    }

    /// <summary>A tenth of the duration, at most 1 s; zero when the duration is unknown.</summary>
    internal static TimeSpan PosterTime(TimeSpan duration) =>
        duration > TimeSpan.Zero ? TimeSpan.FromTicks(Math.Min(duration.Ticks / 10, TimeSpan.FromSeconds(1).Ticks)) : TimeSpan.Zero;

    /// <summary>MF_MT_VIDEO_ROTATION is clockwise; EXIF 6/3/8 rotate 90/180/270 clockwise to display.</summary>
    internal static int OrientationFor(uint rotation) => rotation switch
    {
        90 => 6,
        180 => 3,
        270 => 8,
        _ => 1,
    };

    private static uint? TryGetUInt32(IMFAttributes attributes, Guid key) => attributes.GetUINT32(key, out var value) == 0 ? value : null;

    private static (int X, int Y, int Width, int Height)? TryGetAperture(IMFAttributes attributes) => TryGetArea(attributes, MinimumDisplayAperture);

    private static (int X, int Y, int Width, int Height)? TryGetGeometricAperture(IMFAttributes attributes) => TryGetArea(attributes, GeometricAperture);

    private static (int X, int Y, int Width, int Height)? TryGetArea(IMFAttributes attributes, Guid key)
    {
        // MFVideoArea: two MFOffset (16.16 fixed point: WORD fraction, then SHORT value) then a SIZE.
        var area = new int[4];
        if (attributes.GetBlob(key, area, 16, out var size) != 0 || size != 16) return null;
        return (area[0] >> 16, area[1] >> 16, area[2], area[3]);
    }

    /// <summary>Releases a runtime callable wrapper this class obtained from one out parameter (each is released once).</summary>
    private static void Release(object? com)
    {
        if (com is not null && Marshal.IsComObject(com)) Marshal.FinalReleaseComObject(com);
    }

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFStartup(uint version, uint flags);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateAttributes(out IMFAttributes attributes, uint initialSize);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateMediaType(out IMFAttributes mediaType);

    [DllImport("mfreadwrite.dll", ExactSpelling = true)]
    private static extern int MFCreateSourceReaderFromURL([MarshalAs(UnmanagedType.LPWStr)] string url, IMFAttributes attributes, out IMFSourceReader reader);

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariantInt64
    {
        public ushort VarType;
        public ushort Reserved1, Reserved2, Reserved3;
        public long Value;
        public long Padding;
    }

    // Only the methods used are typed; the others keep their vtable slots.
    [ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFAttributes
    {
        void GetItem(); void GetItemType(); void CompareItem(); void Compare();
        [PreserveSig] int GetUINT32(in Guid key, out uint value);
        void GetUINT64(in Guid key, out ulong value);
        void GetDouble(); void GetGUID(); void GetStringLength(); void GetString(); void GetAllocatedString(); void GetBlobSize();
        [PreserveSig] int GetBlob(in Guid key, [Out, MarshalAs(UnmanagedType.LPArray)] int[] buffer, uint bufferSize, out uint blobSize);
        void GetAllocatedBlob(); void GetUnknown(); void SetItem(); void DeleteItem(); void DeleteAllItems();
        void SetUINT32(in Guid key, uint value);
        void SetUINT64(in Guid key, ulong value);
        void SetDouble();
        void SetGUID(in Guid key, in Guid value);
        void SetString(); void SetBlob(); void SetUnknown(); void LockStore(); void UnlockStore(); void GetCount(); void GetItemByIndex(); void CopyAllItems();
    }

    [ComImport, Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSourceReader
    {
        void GetStreamSelection(uint stream, [MarshalAs(UnmanagedType.Bool)] out bool selected);
        void SetStreamSelection(uint stream, [MarshalAs(UnmanagedType.Bool)] bool selected);
        void GetNativeMediaType(uint stream, uint index, out IMFAttributes mediaType);
        void GetCurrentMediaType(uint stream, out IMFAttributes mediaType);
        void SetCurrentMediaType(uint stream, IntPtr reserved, IntPtr mediaType);
        void SetCurrentPosition(in Guid timeFormat, ref PropVariantInt64 position);
        void ReadSample(uint stream, uint control, out uint actualStream, out uint flags, out long timestamp, out IMFSample? sample);
        void Flush(uint stream);
        void GetServiceForStream();
        void GetPresentationAttribute(uint stream, in Guid key, out PropVariantInt64 value);
    }

    [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSample
    {
        // IMFAttributes (30 slots)
        void A0(); void A1(); void A2(); void A3(); void A4(); void A5(); void A6(); void A7(); void A8(); void A9();
        void A10(); void A11(); void A12(); void A13(); void A14(); void A15(); void A16(); void A17(); void A18(); void A19();
        void A20(); void A21(); void A22(); void A23(); void A24(); void A25(); void A26(); void A27(); void A28(); void A29();
        void GetSampleFlags(); void SetSampleFlags(); void GetSampleTime(); void SetSampleTime(); void GetSampleDuration(); void SetSampleDuration();
        void GetBufferCount(); void GetBufferByIndex();
        void ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
    }

    [ComImport, Guid("045FA593-8799-42b8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFMediaBuffer
    {
        void Lock(out IntPtr buffer, out uint maxLength, out uint currentLength);
        void Unlock();
    }
}
