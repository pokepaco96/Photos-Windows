using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using PhotosWindows.App.ViewModels;
using PhotosWindows.Domain;
using Windows.Foundation;

namespace PhotosWindows.App.Views;

/// <summary>
/// Interactive crop rectangle drawn over the (uncropped) preview. Purely visual interaction: it converts drags to a
/// normalised rectangle and hands it to the view model, which owns the edit state.
/// </summary>
public sealed partial class CropOverlay : Canvas
{
    private const double HandleSize = 18;
    private readonly Rectangle[] _shade = [NewShade(), NewShade(), NewShade(), NewShade()];
    private readonly Rectangle _frame = new() { Stroke = new SolidColorBrush(Colors.White), StrokeThickness = 2 };
    private readonly Thumb _move = new() { Background = new SolidColorBrush(Colors.Transparent), Opacity = 0.01 };
    private readonly Thumb[] _corners = [NewHandle(), NewHandle(), NewHandle(), NewHandle()];
    private EditorViewModel? _vm;
    private Rect _image;
    private NormalizedRect _crop = NormalizedRect.Full;

    public CropOverlay()
    {
        foreach (var s in _shade) Children.Add(s);
        Children.Add(_frame);
        Children.Add(_move);
        foreach (var c in _corners) Children.Add(c);
        _move.DragDelta += (_, e) => Update(r => r with { X = r.X + e.HorizontalChange / _image.Width, Y = r.Y + e.VerticalChange / _image.Height }, keepSize: true);
        _move.DragCompleted += (_, _) => Commit(true);
        for (var i = 0; i < 4; i++)
        {
            var corner = i;
            _corners[i].DragDelta += (_, e) => Update(r => Resize(r, corner, e.HorizontalChange / _image.Width, e.VerticalChange / _image.Height), keepSize: false);
            _corners[i].DragCompleted += (_, _) => Commit(true);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_corners[i], "Crop corner");
        }
    }

    public void Attach(EditorViewModel vm) => _vm = vm;

    public void SetImageBounds(Rect image)
    {
        _image = image;
        _crop = _vm?.Current.Crop ?? NormalizedRect.Full;
        Layout();
    }

    private static Rectangle NewShade() => new() { Fill = new SolidColorBrush(Color(0x99, 0, 0, 0)), IsHitTestVisible = false };

    private static Windows.UI.Color Color(byte a, byte r, byte g, byte b) => Windows.UI.Color.FromArgb(a, r, g, b);

    private static Thumb NewHandle() => new() { Width = HandleSize, Height = HandleSize, Background = new SolidColorBrush(Colors.White), CornerRadius = new CornerRadius(HandleSize / 2) };

    private NormalizedRect Resize(NormalizedRect r, int corner, double dx, double dy)
    {
        double left = r.X, top = r.Y, right = r.X + r.Width, bottom = r.Y + r.Height;
        if (corner is 0 or 3) left += dx; else right += dx;
        if (corner is 0 or 1) top += dy; else bottom += dy;
        const double min = 0.05;
        left = Math.Clamp(left, 0, right - min);
        right = Math.Clamp(right, left + min, 1);
        top = Math.Clamp(top, 0, bottom - min);
        bottom = Math.Clamp(bottom, top + min, 1);
        var result = new NormalizedRect(left, top, right - left, bottom - top);

        // Keep the chosen aspect ratio while resizing.
        if (_vm?.SelectedAspect is { } aspect && _vm.OrientedAspect(_vm.Current, false) is { } imageAspect)
        {
            var ratio = aspect.Key == "original" ? _vm.OrientedAspect(_vm.Current, true) : aspect.Ratio;
            if (ratio is { } target)
            {
                var height = result.Width * imageAspect / target;
                var y = corner is 0 or 1 ? bottom - height : top;
                result = new NormalizedRect(result.X, Math.Max(0, y), result.Width, Math.Min(height, 1)).Clamp();
            }
        }

        return result;
    }

    private void Update(Func<NormalizedRect, NormalizedRect> change, bool keepSize)
    {
        if (_image.Width <= 0) return;
        var next = change(_crop);
        if (keepSize)
        {
            next = next with { X = Math.Clamp(next.X, 0, 1 - next.Width), Y = Math.Clamp(next.Y, 0, 1 - next.Height) };
        }

        _crop = next.Clamp();
        Layout();
        Commit(false);
    }

    private void Commit(bool end) => _vm?.SetCrop(_crop, end);

    private void Layout()
    {
        if (_image.Width <= 0) return;
        var x = _image.X + _crop.X * _image.Width;
        var y = _image.Y + _crop.Y * _image.Height;
        var w = _crop.Width * _image.Width;
        var h = _crop.Height * _image.Height;

        Place(_shade[0], _image.X, _image.Y, _image.Width, y - _image.Y);
        Place(_shade[1], _image.X, y + h, _image.Width, _image.Bottom - (y + h));
        Place(_shade[2], _image.X, y, x - _image.X, h);
        Place(_shade[3], x + w, y, _image.Right - (x + w), h);
        Place(_frame, x, y, w, h);
        Place(_move, x, y, w, h);
        var half = HandleSize / 2;
        Place(_corners[0], x - half, y - half, HandleSize, HandleSize);
        Place(_corners[1], x + w - half, y - half, HandleSize, HandleSize);
        Place(_corners[2], x + w - half, y + h - half, HandleSize, HandleSize);
        Place(_corners[3], x - half, y + h - half, HandleSize, HandleSize);
    }

    private static void Place(FrameworkElement e, double x, double y, double w, double h)
    {
        SetLeft(e, x);
        SetTop(e, y);
        e.Width = Math.Max(0, w);
        e.Height = Math.Max(0, h);
    }
}
