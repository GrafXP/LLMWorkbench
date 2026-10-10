using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using Workbench.Core;
using Workbench.Infrastructure;

namespace Workbench.Desktop;

public sealed partial class WorkbenchViewModel : Bindable
{
    private readonly Dispatcher _ui;
    private readonly string _projectsRoot;
    private SqliteProjectStore _store;
    private ProjectCommands _commands;
    private GraphRevision _graph;
    private JobScheduler _scheduler;
    private readonly ResourceArbiter _arbiter = new();
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource _session = new();
    private Task _queueTask = Task.CompletedTask;
    private Task _saveTask = Task.CompletedTask;
    private readonly TelemetryService _telemetry = new(new WindowsResourceCollector());
    private readonly Task _telemetryTask;
    private bool _switching;
    private bool _stopping;
    private Guid? _selectedNodeId;
    private string _status = "Ready";
    private string _selectedTitle = "", _selectedText = "", _draft = "";
    private RunSnapshot? _activeRun;
    private RunItem? _selectedRun;
    private JobItem? _selectedJob;
    public WorkbenchViewModel(string directory, string projectsRoot, Dispatcher ui)
    {
        _ui = ui; _projectsRoot = projectsRoot;
        _store = new(directory);
        var project = _store.LoadProject() ?? ProjectDocument.Create("My workbench");
        _store.SaveProject(project);
        _commands = new(project); _commands.Changed += GraphEdited;
        _graph = _commands.Current.Graph;
        _registry = new(ModelPath()); _registry.Reload();
        _runtime = new(() => _telemetry.Latest); _backend = new(_registry, _runtime);
        _runtime.Log += line =>
        {
            try { lock (_runtimeLogGate) File.AppendAllText(Path.Combine(_store.DirectoryPath, "runtime.log"), line + Environment.NewLine); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        };
        _scheduler = CreateScheduler();
        Resources = new(_telemetry);
        _telemetry.Sampled += sample => _ui.BeginInvoke(() => { if (!_stopping) Resources.Update(sample); });
        _telemetryTask = Task.Run(() => _telemetry.RunAsync(_lifetime.Token));
        _queueTask = StartQueue();
        AddTextCommand = new Command(() => AddNode(NodeKind.Text));
        AddCollectCommand = new Command(() => AddNode(NodeKind.Collect));
        AddSelectCommand = new Command(() => AddNode(NodeKind.Select));
        SaveDraftCommand = new Command(() => { var node = GraphNode.Create(NodeKind.Text, Graph.Nodes.Length * 38, Graph.Nodes.Length * 38); node.Settings["text"] = Draft; _commands.Add(node); SelectNode(node.Id); Draft = ""; }, () => !string.IsNullOrWhiteSpace(Draft));
        ApplyCommand = new Command(() => Guard(ApplyInspector), () => SelectedNode is not null);
        RemoveCommand = new Command(() => { if (_selectedNodeId is { } id) { _commands.Remove(id); SelectNode(null); } }, () => SelectedNode is not null);
        UndoCommand = new Command(() => _commands.Undo(), () => _commands.CanUndo);
        RedoCommand = new Command(() => _commands.Redo(), () => _commands.CanRedo);
        RunCommand = new Command(Run, () => !_switching);
        PauseCommand = new Command(() => { if (_scheduler.IsPaused) _scheduler.Resume(); else _scheduler.Pause(); Notify(nameof(QueueAction)); Notify(nameof(SchedulerStatus)); });
        CancelCommand = new Command(() => Guard(() => { if (SelectedJob is { } job) _scheduler.Cancel(job.Record.Id); }), () => SelectedJob is { Record.State: JobState.Queued or JobState.WaitingForInput or JobState.Running or JobState.AwaitingSelection or JobState.Blocked });
        RetryCommand = new Command(() => Guard(() => { if (SelectedJob is { } job) { _scheduler.Retry(job.Record.Id); _scheduler.Resume(); Notify(nameof(QueueAction)); } }), () => SelectedJob is { Record.State: JobState.Failed or JobState.Cancelled or JobState.Interrupted or JobState.Blocked });
        ChooseCommand = new Command(() => Guard(() => { var job = SelectedNodeJob; if (job is not null) { _scheduler.Choose(job.Id, Results.Where(r => r.IsChosen).Select(r => r.Artifact.Id).ToArray(), Feedback); _scheduler.Resume(); Notify(nameof(QueueAction)); } }), () => SelectedNodeJob?.State == JobState.AwaitingSelection);
        SaveCommand = new Command(() => Guard(SaveViewerEdits));
        NewCommand = new Command(() => _ = NewAsync(), () => !_switching);
        OpenCommand = new Command(() => _ = OpenAsync(), () => !_switching);
        ExampleCommand = new Command(CreateExample);
        InitializePhaseTwo();
        InitializeViewer();
        RefreshExecution();
    }
    public ResourceViewModel Resources { get; }
    public GraphRevision Graph => _graph;
    public string Title => $"{_commands.Current.Name} — LLM Workbench";
    public string ProjectPath => _store.DatabasePath;
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string QueueAction => _scheduler.IsPaused ? "Resume queue" : "Pause queue";
    public string SchedulerStatus => $"{(_scheduler.IsPaused ? "Queue paused" : "Queue enabled")} · {(_arbiter.GpuOwner is { } owner ? "GPU reserved by " + owner.ToString()[..8] : "GPU unreserved")} · {_runtime.Status.Message}";
    public string Draft { get => _draft; set => Set(ref _draft, value); }
    public string SelectedTitle { get => _selectedTitle; set => Set(ref _selectedTitle, value); }
    public string SelectedText { get => _selectedText; set { if (Set(ref _selectedText, value)) SyncViewerDraft(value); } }
    public GraphNode? SelectedNode => Graph.Nodes.SingleOrDefault(n => n.Id == _selectedNodeId);
    public Guid? SelectedNodeId => _selectedNodeId;
    public string SelectedKind => SelectedNode?.Kind.ToString() ?? "Select a node on the canvas";
    public Visibility TextEditorVisibility => SelectedNode?.Kind == NodeKind.Text ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ChoiceVisibility => SelectedNodeJob?.State == JobState.AwaitingSelection ? Visibility.Visible : Visibility.Collapsed;
    public string NodeDetail => SelectedNode is { } node ? string.Join("\n", node.Ports.Select(p => $"{(p.IsInput ? "IN" : "OUT")}  {p.Name} · {p.Type} {p.Cardinality}")) : "Select a node to inspect its text, ports, and saved results.";
    public ObservableCollection<RunItem> Runs { get; } = [];
    public ObservableCollection<JobItem> Jobs { get; } = [];
    public ObservableCollection<ArtifactChoice> Results { get; } = [];
    public RunItem? SelectedRun { get => _selectedRun; set { if (Set(ref _selectedRun, value)) { _activeRun = value?.Run; RefreshJobs(); } } }
    public JobItem? SelectedJob { get => _selectedJob; set { if (Set(ref _selectedJob, value) && value is not null) { SelectNode(value.Record.NodeId); Resources.HighlightStart = value.Record.StartedUtc; Resources.HighlightEnd = value.Record.FinishedUtc; Resources.Refresh(); } } }
    private JobRecord? SelectedNodeJob => Jobs.FirstOrDefault(j => j.Record.NodeId == _selectedNodeId)?.Record;
    public ICommand AddTextCommand { get; }
    public ICommand AddCollectCommand { get; }
    public ICommand AddSelectCommand { get; }
    public ICommand SaveDraftCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand RedoCommand { get; }
    public ICommand RunCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand ChooseCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand NewCommand { get; }
    public ICommand OpenCommand { get; }
    public ICommand ExampleCommand { get; }
    public event Action? GraphChanged;
    private JobScheduler CreateScheduler()
    {
        JobScheduler? scheduler = null;
        var executor = new GenerationExecutor(_store, _registry, _backend, _arbiter, _runtime.UnloadAsync, ct => scheduler!.WaitAtBoundaryAsync(ct));
        scheduler = new(_store, executor, _arbiter);
        scheduler.Changed += e =>
        {
            _telemetry.History.Mark(new(e.Timestamp, $"{e.State}: {e.Message}", e.JobId));
            _ui.BeginInvoke(() => { if (!_stopping && ReferenceEquals(scheduler, _scheduler)) RefreshExecution(); });
        };
        return scheduler;
    }
    private Task StartQueue() => Task.Run(async () =>
    {
        try { await _scheduler.RunLoopAsync(_session.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _ = _ui.BeginInvoke(() => Status = $"Queue stopped: {ex.Message}"); }
    });
    public void SelectNode(Guid? id)
    {
        var changed = _selectedNodeId != id;
        _loadingViewer = true;
        _selectedNodeId = id;
        var node = SelectedNode;
        SelectedTitle = node?.Title ?? ""; SelectedText = node?.Settings.GetValueOrDefault("text", "") ?? "";
        ReadGenerationInspector(node);
        _loadingViewer = false;
        if (changed) SelectedResult = null;
        foreach (var name in new[] { nameof(SelectedNode), nameof(SelectedNodeId), nameof(SelectedKind), nameof(TextEditorVisibility), nameof(NodeDetail), nameof(ChoiceVisibility) }) Notify(name);
        RefreshResults(); ShowNodeViewer(); GraphChanged?.Invoke(); CommandManager.InvalidateRequerySuggested();
    }
    public void AddNode(NodeKind kind)
    {
        var count = Graph.Nodes.Length;
        var node = GraphNode.Create(kind, 70 + count % 3 * 310, 50 + count / 3 * 220);
        _commands.Add(node); SelectNode(node.Id);
    }
    public void MoveNode(Guid id, double x, double y) => Guard(() => { var node = Graph.Nodes.Single(n => n.Id == id); _commands.Update(node with { X = x, Y = y }); });
    public void Connect(Guid source, string output, Guid target, string input) => Guard(() => _commands.Connect(source, output, target, input));
    public void Disconnect(Guid id) => Guard(() => _commands.Disconnect(id));
    public string NodeStatus(Guid id)
    {
        var job = Jobs.FirstOrDefault(j => j.Record.NodeId == id);
        if (job is null) return _activeRun is not null && !_activeRun.Graph.Nodes.Any(n => n.Id == id) ? "Outside this run" : "Draft";
        return job.Record.State + (job.Record.State == JobState.Running ? " · " + job.Record.Message : "") + ((_activeRun?.Graph.SourceRevisionId ?? _activeRun?.Graph.Id) != Graph.Id ? " · earlier revision" : "");
    }
    private void ApplyInspector()
    {
        if (SelectedNode is not { } node) return;
        if (string.IsNullOrWhiteSpace(SelectedTitle)) throw new InvalidOperationException("Give the node a title.");
        var settings = new Dictionary<string, string>(node.Settings);
        if (node.Kind == NodeKind.Text)
        {
            settings["text"] = SelectedText;
            if (SelectedText != node.Settings.GetValueOrDefault("text", "")) settings.Remove(ManualOutputs.Setting);
            if (_viewerDrafts.TryGetValue(node.Id, out var draft)) draft.SavedText = draft.Text = SelectedText;
        }
        WriteGenerationInspector(node, settings);
        _commands.Update(node with { Title = SelectedTitle.Trim(), Settings = settings });
    }
    private void GraphEdited()
    {
        var before = SelectedNode;
        var viewerRoot = _viewerArtifact?.Artifact.SourceNode;
        var previousOutputs = Graph.Nodes.FirstOrDefault(n => n.Id == viewerRoot)?.Settings.GetValueOrDefault(ManualOutputs.Setting);
        _graph = _commands.Current.Graph;
        Notify(nameof(Graph)); Notify(nameof(Title)); Notify(nameof(NodeDetail));
        if (SelectedNode is null) SelectNode(null);
        else if (before is not null && SelectedNode is { } after && (before.Title != after.Title || DocumentJson.Write(before.Settings) != DocumentJson.Write(after.Settings))) SelectNode(after.Id);
        if (!_savingViewer && _viewerArtifact is { } viewed && previousOutputs != Graph.Nodes.FirstOrDefault(n => n.Id == viewerRoot)?.Settings.GetValueOrDefault(ManualOutputs.Setting))
        {
            var original = SelectedResult ?? new ArtifactChoice(_store.LoadArtifacts().FirstOrDefault(a => a.Id.ToString() == viewed.Artifact.Metadata?.GetValueOrDefault("editRoot")) ?? viewed.Artifact, _store.DirectoryPath);
            ShowArtifact(original);
        }
        Notify(nameof(ManualOutputSummary)); Notify(nameof(ViewerReadOnly)); Notify(nameof(ViewerStatus));
        GraphChanged?.Invoke(); SaveSnapshot(_commands.Current); CommandManager.InvalidateRequerySuggested();
    }
    private void SaveSnapshot(ProjectDocument document)
    {
        try { _store.SaveProject(document); Status = "Project saved"; }
        catch (Exception ex) { Status = $"Could not save project: {ex.Message}"; }
    }
    private void Run() => Guard(() =>
    {
        SaveViewerEdits();
        ApplyInspector();
        _activeRun = _scheduler.Submit(_commands.Current, Guid.NewGuid());
        _scheduler.Resume(); RefreshExecution(); Notify(nameof(QueueAction)); Notify(nameof(SchedulerStatus)); Status = "Run queued from a saved graph revision";
    });
    private void RefreshExecution()
    {
        var runs = _store.LoadRuns(); var selectedId = _activeRun?.Id;
        Runs.Clear(); foreach (var run in runs) Runs.Add(new(run));
        _activeRun = runs.FirstOrDefault(r => r.Id == selectedId) ?? runs.FirstOrDefault();
        _selectedRun = Runs.FirstOrDefault(r => r.Run.Id == _activeRun?.Id); Notify(nameof(SelectedRun));
        RefreshJobs();
    }
    private void RefreshJobs()
    {
        var selectedId = _selectedJob?.Record.Id;
        Jobs.Clear();
        if (_activeRun is not null)
            foreach (var job in _store.LoadJobs(_activeRun.Id)) Jobs.Add(new(job, _activeRun.Graph.Nodes.Single(n => n.Id == job.NodeId).Title));
        _selectedJob = Jobs.FirstOrDefault(j => j.Record.Id == selectedId); Notify(nameof(SelectedJob));
        RefreshResults(); Notify(nameof(ChoiceVisibility)); GraphChanged?.Invoke(); CommandManager.InvalidateRequerySuggested();
        RefreshRenderProgress();
    }
    private void RefreshResults()
    {
        var selectedId = SelectedResult?.Artifact.Id;
        var previous = Results.ToDictionary(r => r.Artifact.Id);
        var chosen = Results.Where(r => r.IsChosen).Select(r => r.Artifact.Id).ToHashSet();
        var job = SelectedNodeJob;
        var ids = job is { State: JobState.AwaitingSelection } ? job.Inputs.Values.SelectMany(x => x).ToHashSet()
            : job is not null ? job.Outputs.ToHashSet() : Jobs.SelectMany(j => j.Record.Outputs).ToHashSet();
        if (job is not null) foreach (var item in _store.LoadRenderItems(job.Id).Where(i => i.ArtifactId.HasValue)) ids.Add(item.ArtifactId!.Value);
        Results.Clear();
        foreach (var artifact in _store.LoadArtifacts().Where(a => ids.Contains(a.Id)))
        {
            var choice = previous.GetValueOrDefault(artifact.Id) ?? new(artifact, _store.DirectoryPath);
            choice.IsChosen = chosen.Contains(artifact.Id) || job?.Selection?.Contains(artifact.Id) == true; Results.Add(choice);
        }
        SelectedResult = Results.FirstOrDefault(r => r.Artifact.Id == selectedId);
        if (SelectedResult is null && _viewerArtifact is null && _viewerNode is null && SelectedNode?.Kind != NodeKind.Text && Results.Count > 0)
            SelectedResult = Results[0];
    }
    public void Guard(Action action)
    {
        try { action(); }
        catch (Exception ex) { Status = ex.Message; }
    }
    public void CreateExample()
    {
        Guard(() =>
        {
            var a = GraphNode.Create(NodeKind.Text, 60, 65); a = a with { Title = "Alpine lake · warm light" }; a.Settings["text"] = "A quiet alpine lake at sunrise, warm light on the mountain peaks and soft mist above the water.";
            var b = GraphNode.Create(NodeKind.Text, 60, 295); b = b with { Title = "Alpine lake · blue hour" }; b.Settings["text"] = "A quiet alpine lake at blue hour, cool shadows on the mountain peaks and a clear reflection in still water.";
            var collect = GraphNode.Create(NodeKind.Collect, 410, 180);
            var select = GraphNode.Create(NodeKind.Select, 760, 180);
            var graph = new GraphRevision(Guid.NewGuid(), [a, b, collect, select],
                [new(Guid.NewGuid(), a.Id, "text", collect.Id, "a"), new(Guid.NewGuid(), b.Id, "text", collect.Id, "b"), new(Guid.NewGuid(), collect.Id, "items", select.Id, "items")]);
            // Append the example to preserve any work already on the canvas.
            var current = Graph;
            var offset = current.Nodes.Length == 0 ? 0 : current.Nodes.Max(n => n.Y) + GraphNode.Height + 70;
            graph = graph with { Nodes = [.. current.Nodes, .. graph.Nodes.Select(n => n with { Y = n.Y + offset })], Edges = [.. current.Edges, .. graph.Edges] };
            _commands.ApplyGraph(graph); SelectNode(a.Id);
        });
    }
    private async Task NewAsync()
    {
        await SwitchAsync(Path.Combine(_projectsRoot, Guid.NewGuid().ToString("N")));
    }
    private async Task OpenAsync()
    {
        var dialog = new OpenFileDialog { Title = "Open a workbench project", Filter = "Workbench project|project.sqlite", InitialDirectory = _projectsRoot };
        if (dialog.ShowDialog() == true) await SwitchAsync(Path.GetDirectoryName(dialog.FileName)!);
    }
    private async Task SwitchAsync(string directory)
    {
        if (_switching || string.Equals(Path.GetFullPath(directory), _store.DirectoryPath, StringComparison.OrdinalIgnoreCase)) return;
        _switching = true;
        SqliteProjectStore? next = null;
        try
        {
            SaveViewerEdits();
            next = await Task.Run(() => new SqliteProjectStore(directory));
            var document = next.LoadProject() ?? ProjectDocument.Create("New project");
            next.SaveProject(document);
            _session.Cancel(); await _queueTask; await StopChatAsync(); await _runtime.UnloadAsync(); await _saveTask;
            _commands.Changed -= GraphEdited; _store.Dispose(); _session.Dispose();
            _store = next; next = null; _commands = new(document); _commands.Changed += GraphEdited; _graph = _commands.Current.Graph;
            ResetViewer();
            _session = new(); _scheduler = CreateScheduler(); _queueTask = StartQueue();
            BindChat();
            RestoreViewerDrafts();
            _selectedNodeId = null; _activeRun = null; _selectedJob = null; _selectedRun = null;
            SelectNode(null); Notify(nameof(Graph)); Notify(nameof(Title)); Notify(nameof(ProjectPath)); Notify(nameof(QueueAction)); Notify(nameof(SchedulerStatus));
            RefreshExecution(); Status = "Project opened"; GraphChanged?.Invoke();
        }
        catch (Exception ex) { Status = $"Could not open project: {ex.Message}"; }
        finally { next?.Dispose(); _switching = false; Notify(nameof(CanSelectTextModel)); CommandManager.InvalidateRequerySuggested(); }
    }
    public async Task StopAsync()
    {
        // Retain pending viewer drafts even when the user closes without pressing Save.
        Guard(SaveViewerEdits);
        Guard(PersistViewerDrafts);
        _stopping = true; _session.Cancel(); _lifetime.Cancel();
        await StopChatAsync();
        try { await Task.WhenAll(_queueTask, _telemetryTask); } catch (OperationCanceledException) { }
        await _saveTask;
        await _runtime.DisposeAsync();
        _store.Dispose(); _telemetry.Dispose(); _chatCancellation.Dispose(); _session.Dispose(); _lifetime.Dispose();
    }
    public string ExportState() => DocumentJson.Write(new { Project = _commands.Current, Resources = Resources.Snapshot, Jobs = Jobs.Select(j => j.Record).ToArray() });
}
public sealed record RunItem(RunSnapshot Run)
{
    public string Label => $"{Run.CreatedUtc.ToLocalTime():dd MMM HH:mm:ss} · {Run.Id.ToString()[..8]}";
    public override string ToString() => Label;
}
public sealed record JobItem(JobRecord Record, string Title)
{
    public string State => Record.State.ToString();
    public string Message => Record.Message;
    public string Attempt => Record.Attempt.ToString();
}
public sealed class ArtifactChoice(Artifact artifact, string? directory = null) : Bindable
{
    public Artifact Artifact { get; } = artifact;
    public string Content => Artifact.Kind == ArtifactKind.ImageSpecification ? DocumentJson.Read<ImageSpecification>(Artifact.Content).Prompt : Artifact.Content;
    public string Detail => $"{Artifact.Kind} · {Artifact.Metadata?.GetValueOrDefault("model") ?? Artifact.Id.ToString()[..8]}" + (Artifact.Metadata?.GetValueOrDefault("seed") is { } seed ? $" · seed {seed}" : "");
    public System.Windows.Media.Imaging.BitmapSource? Preview { get; } = LoadPreview(artifact, directory);
    public Visibility ImageVisibility => Preview is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility TextVisibility => Artifact.Kind == ArtifactKind.Image ? Visibility.Collapsed : Visibility.Visible;
    public string? FullImagePath => ImagePath(Artifact, directory);
    public ICommand OpenImageCommand { get; } = new Command(() =>
    {
        if (ImagePath(artifact, directory) is { } path)
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
    }, () => ImagePath(artifact, directory) is not null);
    private static string? ImagePath(Artifact artifact, string? directory)
    {
        if (artifact.Kind != ArtifactKind.Image || directory is null || artifact.RelativePath is null || Path.IsPathRooted(artifact.RelativePath)) return null;
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, artifact.RelativePath));
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase) && File.Exists(path) ? path : null;
    }
    private static System.Windows.Media.Imaging.BitmapSource? LoadPreview(Artifact artifact, string? directory)
    {
        if (ImagePath(artifact, directory) is not { } path) return null;
        try
        {
            var bitmap = new System.Windows.Media.Imaging.BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 256; bitmap.UriSource = new Uri(path); bitmap.EndInit(); bitmap.Freeze(); return bitmap;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or System.IO.FileFormatException) { return null; }
    }
    private bool _chosen;
    public bool IsChosen { get => _chosen; set => Set(ref _chosen, value); }
}
