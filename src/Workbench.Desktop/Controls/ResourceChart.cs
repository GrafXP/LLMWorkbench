using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Workbench.Core;

namespace Workbench.Desktop.Controls;

public sealed class ResourceChart : FrameworkElement
{
    public static readonly DependencyProperty ModelProperty = DependencyProperty.Register(nameof(Model), typeof(ResourceViewModel), typeof(ResourceChart), new PropertyMetadata(null, ModelChanged));
    public ResourceViewModel? Model { get => (ResourceViewModel?)GetValue(ModelProperty); set => SetValue(ModelProperty, value); }
    public static readonly DependencyProperty MetricProperty = DependencyProperty.Register(nameof(Metric), typeof(string), typeof(ResourceChart), new FrameworkPropertyMetadata("CPU", FrameworkPropertyMetadataOptions.AffectsRender));
    public string Metric { get => (string)GetValue(MetricProperty); set => SetValue(MetricProperty, value); }
    private static void ModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var chart = (ResourceChart)d;
        if (e.OldValue is ResourceViewModel old) old.PropertyChanged -= chart.Changed;
        if (e.NewValue is ResourceViewModel next) next.PropertyChanged += chart.Changed;
        chart.InvalidateVisual();
    }
    private void Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(ResourceViewModel.Samples) or nameof(ResourceViewModel.Markers)) InvalidateVisual();
    }
    private double? Value(ResourceSnapshot sample) => Metric switch
    {
        "CPU" => sample.CpuPercent,
        "RAM" => sample.Memory is { Total: > 0 } m ? 100d * m.Used / m.Total : null,
        "GPU" => sample.Gpu?.Utilization,
        "VRAM" => sample.Gpu is { VramUsed: { } used, VramTotal: > 0 } gpu ? 100d * used / gpu.VramTotal!.Value : null,
        _ => null
    };
    protected override void OnRender(DrawingContext dc)
    {
        var background = new SolidColorBrush(Color.FromRgb(20, 26, 35));
        var rect = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawRectangle(background, new(new SolidColorBrush(Color.FromRgb(52, 65, 82)), 1), rect);
        if (ActualWidth < 4 || ActualHeight < 4 || Model?.Samples is not { Length: > 0 } samples) return;
        var color = Metric switch { "RAM" => Color.FromRgb(104, 213, 166), "GPU" => Color.FromRgb(179, 141, 249), "VRAM" => Color.FromRgb(243, 201, 121), _ => Color.FromRgb(105, 185, 255) };
        var brush = new SolidColorBrush(color);
        var end = samples[^1].Timestamp; var duration = TimeSpan.FromMinutes(Model.Minutes);
        double X(DateTimeOffset stamp) => ActualWidth * (1 - (end - stamp).TotalSeconds / duration.TotalSeconds);
        dc.PushClip(new RectangleGeometry(rect));
        var gridPen = new Pen(new SolidColorBrush(Color.FromRgb(45, 56, 73)), 1);
        foreach (var percent in new[] { 25, 50, 75 }) dc.DrawLine(gridPen, new(0, ActualHeight * (1 - percent / 100d)), new(ActualWidth, ActualHeight * (1 - percent / 100d)));
        if (Model.HighlightStart is { } start)
        {
            var left = Math.Clamp(X(start), 0, ActualWidth); var right = Math.Clamp(X(Model.HighlightEnd ?? end), 0, ActualWidth);
            if (right > left) dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(35, 255, 255, 255)), null, new(left, 0, right - left, ActualHeight));
        }
        Point? previous = null;
        foreach (var sample in samples)
        {
            if (Value(sample) is not { } value) { previous = null; continue; }
            var point = new Point(X(sample.Timestamp), 2 + (ActualHeight - 4) * (1 - Math.Clamp(value, 0, 100) / 100));
            if (previous is { } p) dc.DrawLine(new(brush, 1.7), p, point);
            else dc.DrawEllipse(brush, null, point, 2, 2);
            previous = point;
        }
        foreach (var marker in Model.Markers) dc.DrawLine(new(new SolidColorBrush(Color.FromArgb(65, 255, 255, 255)), 1), new(X(marker.Timestamp), 0), new(X(marker.Timestamp), ActualHeight));
        dc.Pop();
        var label = new FormattedText($"{Model.Minutes} min · 0–100%", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, Brushes.Gray, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(label, new(6, ActualHeight - 17));
        ToolTip = $"{Metric} utilisation · White markers show job events · Highlight follows the selected queue item";
    }
}
