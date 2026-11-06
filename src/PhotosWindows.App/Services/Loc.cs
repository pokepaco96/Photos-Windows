using System.Globalization;
using Microsoft.Windows.ApplicationModel.Resources;

namespace PhotosWindows.App.Services;

/// <summary>Localised strings from Strings/&lt;lang&gt;/Resources.resw (en-US, es-ES). Missing keys fall back to the key itself.</summary>
public static class Loc
{
    private static readonly Lazy<ResourceLoader?> Loader = new(() =>
    {
        try
        {
            return new ResourceLoader();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or FileNotFoundException)
        {
            return null;
        }
    });

    public static string Get(string key)
    {
        try
        {
            var value = Loader.Value?.GetString(key);
            return string.IsNullOrEmpty(value) ? key : value;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return key;
        }
    }

    public static string Format(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), args);
}
