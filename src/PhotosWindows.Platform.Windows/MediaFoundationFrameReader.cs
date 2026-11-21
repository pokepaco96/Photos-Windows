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
    private static readonly Guid Duration = new("6c990d33-bb8e-477a-8598-0d5d96fcd88a");
    private const uint EndOfStream = 0x2;
    private const uint ReaderError = 0x1;
    private static readonly Guid EnableVideoProcessing = new("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");
    private static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid DefaultStride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    private static readonly Guid VideoRotation = new("c380465d-2271-428c-9b83-ecea3b4a85c1");
    private static readonly Guid MinimumDisplayAperture = new("d7388766-18fe-48c6-a177-ee894867c8c4");
    private static readonly Guid MediaTypeVideo = new("73646976-0000-0010-8000-00AA00389B71");
    private static readonly Guid VideoFormatRgb32 = new("00000016-0000-0010-8000-00AA00389B71");
    private static readonly Lazy<int> Startup = new(() => MFStartup(0x00020070, 0), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// The frame at a tenth of the video (at most 1 s in: first frames are often black) or the key frame before it, as BGRA with the
    /// rotation stored in the video carried as an EXIF orientation. Throws <see cref="COMException"/> when Media
    /// Foundation cannot read the file and <see cref="InvalidDataException"/> when it has no decodable frame.
    /// </summary>
    public static RasterImage Read(string path)
    {
        Marshal.ThrowExceptionForHR(Startup.Value);
        IMFAttributes? attributes = null, type = null, native = null, current = null;
        IMFSourceReader? reader = null;
        IMFSample? sample = null;
        IMFMediaBuffer? buffer = null;
        try
        {
            Marshal.ThrowExceptionForHR(MFCreateAttributes(out attributes, 1));
            attributes.SetUINT32(EnableVideoProcessing, 1);
            Marshal.ThrowExceptionForHR(MFCreateSourceReaderFromURL(path, attributes, out reader));
            reader.SetStreamSelection(AllStreams, false);
            reader.SetStreamSelection(FirstVideoStream, true);

            reader.GetNativeMediaType(FirstVideoStream, 0, out native);
            var rotation = TryGetUInt32(native, VideoRotation) ?? 0;
            var aperture = TryGetAperture(native);

            Marshal.ThrowExceptionForHR(MFCreateMediaType(out type));
            type.SetGUID(MajorType, MediaTypeVideo);
            type.SetGUID(Subtype, VideoFormatRgb32);
            reader.SetCurrentMediaType(FirstVideoStream, IntPtr.Zero, type);
            reader.GetCurrentMediaType(FirstVideoStream, out current);
            current.GetUINT64(FrameSize, out var packed);
            int width = (int)(packed >> 32), height = (int)(packed & 0xFFFFFFFF);
            if (width <= 0 || height <= 0 || (long)width * height > DecodeLimits.MaxPixels) throw new InvalidDataException("Unsupported video frame size.");
            var stride = TryGetUInt32(current, DefaultStride) is { } rawStride ? unchecked((int)rawStride) : width * 4;

            var at = PosterTime(reader);
            if (at > TimeSpan.Zero)
            {
                var position = new PropVariantInt64 { VarType = 20 /* VT_I8 */, Value = at.Ticks };
                try
                {
                    reader.SetCurrentPosition(Guid.Empty, ref position);
                }
                catch (COMException)
                {
                    // Not seekable: the first frame will do.
                }
            }

            for (var i = 0; i < 300 && sample is null; i++)
            {
                reader.ReadSample(FirstVideoStream, 0, out _, out var flags, out _, out sample);
                if ((flags & ReaderError) != 0) throw new InvalidDataException("The video stream reported an error.");
                if ((flags & EndOfStream) != 0) break;
            }

            if (sample is null) throw new InvalidDataException("The video has no decodable frame.");
            sample.ConvertToContiguousBuffer(out buffer);
            buffer.Lock(out var data, out _, out var length);
            try
            {
                // Crop the decoder's padding (1080 lines are coded as 1088) when the display area starts at the corner.
                var (outWidth, outHeight) = aperture is { X: 0, Y: 0 } a && a.Width <= width && a.Height <= height ? (a.Width, a.Height) : (width, height);
                if (Math.Abs(stride) < outWidth * 4 || (long)Math.Abs(stride) * (outHeight - 1) + outWidth * 4 > length) throw new InvalidDataException("Unexpected frame layout.");
                var pixels = new byte[outWidth * outHeight * 4];
                for (var y = 0; y < outHeight; y++)
                {
                    // A negative stride means a bottom-up image.
                    var row = stride >= 0 ? y * (long)stride : (height - 1 - y) * (long)-stride;
                    Marshal.Copy(data + (nint)row, pixels, y * outWidth * 4, outWidth * 4);
                }

                for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255; // RGB32 leaves the fourth byte undefined
                return new RasterImage(outWidth, outHeight, pixels, OrientationFor(rotation));
            }
            finally
            {
                buffer.Unlock();
            }
        }
        finally
        {
            Release(buffer);
            Release(sample);
            Release(current);
            Release(type);
            Release(native);
            Release(reader);
            Release(attributes);
        }
    }

    /// <summary>A tenth of the duration, at most 1 s; zero when the duration is unknown.</summary>
    private static TimeSpan PosterTime(IMFSourceReader reader)
    {
        try
        {
            reader.GetPresentationAttribute(MediaSource, Duration, out var value);
            return value.VarType == 21 /* VT_UI8 */ && value.Value > 0 ? TimeSpan.FromTicks(Math.Min(value.Value / 10, TimeSpan.FromSeconds(1).Ticks)) : TimeSpan.Zero;
        }
        catch (COMException)
        {
            return TimeSpan.Zero;
        }
    }

    /// <summary>MF_MT_VIDEO_ROTATION is clockwise; EXIF 6/3/8 rotate 90/180/270 clockwise to display.</summary>
    internal static int OrientationFor(uint rotation) => rotation switch
    {
        90 => 6,
        180 => 3,
        270 => 8,
        _ => 1,
    };

    private static uint? TryGetUInt32(IMFAttributes attributes, Guid key) => attributes.GetUINT32(key, out var value) == 0 ? value : null;

    private static (int X, int Y, int Width, int Height)? TryGetAperture(IMFAttributes attributes)
    {
        // MFVideoArea: two MFOffset (16.16 fixed point) then a SIZE.
        var area = new int[4];
        if (attributes.GetBlob(MinimumDisplayAperture, area, 16, out var size) != 0 || size != 16) return null;
        return (area[0] >> 16, area[1] >> 16, area[2], area[3]);
    }

    private static void Release(object? com)
    {
        if (com is not null) Marshal.ReleaseComObject(com);
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
        void SetCurrentMediaType(uint stream, IntPtr reserved, IMFAttributes mediaType);
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
