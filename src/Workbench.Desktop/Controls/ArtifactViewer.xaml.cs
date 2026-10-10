using System.Windows;
using System.Windows.Controls;

namespace Workbench.Desktop.Controls;

public partial class ArtifactViewer : UserControl
{
    public ArtifactViewer()
    {
        InitializeComponent();
        ImageView.ViewChanged += () => ZoomLabel.Text = $"{ImageView.Zoom * 100:0.#}%";
    }
    private void FitImage(object sender, RoutedEventArgs e) => ImageView.Fit();
    private void ActualImage(object sender, RoutedEventArgs e) => ImageView.ActualSize();
    private void ZoomIn(object sender, RoutedEventArgs e) => ImageView.ZoomBy(1.2);
    private void ZoomOut(object sender, RoutedEventArgs e) => ImageView.ZoomBy(1 / 1.2);
}
