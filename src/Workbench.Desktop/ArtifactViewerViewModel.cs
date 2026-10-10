using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Workbench.Core;

namespace Workbench.Desktop;

public sealed partial class WorkbenchViewModel
{
    private sealed record PendingText(Guid? NodeId, Guid? ArtifactId, string Text, Guid[] OutputIds);
    private sealed class TextDraft(Guid? nodeId, Artifact? artifact, string text)
    {
        public Guid? NodeId { get; } = nodeId;
        public Artifact? Artifact { get; set; } = artifact;
        public string SavedText { get; set; } = text;
        public string Text { get; set; } = text;
        public Guid[] OutputIds { get; set; } = [];
        public bool Dirty => Text != SavedText;
    }
    private readonly Dictionary<Guid, TextDraft> _viewerDrafts = [];
    private TextDraft? _viewerDraft;
    private ArtifactChoice? _viewerArtifact;
    private Guid? _viewerNode;
    private BitmapSource? _viewerImage;
    private bool _savingViewer, _loadingViewer;
    public ICommand SaveViewerCommand { get; private set; } = null!;
    public ICommand RunViewerCommand { get; private set; } = null!;
    public ICommand PreviousResultCommand { get; private set; } = null!;
    public ICommand NextResultCommand { get; private set; } = null!;
    public ICommand EditSourceTextCommand { get; private set; } = null!;
    public ICommand EditPreparedPromptCommand { get; private set; } = null!;
    public ICommand ClearManualOutputsCommand { get; private set; } = null!;
    public event Action? ViewerRequested;
    public string ViewerTitle => _viewerNode is { } id ? Graph.Nodes.FirstOrDefault(n => n.Id == id)?.Title ?? "Text draft"
        : _viewerArtifact?.Artifact.Kind switch { ArtifactKind.Image => "Image", ArtifactKind.ImageSpecification => "Prepared image prompt", ArtifactKind.Text => "Text result", _ => "Viewer" };
    public string ViewerDetail => _viewerArtifact is { } a ? a.Detail + (a.Artifact.Metadata?.ContainsKey("manualEdit") == true ? " · manual revision" : "")
        : _viewerNode is not null ? "Draft text · saved with the workflow" : "Images and editable texts";
    public BitmapSource? ViewerImage => _viewerImage;
    public bool ViewerReadOnly => _viewerDraft is null || !Graph.Nodes.Any(n => n.Id == ViewerRoot);
    private Guid? ViewerRoot => _viewerNode ?? _viewerArtifact?.Artifact.SourceNode;
    public Visibility ViewerImageVisibility => _viewerArtifact?.Artifact.Kind == ArtifactKind.Image ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ViewerTextVisibility => _viewerDraft is not null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ViewerEmptyVisibility => _viewerArtifact is null && _viewerNode is null ? Visibility.Visible : Visibility.Collapsed;
    public string ViewerText
    {
        get => _viewerDraft?.Text ?? "";
        set
        {
            if (_viewerDraft is null || ViewerReadOnly || _viewerDraft.Text == value) return;
            _viewerDraft.Text = value;
            if (_viewerNode == _selectedNodeId && _viewerNode is not null) SelectedText = value;
            Notify(); Notify(nameof(ViewerStatus)); CommandManager.InvalidateRequerySuggested();
        }
    }
    public string ViewerStatus => _viewerDraft is { Dirty: true } ? "Unsaved edits · Save text edits, Run downstream, or F5 to use them."
        : ViewerReadOnly && _viewerDraft is not null ? "The source node was removed. This saved text remains available to read."
        : _viewerArtifact is { } a && Graph.Nodes.FirstOrDefault(n => n.Id == a.Artifact.SourceNode) is { } node && ManualOutputs.Read(node).Contains(a.Artifact.Id)
            ? "Saved manual version · future runs use these outputs. Original results remain in history."
        : _viewerNode is not null ? "Edit this draft, then save or run the workflow."
        : _viewerArtifact?.Artifact.Kind == ArtifactKind.Image ? _viewerImage is null ? "The saved image could not be loaded." : $"{_viewerImage.PixelWidth} × {_viewerImage.PixelHeight} pixels · original resolution"
        : _viewerDraft is not null ? "Editing this result saves a new version for the next run." : "";
    public string ManualOutputSummary => SelectedNode is { } node && ManualOutputs.Read(node) is { Length: > 0 } ids
        ? $"Using {ids.Length} saved text output(s). Clear saved edits to generate fresh outputs." : "";

