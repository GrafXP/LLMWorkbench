using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Workbench.Desktop;

public partial class MainWindow : Window
{
    private bool _canClose;
    private bool _closing;
    public MainWindow(WorkbenchViewModel model)
    {
        InitializeComponent(); DataContext = model;
        model.ViewerRequested += () => { ViewerPanel.IsSelected = true; ViewerPanel.IsActive = true; };
        Closing += OnClosing;
        KeyDown += (_, e) =>
        {
            if (Keyboard.FocusedElement is TextBox) return;
            if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control) { model.UndoCommand.Execute(null); e.Handled = true; }
            if (e.Key == Key.Y && Keyboard.Modifiers == ModifierKeys.Control) { model.RedoCommand.Execute(null); e.Handled = true; }
        };
    }
    private void FitGraph(object sender, RoutedEventArgs e) => GraphCanvas.Fit();
    private void ViewResult(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ArtifactChoice result } && DataContext is WorkbenchViewModel model)
            model.SelectedResult = result;
        ViewerPanel.IsSelected = true; ViewerPanel.IsActive = true;
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_canClose) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true; IsEnabled = false;
        try { await ((WorkbenchViewModel)DataContext).StopAsync(); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Closing the workbench", MessageBoxButton.OK, MessageBoxImage.Error); }
        _canClose = true;
        _ = Dispatcher.BeginInvoke(new Action(Close));
    }
}
