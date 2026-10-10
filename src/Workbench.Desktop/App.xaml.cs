using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Workbench.Core;
using AvalonDock.Layout;

namespace Workbench.Desktop;

public partial class App : Application
{
    private string? _smokeDirectory;
    protected override void OnStartup(StartupEventArgs e)
    {
        Themes.DarkChrome.Initialize();
        base.OnStartup(e);
        _smokeDirectory = Value(e.Args, "--smoke-test") ?? Value(e.Args, "--phase2-smoke");
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
            var smoke = _smokeDirectory;
            var project = Value(e.Args, "--project") ?? (smoke is not null ? Path.Combine(smoke, "project") : Path.Combine(root, "default"));
            var model = new WorkbenchViewModel(project, root, Dispatcher);
            var window = new MainWindow(model); MainWindow = window;
            if (smoke is not null)
            {
                window.ShowInTaskbar = false; window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -10000; window.Top = -10000;
                window.ContentRendered += async (_, _) => { if (Value(e.Args, "--phase2-smoke") is not null) await PhaseTwoSmokeAsync(window, model, smoke); else await SmokeAsync(window, model, smoke); };
            }
            window.Show();
        }
        catch (Exception ex)
        {
            var smoke = _smokeDirectory;
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
    private async Task PhaseTwoSmokeAsync(MainWindow window, WorkbenchViewModel model, string directory)
    {
        if (_smokeStarted) return; _smokeStarted = true;
        try
        {
            Directory.CreateDirectory(directory);
            if (model.TextModels.Count(p => p.Profile.Available) < 2) throw new InvalidOperationException("Both installed Qwen choices are required.");
            var originalTextModel = model.TextModel;
            model.TextModel = model.TextModels.First(p => p.Profile.Id != originalTextModel!.Profile.Id && p.Profile.Available);
            if (model.TextModel?.Profile.Id == originalTextModel!.Profile.Id || model.TextModel?.ToString() != model.TextModel?.Label) throw new InvalidOperationException("Qwen model selection did not change or display its label.");
            model.TextModel = originalTextModel;
            var arrayResult = new ChatItem(new(Guid.NewGuid(), "tool", "[{\"Id\":\"qwen36\"}]", DateTimeOffset.UtcNow, Name: "list_models"));
            if (arrayResult.Content != "Read the model library." || string.IsNullOrEmpty(arrayResult.ToString())) throw new InvalidOperationException("Array tool result display failed.");
            if (model.Runs.Count < 2 || model.Conversation.Count == 0) throw new InvalidOperationException("The saved image-loop history and conversation did not reopen.");
            var selection = model.Jobs.Single(j => j.Record.State == JobState.AwaitingSelection); model.SelectedJob = selection;
            if (model.Results.Count != 6 || model.Results.Any(r => r.Preview is null)) throw new InvalidOperationException("The six image thumbnails did not load.");
            var graph = model.Graph;
            model.RoundCount = "3";
            if (!model.BatchPreview.Contains("3 texts", StringComparison.Ordinal)) throw new InvalidOperationException("Batch preview did not update.");
            // Round creation and undo are actual UI commands; verification leaves the saved graph intact.
            model.AddRoundCommand.Execute(null);
            if (model.Graph.Nodes.Length != graph.Nodes.Length + 6) throw new InvalidOperationException("The image-round control did not create typed nodes.");
            model.UndoCommand.Execute(null);
            if (model.Graph.Nodes.Length != graph.Nodes.Length) throw new InvalidOperationException("Image-round undo failed.");
            model.SelectNode(model.Graph.Nodes.Last(n => n.Kind == NodeKind.PrepareImage).Id);
            model.SelectedProfiles = "klein4b,zimage"; model.SelectedSeeds = "42,43";
            if (!model.NodeMatrixPreview.Contains("24 images", StringComparison.Ordinal)) throw new InvalidOperationException("Model/seed comparison preview failed.");
            model.SelectNode(selection.Record.NodeId); window.GraphCanvas.Fit(); await Task.Delay(1600); window.UpdateLayout();
            model.SelectedResult = model.Results[0];
            if (model.ArtifactPath.Count < 3 || !model.ArtifactOrigin.StartsWith("Source text:", StringComparison.Ordinal)) throw new InvalidOperationException("Image source-path inspection failed.");
            var fullImage = model.ViewerImage ?? throw new InvalidOperationException("The viewer did not load the original image.");
            var zoomImage = window.ResultViewer.ImageView;
            window.UpdateLayout();
            if (zoomImage.Source?.PixelWidth != fullImage.PixelWidth || fullImage.PixelWidth <= model.Results[0].Preview!.PixelWidth)
                throw new InvalidOperationException("The image viewer is using the thumbnail instead of the original bitmap.");
            zoomImage.Fit(); var fit = zoomImage.Zoom;
            var anchor = new Point(zoomImage.ActualWidth * .35, zoomImage.ActualHeight * .4);
            var beforePoint = new Point((anchor.X - zoomImage.Offset.X) / zoomImage.Zoom, (anchor.Y - zoomImage.Offset.Y) / zoomImage.Zoom);
            zoomImage.ZoomAt(fit * 2, anchor);
            var afterPoint = new Point((anchor.X - zoomImage.Offset.X) / zoomImage.Zoom, (anchor.Y - zoomImage.Offset.Y) / zoomImage.Zoom);
            if ((beforePoint - afterPoint).Length > .001 || Math.Abs(zoomImage.Zoom - fit * 2) > .001) throw new InvalidOperationException("Cursor-centred image zoom failed.");
            var offset = zoomImage.Offset; zoomImage.PanBy(new(27, -19));
            if ((zoomImage.Offset - offset - new Vector(27, -19)).Length > .001) throw new InvalidOperationException("Image pan failed.");
            zoomImage.ActualSize(); if (Math.Abs(zoomImage.Zoom - 1) > .001) throw new InvalidOperationException("Actual image size failed.");
            zoomImage.Fit();
            window.ViewerPanel.FloatingLeft = -10000; window.ViewerPanel.FloatingTop = -10000;
            window.ViewerPanel.Float(); await Task.Delay(100);
            if (!window.DockManager.Layout.FloatingWindows.Any()) throw new InvalidOperationException("The viewer did not float.");
            window.ViewerPanel.Dock();
            var results = window.DockManager.Layout.Descendents().OfType<LayoutAnchorable>().Single(p => p.ContentId == "Results");
            if (results.Parent is LayoutAnchorablePane pane)
            {
                pane.DockWidth = new GridLength(660);
                if (pane.Parent is LayoutPanel panel) panel.DockHeight = new GridLength(440);
            }
            window.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(Path.Combine(directory, "workbench-phase2.png"))) encoder.Save(file);
            File.WriteAllText(Path.Combine(directory, "result.json"), DocumentJson.Write(new { TextModels = model.TextModels.Select(p => p.Label).ToArray(), SelectedTextModel = model.TextModel?.Profile.Id, ArrayToolDisplay = arrayResult.Content, ImageThumbnails = model.Results.Count(r => r.Preview is not null), ViewerWidth = fullImage.PixelWidth, ViewerHeight = fullImage.PixelHeight, ImageZoomPan = true, ViewerFloatDock = true, SavedRuns = model.Runs.Count, ConversationMessages = model.Conversation.Count, Graph = model.Graph, Resources = model.Resources.Snapshot }));
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(directory, "error.txt"), ex.ToString()); Environment.ExitCode = 1; }
        finally { window.Close(); }
    }
    private async Task SmokeAsync(MainWindow window, WorkbenchViewModel model, string directory)
    {
        if (_smokeStarted) return;
        _smokeStarted = true;
        try
        {
            Directory.CreateDirectory(directory);
            model.CreateExample();
            var edge = model.Graph.Edges[0];
            model.Disconnect(edge.Id);
            if (model.Graph.Edges.Length != 2) throw new InvalidOperationException("Disconnect failed.");
            model.UndoCommand.Execute(null);
            if (model.Graph.Edges.Length != 3) throw new InvalidOperationException("Undo failed.");
            model.RedoCommand.Execute(null);
            model.Connect(edge.SourceNode, edge.SourcePort, edge.TargetNode, edge.TargetPort);
            var text = model.Graph.Nodes.First(n => n.Kind == NodeKind.Text);
            var choice = model.Graph.Nodes.Single(n => n.Kind == NodeKind.Select);
            model.Connect(text.Id, "text", choice.Id, "items");
            if (model.Graph.Edges.Length != 3) throw new InvalidOperationException("Invalid connection modified the graph.");
            model.SelectNode(text.Id);
            var draftWords = model.ViewerText + "\nA detail edited in the viewer.";
            model.ViewerText = draftWords;
            model.SelectNode(choice.Id); model.SelectNode(text.Id);
            if (model.ViewerText != draftWords || model.SelectedText != draftWords) throw new InvalidOperationException("Navigating nodes lost the text draft or inspector synchronisation.");
            window.GraphCanvas.Fit(); model.RunCommand.Execute(null);
            if (model.Graph.Nodes.Single(n => n.Id == text.Id).Settings["text"] != draftWords) throw new InvalidOperationException("Run graph did not save the edited viewer text.");
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
            var ram = window.DockManager.Layout.Descendents().OfType<LayoutAnchorable>().Single(p => p.ContentId == "RAM");
            ram.FloatingLeft = -10000; ram.FloatingTop = -10000;
            ram.Float(); await Task.Delay(100);
            if (!window.DockManager.Layout.FloatingWindows.Any()) throw new InvalidOperationException("Resource panel did not float.");
            ram.Dock();
            model.SelectedResult = model.Results[0]; model.ViewerText = "First manually edited candidate";
            model.NextResultCommand.Execute(null); model.ViewerText = "Second manually edited candidate";
            model.SaveViewerCommand.Execute(null);
            if (model.Graph.Nodes.Count(n => ManualOutputs.Read(n).Length > 0) != 2) throw new InvalidOperationException("Saving edits after navigating results lost a text revision.");
            model.UndoCommand.Execute(null);
            if (model.ViewerText == "Second manually edited candidate") throw new InvalidOperationException("Undo did not restore the original text in the viewer.");
            model.RedoCommand.Execute(null);
            if (model.ViewerText != "Second manually edited candidate") throw new InvalidOperationException("Redo did not restore the manual text revision.");
            model.Results[0].IsChosen = true; model.ChooseCommand.Execute(null);
            for (var i = 0; i < 40 && model.Jobs.Any(j => j.Record.State != JobState.Done); i++) await Task.Delay(100);
            if (model.Jobs.Any(j => j.Record.State != JobState.Done)) throw new InvalidOperationException("The example did not complete after selection.");
            var historicalRun = model.SelectedRun;
            model.SelectedResult = model.Results[0];
            const string manualWords = "Human-edited result\nuse these exact words downstream.";
            window.ResultViewer.TextEditor.Text = manualWords;
            window.ResultViewer.TextEditor.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)!.UpdateSource();
            if (model.ViewerText != manualWords || !model.SaveViewerCommand.CanExecute(null)) throw new InvalidOperationException("The viewer text editor did not update its draft.");
            model.SaveViewerCommand.Execute(null);
            if (model.ViewerText != manualWords || !model.ViewerStatus.StartsWith("Saved manual version", StringComparison.Ordinal)) throw new InvalidOperationException("Manual artifact save failed: " + model.Status);
            var originalWords = model.Results[0].Content;
            if (originalWords == manualWords) throw new InvalidOperationException("Editing an artifact overwrote a historical result.");
            model.RunViewerCommand.Execute(null);
            for (var i = 0; i < 50 && !model.Jobs.Any(j => j.Record.State == JobState.AwaitingSelection); i++) await Task.Delay(100);
            var editedSelection = model.Jobs.Single(j => j.Record.State == JobState.AwaitingSelection);
            model.SelectedJob = editedSelection;
            var edited = model.Results.Single(r => r.Content == manualWords);
            if (model.Graph.Nodes.Single(n => n.Id == text.Id).Settings["text"] != draftWords) throw new InvalidOperationException("Editing a result overwrote the original text draft.");
            model.SelectedRun = historicalRun; model.SelectNode(choice.Id);
            if (model.Results.Any(r => r.Content == manualWords)) throw new InvalidOperationException("Historical execution outputs changed after a manual edit.");
            model.SelectedRun = model.Runs[0]; model.SelectNode(editedSelection.Record.NodeId);
            model.Results.Single(r => r.Artifact.Id == edited.Artifact.Id).IsChosen = true; model.ChooseCommand.Execute(null);
            for (var i = 0; i < 40 && model.Jobs.Any(j => j.Record.State != JobState.Done); i++) await Task.Delay(100);
            if (model.Jobs.Any(j => j.Record.State != JobState.Done)) throw new InvalidOperationException("The edited downstream run did not finish: " + model.Status);
            model.SelectedResult = model.Results[0];
            var editedBitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); editedBitmap.Render(window);
            var editedEncoder = new PngBitmapEncoder(); editedEncoder.Frames.Add(BitmapFrame.Create(editedBitmap));
            using (var file = File.Create(Path.Combine(directory, "workbench-text-edits.png"))) editedEncoder.Save(file);
            // A temporarily blank editor cannot become a runnable text artifact, but closing must preserve its draft.
            var recoveryPath = Path.Combine(directory, "draft-recovery");
            var pendingModel = new WorkbenchViewModel(recoveryPath, recoveryPath, Dispatcher);
            pendingModel.CreateExample(); var pendingNode = pendingModel.Graph.Nodes.First(n => n.Kind == NodeKind.Text).Id;
            pendingModel.ViewerText = ""; await pendingModel.StopAsync();
            var reopenedModel = new WorkbenchViewModel(recoveryPath, recoveryPath, Dispatcher);
            try
            {
                reopenedModel.SelectNode(pendingNode);
                if (reopenedModel.ViewerText != "" || !reopenedModel.ViewerStatus.StartsWith("Unsaved edits", StringComparison.Ordinal)) throw new InvalidOperationException("A blank text draft did not survive reopening.");
                reopenedModel.ViewerText = "Recovered human draft"; reopenedModel.SaveCommand.Execute(null);
                if (reopenedModel.Graph.Nodes.Single(n => n.Id == pendingNode).Settings["text"] != "Recovered human draft") throw new InvalidOperationException("The recovered text draft could not be saved.");
            }
            finally { await reopenedModel.StopAsync(); }
            File.WriteAllText(Path.Combine(directory, "result.json"), model.ExportState());
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(directory, "error.txt"), ex.ToString()); Environment.ExitCode = 1; }
        finally { window.Close(); }
    }
}