    private void InitializeViewer()
    {
        SaveViewerCommand = new Command(() => Guard(SaveViewerEdits), () => _viewerDraft is { Dirty: true } && !ViewerReadOnly);
        RunViewerCommand = new Command(() => Guard(() =>
        {
            var root = ViewerRoot ?? throw new InvalidOperationException("Select a text to run from.");
            SaveViewerEdits();
            _activeRun = _scheduler.Submit(ManualOutputs.Downstream(_commands.Current, root), Guid.NewGuid());
            _scheduler.Resume(); RefreshExecution(); Notify(nameof(QueueAction)); Notify(nameof(SchedulerStatus));
            Status = "Downstream workflow queued with your saved texts";
        }), () => !_switching && !ViewerReadOnly && ViewerRoot is { } root && Graph.Nodes.FirstOrDefault(n => n.Id == root) is { } node &&
            (node.Kind == NodeKind.Text || _viewerDraft?.Dirty == true || ManualOutputs.Read(node).Length > 0));
        PreviousResultCommand = new Command(() => NavigateResult(-1), () => Results.Count > 1);
        NextResultCommand = new Command(() => NavigateResult(1), () => Results.Count > 1);
        EditSourceTextCommand = new Command(() => { if (ImageSourceText() is { } text) SelectedResult = new(text, _store.DirectoryPath); }, () => ImageSourceText() is not null);
        EditPreparedPromptCommand = new Command(() => { if (ImagePrompt() is { } spec) SelectedResult = new(spec, _store.DirectoryPath); }, () => ImagePrompt() is not null);
        ClearManualOutputsCommand = new Command(() => Guard(() =>
        {
            if (SelectedNode is not { } node) return;
            var settings = new Dictionary<string, string>(node.Settings); settings.Remove(ManualOutputs.Setting);
            foreach (var key in _viewerDrafts.Where(kv => kv.Value.Artifact?.SourceNode == node.Id).Select(kv => kv.Key).ToArray()) _viewerDrafts.Remove(key);
            _commands.Update(node with { Settings = settings });
            if (_viewerArtifact?.Artifact.SourceNode == node.Id)
            {
                var artifact = _viewerArtifact.Artifact;
                var original = _store.LoadArtifacts().FirstOrDefault(a => a.Id.ToString() == artifact.Metadata?.GetValueOrDefault("editRoot")) ?? artifact;
                ShowArtifact(new(original, _store.DirectoryPath));
            }
            RefreshViewer(); Status = "Saved output overrides cleared · next run generates fresh texts";
        }), () => SelectedNode is { } node && ManualOutputs.Read(node).Length > 0);
        RestoreViewerDrafts();
    }
    private void NavigateResult(int delta)
    {
        if (Results.Count == 0) return;
        var index = SelectedResult is null ? -1 : Results.IndexOf(SelectedResult);
        if (index < 0) index = delta > 0 ? -1 : 0;
        SelectedResult = Results[(index + delta + Results.Count) % Results.Count];
    }
    private Artifact? ImagePrompt() => _viewerArtifact?.Artifact.Kind == ArtifactKind.Image
        ? _store.LoadArtifacts().FirstOrDefault(a => a.Kind == ArtifactKind.ImageSpecification && _viewerArtifact.Artifact.Parents.Contains(a.Id)) : null;
    private Artifact? ImageSourceText() => ImagePrompt() is { } spec
        ? _store.LoadArtifacts().FirstOrDefault(a => a.Kind == ArtifactKind.Text && spec.Parents.Contains(a.Id)) : null;
    public string ViewerImageText => _viewerArtifact?.Artifact.Kind == ArtifactKind.Image
        ? "Rendered prompt:\n" + _viewerArtifact.Content + (ImageSourceText() is { } source ? "\n\nSource text:\n" + source.Content : "") : "";
    private void ShowNodeViewer()
    {
        if (_savingViewer) return;
        if (SelectedNode is { Kind: NodeKind.Text } node)
        {
            _viewerNode = node.Id; _viewerArtifact = null; _viewerImage = null;
            var text = node.Settings.GetValueOrDefault("text", "");
            if (!_viewerDrafts.TryGetValue(node.Id, out _viewerDraft)) _viewerDrafts[node.Id] = _viewerDraft = new(node.Id, null, text);
            else if (!_viewerDraft.Dirty) _viewerDraft.Text = _viewerDraft.SavedText = text;
            SelectedText = _viewerDraft.Text; RefreshViewer(); ViewerRequested?.Invoke();
        }
        else if (SelectedResult is { } selected) ShowArtifact(selected);
        else if (Results.Count > 0) SelectedResult = Results[0];
        else { _viewerDraft = null; _viewerNode = null; _viewerArtifact = null; _viewerImage = null; RefreshViewer(); }
    }
    private void ShowArtifact(ArtifactChoice choice)
    {
        if (_savingViewer) return;
        var artifact = choice.Artifact;
        if (Graph.Nodes.FirstOrDefault(n => n.Id == artifact.SourceNode) is { } node)
        {
            var all = _store.LoadArtifacts().ToDictionary(a => a.Id);
            var root = artifact.Metadata?.GetValueOrDefault("editRoot") ?? artifact.Id.ToString();
            artifact = ManualOutputs.Read(node).Select(id => all.GetValueOrDefault(id)).FirstOrDefault(a => a is not null &&
                (a.Id == artifact.Id || a.Metadata?.GetValueOrDefault("editRoot") == root || a.Parents.Contains(artifact.Id))) ?? artifact;
        }
        if (_viewerNode is null && _viewerArtifact?.Artifact.Id == artifact.Id) { RefreshViewer(); ViewerRequested?.Invoke(); return; }
        _viewerNode = null; _viewerArtifact = artifact.Id == choice.Artifact.Id ? choice : new(artifact, _store.DirectoryPath);
        _viewerDraft = null; _viewerImage = null;
        if (artifact.Kind is ArtifactKind.Text or ArtifactKind.ImageSpecification)
        {
            var key = Guid.Parse(artifact.Metadata?.GetValueOrDefault("editRoot") ?? artifact.Id.ToString());
            if (!_viewerDrafts.TryGetValue(key, out _viewerDraft))
            {
                _viewerDrafts[key] = _viewerDraft = new(null, artifact, _viewerArtifact.Content);
                _viewerDraft.OutputIds = _activeRun is null ? [] : _store.LoadJobs(_activeRun.Id).FirstOrDefault(j => j.NodeId == artifact.SourceNode)?.Outputs ?? [];
            }
            else if (!_viewerDraft.Dirty && _viewerDraft.Artifact?.Id != artifact.Id)
            {
                _viewerDraft.Artifact = artifact; _viewerDraft.SavedText = _viewerDraft.Text = _viewerArtifact.Content;
                _viewerDraft.OutputIds = _activeRun is null ? [] : _store.LoadJobs(_activeRun.Id).FirstOrDefault(j => j.NodeId == artifact.SourceNode)?.Outputs ?? [];
            }
        }
        else if (_viewerArtifact.FullImagePath is { } path)
        {
            try
            {
                var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(path); bitmap.EndInit(); bitmap.Freeze(); _viewerImage = bitmap;
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException or UnauthorizedAccessException) { Status = "Could not load image: " + ex.Message; }
        }
        RefreshViewer(); ViewerRequested?.Invoke();
    }
    private void SyncViewerDraft(string text)
    {
        if (_loadingViewer || _viewerNode != _selectedNodeId || _viewerDraft is null || _viewerNode is null) return;
        _viewerDraft.Text = text; Notify(nameof(ViewerText)); Notify(nameof(ViewerStatus));
    }
    private void SaveViewerEdits()
    {
        var drafts = _viewerDrafts.Values.Where(d => d.Dirty).ToArray();
        if (drafts.Any(d => string.IsNullOrWhiteSpace(d.Text))) throw new InvalidOperationException("Write some text before saving your edits.");
        _savingViewer = true;
        try
        {
            foreach (var draft in drafts)
            {
                var root = draft.NodeId ?? draft.Artifact!.SourceNode;
                var node = Graph.Nodes.SingleOrDefault(n => n.Id == root) ?? throw new InvalidOperationException("The edited text's source node was removed.");
                if (draft.NodeId is not null)
                {
                    var settings = new Dictionary<string, string>(node.Settings) { ["text"] = draft.Text }; settings.Remove(ManualOutputs.Setting);
                    _commands.Update(node with { Settings = settings });
                }
                else
                {
                    var source = draft.Artifact!;
                    var all = _store.LoadArtifacts().ToDictionary(a => a.Id);
                    var ids = ManualOutputs.Read(node);
                    if (!ids.Contains(source.Id)) ids = draft.OutputIds;
                    if (!ids.Contains(source.Id)) throw new InvalidOperationException("Select the original run for this text before saving its edits.");
                    var edit = _store.SaveTextRevision(source.Id, draft.Text); all.Add(edit.Id, edit);
                    _commands.Update(ManualOutputs.Pin(node, ids.Select(id => id == source.Id ? edit : all[id]).ToArray()));
                    draft.Artifact = edit; draft.OutputIds = ManualOutputs.Read(Graph.Nodes.Single(n => n.Id == node.Id));
                }
                draft.SavedText = draft.Text;
            }
        }
        finally { _savingViewer = false; }
        if (_viewerDraft?.Artifact is { } saved) _viewerArtifact = new(saved, _store.DirectoryPath);
        RefreshViewer(); SaveSnapshot(_commands.Current); PersistViewerDrafts();
    }
    private void PersistViewerDrafts() => _store.SavePreference("pendingViewerTexts", DocumentJson.Write(_viewerDrafts.Values.Where(d => d.Dirty)
        .Select(d => new PendingText(d.NodeId, d.Artifact?.Id, d.Text, d.OutputIds)).ToArray()));
    private void RestoreViewerDrafts()
    {
        if (_store.LoadPreference("pendingViewerTexts") is not { } json) return;
        var all = _store.LoadArtifacts().ToDictionary(a => a.Id);
        foreach (var pending in DocumentJson.Read<PendingText[]>(json))
        {
            if (pending.NodeId is { } id && Graph.Nodes.FirstOrDefault(n => n.Id == id && n.Kind == NodeKind.Text) is { } node)
                _viewerDrafts[id] = new(id, null, node.Settings.GetValueOrDefault("text", "")) { Text = pending.Text };
            else if (pending.ArtifactId is { } artifactId && all.TryGetValue(artifactId, out var artifact) &&
                artifact.Kind is ArtifactKind.Text or ArtifactKind.ImageSpecification && Graph.Nodes.Any(n => n.Id == artifact.SourceNode))
                _viewerDrafts[Guid.Parse(artifact.Metadata?.GetValueOrDefault("editRoot") ?? artifact.Id.ToString())] = new(null, artifact,
                    new ArtifactChoice(artifact).Content) { Text = pending.Text, OutputIds = pending.OutputIds };
        }
    }
    private void RefreshViewer()
    {
        foreach (var name in new[] { nameof(ViewerTitle), nameof(ViewerDetail), nameof(ViewerImage), nameof(ViewerReadOnly), nameof(ViewerText),
            nameof(ViewerImageVisibility), nameof(ViewerTextVisibility), nameof(ViewerEmptyVisibility), nameof(ViewerStatus), nameof(ViewerImageText), nameof(ManualOutputSummary) }) Notify(name);
        CommandManager.InvalidateRequerySuggested();
    }
    private void ResetViewer()
    {
        _viewerDrafts.Clear(); _viewerDraft = null; _viewerArtifact = null; _viewerNode = null; _viewerImage = null; RefreshViewer();
    }
}
