using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Workbench.Core;

namespace Workbench.Desktop;

public partial class App : Application
{
    private string? _smokeDirectory;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _smokeDirectory = Value(e.Args, "--smoke-test");
        DispatcherUnhandledException += (_, args) =>
        {
            var path = Path.Combine(Path.GetTempPath(), "LLMWorkbench-error.log");
            File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O}\n{args.Exception}\n");
            if (_smokeDirectory is not null)
            {
                Directory.CreateDirectory(_smokeDirectory);
                File.WriteAllText(Path.Combine(_smokeDirectory, "error.txt"), args.Exception.ToString());
                args.Handled = true; Shutdown(1); return;
            }
            MessageBox.Show($"{args.Exception.Message}\n\nDetails: {path}", "Workbench error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        try
        {
            var workspace = FindWorkspace();
            var root = workspace is not null ? Path.Combine(workspace, "outputs", "workbench-v2", "projects") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LLMWorkbench", "projects");
            var smoke = Value(e.Args, "--smoke-test");
            var project = Value(e.Args, "--project") ?? (smoke is not null ? Path.Combine(smoke, "project") : Path.Combine(root, "default"));
            var model = new WorkbenchViewModel(project, root, Dispatcher);
            var window = new MainWindow(model); MainWindow = window;
            if (smoke is not null)
            {
                window.ShowInTaskbar = false; window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -10000; window.Top = -10000;
                window.ContentRendered += async (_, _) => await SmokeAsync(window, model, smoke);
            }
            window.Show();
        }
        catch (Exception ex)
        {
            var smoke = Value(e.Args, "--smoke-test");
            if (smoke is not null) { Directory.CreateDirectory(smoke); File.WriteAllText(Path.Combine(smoke, "error.txt"), ex.ToString()); }
            else MessageBox.Show(ex.Message, "Could not start the workbench", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
    private static string? Value(string[] args, string flag)
    {
        var index = Array.IndexOf(args, flag); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
    private static string? FindWorkspace()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "models")) && Directory.Exists(Path.Combine(directory.FullName, "workbench"))) return directory.FullName;
            directory = directory.Parent;
        }
        return null;
    }
    private static bool _smokeStarted;
    private async Task SmokeAsync(MainWindow window, WorkbenchViewModel model, string directory)
    {
        if (_smokeStarted) return;
        _smokeStarted = true;
        try
        {
            Directory.CreateDirectory(directory);
            model.CreateExample(); window.GraphCanvas.Fit(); model.RunCommand.Execute(null);
            for (var i = 0; i < 50 && !model.Jobs.Any(j => j.Record.State == JobState.AwaitingSelection); i++) await Task.Delay(100);
            if (!model.Jobs.Any(j => j.Record.State == JobState.AwaitingSelection)) throw new InvalidOperationException("The example did not reach its selection checkpoint.");
            var selection = model.Jobs.Single(j => j.Record.State == JobState.AwaitingSelection);
            model.SelectedJob = selection;
            await Task.Delay(1600);
            window.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(Path.Combine(directory, "workbench.png"))) encoder.Save(file);
            File.WriteAllText(Path.Combine(directory, "state.json"), model.ExportState());
            if (model.Resources.Snapshot?.Memory is null || model.Resources.Snapshot.CpuPercent is null) throw new InvalidOperationException("RAM/CPU telemetry did not arrive.");
            model.Results[0].IsChosen = true; model.ChooseCommand.Execute(null);
            for (var i = 0; i < 40 && model.Jobs.Any(j => j.Record.State != JobState.Done); i++) await Task.Delay(100);
            if (model.Jobs.Any(j => j.Record.State != JobState.Done)) throw new InvalidOperationException("The example did not complete after selection.");
            File.WriteAllText(Path.Combine(directory, "result.json"), model.ExportState());
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(directory, "error.txt"), ex.ToString()); Environment.ExitCode = 1; }
        finally { window.Close(); }
    }
}
