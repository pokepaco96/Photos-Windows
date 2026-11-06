using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace PhotosWindows.App.Services;

/// <summary>Creates bitmaps for the UI. File reads happen off the UI thread; BitmapImage creation happens on it.</summary>
public sealed class ImageLoader(UiThread ui)
{
    public async Task<BitmapImage?> FromFileAsync(string path, int decodePixelWidth, CancellationToken ct)
    {
        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (ct.IsCancellationRequested) return null;
        BitmapImage? result = null;
        await ui.RunAsync(async () => result = await FromBytesAsync(bytes, decodePixelWidth));
        return result;
    }

    /// <summary>Must be called on the UI thread.</summary>
    public static async Task<BitmapImage> FromBytesAsync(byte[] bytes, int decodePixelWidth)
    {
        var bitmap = new BitmapImage();
        if (decodePixelWidth > 0) bitmap.DecodePixelWidth = decodePixelWidth;
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(bytes.AsBuffer());
        stream.Seek(0);
        await bitmap.SetSourceAsync(stream);
        return bitmap;
    }
}
