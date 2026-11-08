using System.Runtime.InteropServices;

namespace PhotosWindows.App;

internal static partial class NativeMethods
{
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
