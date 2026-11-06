using Microsoft.UI.Xaml;

namespace PhotosWindows.App.Converters;

/// <summary>Helpers for x:Bind function bindings (no converter classes needed).</summary>
public static class Bind
{
    public static bool Not(bool value) => !value;

    public static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility Collapsed(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility VisibleIfText(string? value) => string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility VisibleIfAny(int count) => count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public static bool And(bool a, bool b) => a && b;

    public static Visibility VisibleAnd(bool a, bool b) => a && b ? Visibility.Visible : Visibility.Collapsed;
}
