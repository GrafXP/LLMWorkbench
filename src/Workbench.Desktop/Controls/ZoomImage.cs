using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Workbench.Desktop.Controls;

/// <summary>Draws the original bitmap with cursor-centred zoom and captured mouse panning.</summary>
public sealed class ZoomImage : FrameworkElement
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(nameof(Source), typeof(BitmapSource), typeof(ZoomImage),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((ZoomImage)d).Fit()));
    public BitmapSource? Source { get => (BitmapSource?)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public double Zoom { get; private set; } = 1;
    public Vector Offset { get; private set; }
    private bool _fit = true, _panning;
    private Point _last;
    public event Action? ViewChanged;
    public ZoomImage() { Focusable = true; ClipToBounds = true; Cursor = Cursors.Hand; }
    public void Fit()
    {
        _fit = true;
        if (Source is { } source && ActualWidth > 0 && ActualHeight > 0)
        {
            Zoom = Math.Clamp(Math.Min(ActualWidth / source.PixelWidth, ActualHeight / source.PixelHeight), .01, 32);
            Offset = new((ActualWidth - source.PixelWidth * Zoom) / 2, (ActualHeight - source.PixelHeight * Zoom) / 2);
        }
        InvalidateVisual(); ViewChanged?.Invoke();
    }
    public void ActualSize() => ZoomAt(1, new(ActualWidth / 2, ActualHeight / 2));
    public void ZoomBy(double factor) => ZoomAt(Zoom * factor, new(ActualWidth / 2, ActualHeight / 2));
    public void ZoomAt(double zoom, Point anchor)
    {
        if (Source is null || !double.IsFinite(zoom) || !double.IsFinite(anchor.X) || !double.IsFinite(anchor.Y)) return;
        zoom = Math.Clamp(zoom, .01, 32);
        Offset = new(anchor.X - (anchor.X - Offset.X) * zoom / Zoom, anchor.Y - (anchor.Y - Offset.Y) * zoom / Zoom);
        Zoom = zoom; _fit = false; InvalidateVisual(); ViewChanged?.Invoke();
    }
    public void PanBy(Vector delta)
    {
        if (Source is null || !double.IsFinite(delta.X) || !double.IsFinite(delta.Y)) return;
        Offset += delta; _fit = false; InvalidateVisual(); ViewChanged?.Invoke();
    }
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (_fit) Fit();
        else PanBy(new((sizeInfo.NewSize.Width - sizeInfo.PreviousSize.Width) / 2, (sizeInfo.NewSize.Height - sizeInfo.PreviousSize.Height) / 2));
    }
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(17, 23, 32)), null, new(0, 0, ActualWidth, ActualHeight));
        if (Source is { } source) dc.DrawImage(source, new(Offset.X, Offset.Y, source.PixelWidth * Zoom, source.PixelHeight * Zoom));
    }
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e); ZoomAt(Zoom * Math.Pow(1.2, e.Delta / 120d), e.GetPosition(this)); e.Handled = true;
    }
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e); Focus();
        if (e.ChangedButton is not (MouseButton.Left or MouseButton.Middle) || Source is null) return;
        if (e.ClickCount == 2) { Fit(); e.Handled = true; return; }
        _panning = true; _last = e.GetPosition(this); CaptureMouse(); Cursor = Cursors.SizeAll; e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_panning) return;
        var point = e.GetPosition(this); PanBy(point - _last); _last = point; e.Handled = true;
    }
    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_panning || e.ChangedButton is not (MouseButton.Left or MouseButton.Middle)) return;
        ReleaseMouseCapture(); e.Handled = true;
    }
    protected override void OnLostMouseCapture(MouseEventArgs e) { base.OnLostMouseCapture(e); _panning = false; Cursor = Cursors.Hand; }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.Home: case Key.F: Fit(); break;
            case Key.D1: case Key.NumPad1: ActualSize(); break;
            case Key.Add: case Key.OemPlus: ZoomBy(1.2); break;
            case Key.Subtract: case Key.OemMinus: ZoomBy(1 / 1.2); break;
            case Key.Left: PanBy(new(40, 0)); break;
            case Key.Right: PanBy(new(-40, 0)); break;
            case Key.Up: PanBy(new(0, 40)); break;
            case Key.Down: PanBy(new(0, -40)); break;
            default: return;
        }
        e.Handled = true;
    }
}
