using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using PhotosWindows.App.ViewModels;

namespace PhotosWindows.App.Views;

public sealed partial class EditorPage : Page
{
    public EditorPage()
    {
        ViewModel = App.Services.GetRequiredService<EditorViewModel>();
        InitializeComponent();
        Crop.Attach(ViewModel);
        ViewModel.CropChanged += (_, _) => UpdateCropLayout();
        // Releasing the mouse ends a slider gesture: the next drag becomes a new undo step.
        Adjustments.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => ViewModel.EndGesture()), handledEventsToo: true);
    }

    public EditorViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is long id) await ViewModel.LoadAsync(id);
    }

    private void OnPreviewSizeChanged(object sender, SizeChangedEventArgs e) => UpdateCropLayout();

    private void UpdateCropLayout()
    {
        if (ViewModel.PreviewPixelWidth <= 0 || PreviewHost.ActualWidth <= 0) return;
        var scale = Math.Min(PreviewHost.ActualWidth / ViewModel.PreviewPixelWidth, PreviewHost.ActualHeight / ViewModel.PreviewPixelHeight);
        var w = ViewModel.PreviewPixelWidth * scale;
        var h = ViewModel.PreviewPixelHeight * scale;
        Crop.SetImageBounds(new Windows.Foundation.Rect((PreviewHost.ActualWidth - w) / 2, (PreviewHost.ActualHeight - h) / 2, w, h));
    }

    private void OnPresetClick(object sender, RoutedEventArgs e) => ViewModel.ApplyPresetCommand.Execute((sender as FrameworkElement)?.Tag as PresetItem);

    private void OnCompareChecked(object sender, RoutedEventArgs e) => ViewModel.SetComparing(true);

    private void OnCompareUnchecked(object sender, RoutedEventArgs e) => ViewModel.SetComparing(false);

    private void OnUndo(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ViewModel.UndoCommand.Execute(null);
        args.Handled = true;
    }

    private void OnRedo(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ViewModel.RedoCommand.Execute(null);
        args.Handled = true;
    }

    private void OnSave(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ViewModel.SaveCommand.Execute(null);
        args.Handled = true;
    }
}
