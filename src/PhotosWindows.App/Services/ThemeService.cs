using Microsoft.UI.Xaml;
using PhotosWindows.Application.Library;

namespace PhotosWindows.App.Services;

public sealed class ThemeService(ISettingsStore settings, WindowContext window)
{
    public AppTheme Current => settings.Load().Theme;

    public void Apply() => Apply(Current);

    public void Set(AppTheme theme)
    {
        settings.Save(settings.Load() with { Theme = theme });
        Apply(theme);
    }

    private void Apply(AppTheme theme)
    {
        if (window.Window.Content is FrameworkElement root)
        {
            root.RequestedTheme = theme switch
            {
                AppTheme.Light => ElementTheme.Light,
                AppTheme.Dark => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
        }
    }
}
