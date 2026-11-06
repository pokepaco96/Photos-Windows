using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PhotosWindows.App.Services;

/// <summary>Content dialogs. WinUI allows one open dialog at a time, so calls are serialised.</summary>
public sealed class DialogService(WindowContext window, ILogger<DialogService> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        await _gate.WaitAsync();
        try
        {
            dialog.XamlRoot = window.XamlRoot;
            dialog.RequestedTheme = (window.Window.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default;
            dialog.Style ??= Microsoft.UI.Xaml.Application.Current.Resources["DefaultContentDialogStyle"] as Style;
            return await dialog.ShowAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<ContentDialogResult> ShowMessageAsync(string title, string message) =>
        ShowAsync(new ContentDialog { Title = title, Content = Wrap(message), CloseButtonText = Loc.Get("Common_Ok"), DefaultButton = ContentDialogButton.Close });

    public async Task<bool> ConfirmAsync(string title, string message, string primaryText, bool destructive = false)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = Wrap(message),
            PrimaryButtonText = primaryText,
            CloseButtonText = Loc.Get("Common_Cancel"),
            DefaultButton = destructive ? ContentDialogButton.Close : ContentDialogButton.Primary,
        };
        return await ShowAsync(dialog) == ContentDialogResult.Primary;
    }

    public async Task<string?> PromptAsync(string title, string placeholder, string? initial = null, string? primary = null)
    {
        var box = new TextBox { PlaceholderText = placeholder, Text = initial ?? string.Empty, MaxLength = 200, AcceptsReturn = false };
        box.SelectAll();
        var dialog = new ContentDialog
        {
            Title = title,
            Content = box,
            PrimaryButtonText = primary ?? Loc.Get("Common_Ok"),
            CloseButtonText = Loc.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        box.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(box.Text);
        dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(box.Text);
        return await ShowAsync(dialog) == ContentDialogResult.Primary ? box.Text.Trim() : null;
    }

    /// <summary>Shows a user-oriented error. Technical details stay in the local log.</summary>
    public Task ShowErrorAsync(string title, Exception exception)
    {
        logger.LogWarning(exception, "Error shown to the user: {Title}", title);
        return ShowMessageAsync(title, exception switch
        {
            UnauthorizedAccessException => Loc.Get("Error_AccessDenied"),
            DirectoryNotFoundException or FileNotFoundException => Loc.Get("Error_NotFound"),
            IOException io => Loc.Format("Error_Io", io.Message),
            _ => exception.Message,
        });
    }

    private static TextBlock Wrap(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
}
