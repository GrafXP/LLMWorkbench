using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Workbench.Core;
using Workbench.Infrastructure;

namespace Workbench.Desktop;

public sealed partial class WorkbenchViewModel
{
    private readonly ModelRegistry _registry;
    private readonly RuntimeManager _runtime;
    private readonly LocalGenerationBackend _backend;
    private readonly object _runtimeLogGate = new();
    private AgentChatService _chat = null!;
    private CancellationTokenSource _chatCancellation = new();
    private Task _chatTask = Task.CompletedTask;
    private string _chatInput = "", _streamingReply = "", _chatStatus = "Ready · Qwen loads on demand", _feedback = "";
    private bool _chatBusy;
    private bool _preferThroughput;
    public bool PreferThroughput { get => _preferThroughput; set => Set(ref _preferThroughput, value); }
    private string _roundBrief = "A quiet alpine lake with mountain reflections", _roundInstructions = "Vary composition, lighting and atmosphere; preserve the lake and mountains.";
    private string _roundCount = "6", _roundWidth = "512", _roundHeight = "512", _roundSeeds = "42";
    private ProfileChoice? _imageModel;
    private ProfileChoice? _textModel;
    private string _instructions = "", _count = "6", _profiles = "klein4b", _width = "512", _height = "512", _seeds = "42", _negative = "";
    public ObservableCollection<ProfileChoice> Models { get; } = [];
    public ObservableCollection<ProfileChoice> TextModels { get; } = [];
    public ObservableCollection<ChatItem> Conversation { get; } = [];
    public ObservableCollection<RenderItem> RenderItems { get; } = [];
    private ArtifactChoice? _selectedResult;
    public HashSet<Guid> ArtifactPath { get; } = [];
    public ArtifactChoice? SelectedResult
    {
        get => _selectedResult;
        set
        {
            if (!Set(ref _selectedResult, value)) return;
            if (value is not null) ShowArtifact(value);
            ArtifactPath.Clear();
            var artifacts = _store.LoadArtifacts().ToDictionary(a => a.Id); var pending = new Stack<Guid>(); var seen = new HashSet<Guid>();
            if (value is not null) pending.Push(value.Artifact.Id);
            while (pending.TryPop(out var id)) if (seen.Add(id) && artifacts.TryGetValue(id, out var a)) { ArtifactPath.Add(a.SourceNode); foreach (var parent in a.Parents) pending.Push(parent); }
            var nodes = Graph.Nodes.ToDictionary(n => n.Id); var transparent = new Queue<Guid>(ArtifactPath);
            while (transparent.TryDequeue(out var id)) foreach (var edge in Graph.Edges.Where(e => e.TargetNode == id))
                if (nodes.TryGetValue(edge.SourceNode, out var node) && node.Kind is NodeKind.Collect or NodeKind.Select && ArtifactPath.Add(node.Id)) transparent.Enqueue(node.Id);
            Notify(nameof(ArtifactOrigin)); GraphChanged?.Invoke();
        }
    }
    public string ArtifactOrigin
    {
        get
        {
            if (SelectedResult is not { } selected) return "Select a result to view it and highlight its source path.";
            var all = _store.LoadArtifacts().ToDictionary(a => a.Id);
            var source = selected.Artifact.Parents.Select(id => all.GetValueOrDefault(id)).FirstOrDefault(a => a?.Kind == ArtifactKind.ImageSpecification);
            if (source is not null) source = source.Parents.Select(id => all.GetValueOrDefault(id)).FirstOrDefault(a => a?.Kind == ArtifactKind.Text);
            return source is null ? "Saved artifact · " + selected.Detail : "Source text: " + source.Content;
        }
    }
    public ProfileChoice? ImageModel { get => _imageModel; set { if (Set(ref _imageModel, value)) { Notify(nameof(ModelDetail)); Notify(nameof(BatchPreview)); } } }
    public ProfileChoice? TextModel
    {
        get => _textModel;
        set
        {
            if (value is null || !CanSelectTextModel || value.Profile.Id == _textModel?.Profile.Id) return;
            _registry.SelectTextModel(value.Profile.Id);
            _store.SavePreference("textModelId", value.Profile.Id);
            Set(ref _textModel, value); Notify(nameof(TextModelDetail));
        }
    }
    public bool CanSelectTextModel => !_chatBusy && !_switching && _arbiter.GpuOwner is null;
    public string TextModelDetail => TextModel?.Profile.Notes ?? "Choose a text model for chat and generation nodes.";
    public string ModelDetail => ImageModel?.Detail ?? "Choose an image profile";
    public string RuntimeDetail => _runtime.Status.Message + (_runtime.Status.Pid is { } pid ? $" · PID {pid} · Workbench owned" : "");
    public string ChatInput { get => _chatInput; set => Set(ref _chatInput, value); }
    public string StreamingReply { get => _streamingReply; private set => Set(ref _streamingReply, value); }
    public string ChatStatus { get => _chatStatus; private set => Set(ref _chatStatus, value); }
    public string Feedback { get => _feedback; set => Set(ref _feedback, value); }
    public string RoundBrief { get => _roundBrief; set => Set(ref _roundBrief, value); }
    public string RoundInstructions { get => _roundInstructions; set => Set(ref _roundInstructions, value); }
    public string RoundCount { get => _roundCount; set { if (Set(ref _roundCount, value)) Notify(nameof(BatchPreview)); } }
    public string RoundWidth { get => _roundWidth; set => Set(ref _roundWidth, value); }
    public string RoundHeight { get => _roundHeight; set => Set(ref _roundHeight, value); }
    public string RoundSeeds { get => _roundSeeds; set { if (Set(ref _roundSeeds, value)) Notify(nameof(BatchPreview)); } }
    public string BatchPreview
    {
        get
        {
            if (!int.TryParse(RoundCount, out var count) || count is < 1 or > 12) return "Choose 1–12 variations per source";
            var seedCount = RoundSeeds.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Length;
            var sources = SelectedNodeJob is { State: JobState.Done, Selection: { Length: > 0 } } j ? j.Outputs.Length : 1;
            return $"{sources} source(s) × {count} texts × 1 model × {seedCount} seeds = {sources * count * seedCount} images · Cartesian expansion";
        }
    }
    public string SelectedInstructions { get => _instructions; set => Set(ref _instructions, value); }
    public string SelectedCount { get => _count; set => Set(ref _count, value); }
    public string SelectedProfiles { get => _profiles; set { if (Set(ref _profiles, value)) Notify(nameof(NodeMatrixPreview)); } }
    public string SelectedWidth { get => _width; set => Set(ref _width, value); }
    public string SelectedHeight { get => _height; set => Set(ref _height, value); }
    public string SelectedSeeds { get => _seeds; set { if (Set(ref _seeds, value)) Notify(nameof(NodeMatrixPreview)); } }
    public string SelectedNegative { get => _negative; set => Set(ref _negative, value); }
    public Visibility QwenEditorVisibility => SelectedNode?.Kind is NodeKind.Variations or NodeKind.GenerateText ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PreparationVisibility => SelectedNode?.Kind == NodeKind.PrepareImage ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FeedbackVisibility => SelectedNode?.Kind is NodeKind.Select or NodeKind.Branch ? Visibility.Visible : Visibility.Collapsed;
    public string NodeMatrixPreview
    {
        get
        {
            if (SelectedNode?.Kind != NodeKind.PrepareImage) return "";
            int Count(Guid id)
            {
                var node = Graph.Nodes.Single(n => n.Id == id);
                if (node.Kind == NodeKind.Text || node.Kind == NodeKind.GenerateText) return 1;
                if (node.Kind == NodeKind.Select) return _store.LoadRuns().SelectMany(r => _store.LoadJobs(r.Id)).FirstOrDefault(j => j.NodeId == id && j.State == JobState.Done)?.Outputs.Length ?? 0;
                var input = Graph.Edges.Where(e => e.TargetNode == id).Sum(e => Count(e.SourceNode));
                return node.Kind == NodeKind.Variations && int.TryParse(node.Settings.GetValueOrDefault("count", "6"), out var count) ? checked(input * count) : input;
            }
            try
            {
                var texts = Graph.Edges.Where(e => e.TargetNode == SelectedNode.Id).Sum(e => Count(e.SourceNode));
                var models = SelectedProfiles.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct().Count();
                var seeds = SelectedSeeds.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Length;
                var total = checked(texts * models * seeds);
                return $"{texts} texts × {models} models × {seeds} seeds = {total} images · Cartesian expansion" + (total > 256 ? " · exceeds the 256-image limit" : "");
            }
            catch (OverflowException) { return "The batch exceeds the supported size."; }
        }
    }
    public ICommand SendChatCommand { get; private set; } = null!;
    public ICommand CancelChatCommand { get; private set; } = null!;
    public ICommand AddRoundCommand { get; private set; } = null!;
    public ICommand BranchCommand { get; private set; } = null!;
    public ICommand RefreshModelsCommand { get; private set; } = null!;
    public ICommand UnloadCommand { get; private set; } = null!;
    public ICommand AddVariationsCommand { get; private set; } = null!;
    public ICommand AddPrepareCommand { get; private set; } = null!;
    public ICommand AddImageCommand { get; private set; } = null!;
    public ICommand AddImageSelectCommand { get; private set; } = null!;
    public ICommand AddGenerateTextCommand { get; private set; } = null!;
    private static string ModelPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LLMWorkbench.slnx"))) return Path.Combine(directory.FullName, "models.json");
            directory = directory.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, "models.json");
    }
    private void InitializePhaseTwo()
    {
        RefreshModels(); BindChat();
        _runtime.Changed += state =>
        {
            if (!_stopping && _arbiter.GpuOwner is { } owner) _scheduler.UpdateProgress(owner, state.Message);
            _telemetry.History.Mark(new(DateTimeOffset.UtcNow, state.Message));
            _ui.BeginInvoke(() => { if (!_stopping) { Notify(nameof(RuntimeDetail)); Notify(nameof(SchedulerStatus)); } });
        };
        _arbiter.Changed += () => _ui.BeginInvoke(() => { if (!_stopping) { Notify(nameof(SchedulerStatus)); Notify(nameof(CanSelectTextModel)); } });
        SendChatCommand = new Command(() => _chatTask = SendChatAsync(), () => !_chatBusy && !_switching && !string.IsNullOrWhiteSpace(ChatInput));
        CancelChatCommand = new Command(() => _chatCancellation.Cancel(), () => _chatBusy);
        AddRoundCommand = new Command(() => Guard(() =>
        {
            var id = WorkflowCommands.AddImageRound(_commands, RoundBrief, RoundInstructions, int.Parse(RoundCount), RequiredProfile(), BatchPlan.Dimension(RoundWidth), BatchPlan.Dimension(RoundHeight), RoundSeeds);
            SelectNode(id); Status = "Image round created · press Run graph to execute";
        }));
        BranchCommand = new Command(() => Guard(BranchRound), () => SelectedNodeJob is { State: JobState.Done, Selection: { Length: > 0 } } && SelectedNode?.Ports.Single(p => !p.IsInput).Type == ArtifactKind.Image);
        RefreshModelsCommand = new Command(() => Guard(RefreshModels));
        UnloadCommand = new Command(() => _ = UnloadIdleAsync(), () => _arbiter.GpuOwner is null && !_chatBusy);
        AddVariationsCommand = new Command(() => AddNode(NodeKind.Variations));
        AddGenerateTextCommand = new Command(() => AddNode(NodeKind.GenerateText));
        AddPrepareCommand = new Command(() => AddNode(NodeKind.PrepareImage));
        AddImageCommand = new Command(() => AddNode(NodeKind.Image));
        AddImageSelectCommand = new Command(() => { var n = GraphNode.Create(NodeKind.Select, 60, Graph.Nodes.Length * 40, ArtifactKind.Image); _commands.Add(n); SelectNode(n.Id); });
    }
    private string RequiredProfile() => ImageModel is { Profile.Available: true } selected ? selected.Profile.Id : throw new InvalidOperationException("Choose an available image profile.");
    private void RefreshModels()
    {
        _registry.Reload();
        TextModels.Clear(); foreach (var p in _registry.Profiles.Where(p => p.Runtime == RuntimeKind.Llama)) TextModels.Add(new(p));
        RestoreTextModel();
        var id = ImageModel?.Profile.Id; Models.Clear();
        foreach (var p in _registry.Profiles.Where(p => p.Runtime == RuntimeKind.StableDiffusion)) Models.Add(new(p));
        ImageModel = Models.FirstOrDefault(p => p.Profile.Id == id) ?? Models.FirstOrDefault(p => p.Profile.Id == "klein4b" && p.Profile.Available) ?? Models.FirstOrDefault(p => p.Profile.Available); Notify(nameof(ModelDetail));
    }
    private void RestoreTextModel()
    {
        var id = _store.LoadPreference("textModelId");
        if (TextModels.Any(p => p.Profile.Id == id && p.Profile.Available)) _registry.SelectTextModel(id!);
        _textModel = TextModels.FirstOrDefault(p => p.Profile.Id == _registry.Qwen.Id);
        if (_textModel?.Profile.Available == true) _store.SavePreference("textModelId", _textModel.Profile.Id);
        Notify(nameof(TextModel)); Notify(nameof(TextModelDetail)); Notify(nameof(CanSelectTextModel));
    }
    private void BindChat()
    {
        RestoreTextModel();
        SelectedResult = null; ArtifactPath.Clear(); Notify(nameof(ArtifactOrigin));
        _chatCancellation.Dispose(); _chatCancellation = CancellationTokenSource.CreateLinkedTokenSource(_session.Token);
        var tools = new AgentTools(_store, _commands, _scheduler, _registry, () => Resources.Snapshot, action => _ui.InvokeAsync(action).Task);
        _chat = new(_store, _backend, tools, _arbiter, () => PreferThroughput && !_scheduler.IsPaused && _store.LoadRuns().SelectMany(r => _store.LoadJobs(r.Id)).Any(j => j.State == JobState.Running && _store.LoadRuns().Single(r => r.Id == j.RunId).Graph.Nodes.Single(n => n.Id == j.NodeId).Kind == NodeKind.Image));
        Conversation.Clear(); foreach (var m in _store.LoadConversation()) Conversation.Add(new(m));
        _chat.Message += m => _ui.BeginInvoke(() => { if (!_stopping) { Conversation.Add(new(m)); StreamingReply = ""; RefreshExecution(); } });
        _chat.Token += text => _ui.BeginInvoke(() => { if (!_stopping) StreamingReply += text; });
        _chat.Status += status => _ui.BeginInvoke(() => { if (!_stopping) ChatStatus = status; });
        StreamingReply = ""; ChatStatus = "Ready · Qwen loads on demand";
    }
    private async Task SendChatAsync()
    {
        var input = ChatInput; ChatInput = ""; _chatBusy = true; Notify(nameof(CanSelectTextModel)); CommandManager.InvalidateRequerySuggested();
        if (_chatCancellation.IsCancellationRequested) { _chatCancellation.Dispose(); _chatCancellation = CancellationTokenSource.CreateLinkedTokenSource(_session.Token); }
        var context = DocumentJson.Write(new { SelectedNode = _selectedNodeId, SelectedRun = _activeRun?.Id, SelectedJob = SelectedNodeJob?.Id, SelectedArtifact = SelectedResult?.Artifact.Id, ChosenArtifacts = Results.Where(r => r.IsChosen).Select(r => r.Artifact.Id).ToArray(), Feedback });
        try { await Task.Run(() => _chat.SendAsync(input, _chatCancellation.Token, context)); }
        catch (Exception ex) { ChatStatus = ex.Message; }
        finally { _chatBusy = false; Notify(nameof(CanSelectTextModel)); CommandManager.InvalidateRequerySuggested(); }
    }
    private async Task StopChatAsync() { _chatCancellation.Cancel(); await _chatTask; }
    private async Task UnloadIdleAsync()
    {
        try { using var lease = await _arbiter.AcquireAsync(ResourceKind.Gpu, Guid.NewGuid(), _session.Token); await _runtime.UnloadAsync(); }
        catch (Exception ex) { Status = ex.Message; }
    }
    private void BranchRound()
    {
        var job = SelectedNodeJob ?? throw new InvalidOperationException("Select a completed favourites node.");
        BatchPlan.Count(job.Outputs.Length * int.Parse(RoundCount), 1, BatchPlan.Seeds(RoundSeeds).Length);
        WorkflowCommands.AddImageRound(_commands, "", RoundInstructions, int.Parse(RoundCount), RequiredProfile(), BatchPlan.Dimension(RoundWidth), BatchPlan.Dimension(RoundHeight), RoundSeeds, job.NodeId, Feedback);
        var branch = Graph.Nodes.Last(n => n.Kind == NodeKind.Branch);
        _activeRun = _scheduler.Submit(WorkflowCommands.ExecutionBranch(_commands.Current, branch.Id, job.Outputs), Guid.NewGuid());
        _scheduler.Resume(); RefreshExecution(); SelectNode(Graph.Nodes.Last(n => n.Kind == NodeKind.Select).Id);
        Notify(nameof(QueueAction)); Status = "Next round queued from the saved selected images";
    }
    private void ReadGenerationInspector(GraphNode? node)
    {
        var s = node?.Settings ?? new();
        SelectedInstructions = s.GetValueOrDefault("instructions", ""); SelectedCount = s.GetValueOrDefault("count", "6");
        SelectedProfiles = s.GetValueOrDefault("profiles", "klein4b"); SelectedWidth = s.GetValueOrDefault("width", "512"); SelectedHeight = s.GetValueOrDefault("height", "512");
        SelectedSeeds = s.GetValueOrDefault("seeds", "42"); SelectedNegative = s.GetValueOrDefault("negative", ""); Feedback = node?.Kind == NodeKind.Select ? SelectedNodeJob?.Feedback ?? s.GetValueOrDefault("feedback", "") : s.GetValueOrDefault("feedback", "");
        foreach (var name in new[] { nameof(QwenEditorVisibility), nameof(PreparationVisibility), nameof(FeedbackVisibility), nameof(BatchPreview), nameof(NodeMatrixPreview) }) Notify(name);
    }
    private void WriteGenerationInspector(GraphNode node, Dictionary<string, string> settings)
    {
        if (node.Kind is NodeKind.Variations or NodeKind.GenerateText)
        {
            if (!int.TryParse(SelectedCount, out var count) || count is < 1 or > 12) throw new ArgumentException("Choose 1–12 variations per source.");
            settings["count"] = SelectedCount; settings["instructions"] = SelectedInstructions;
        }
        if (node.Kind == NodeKind.PrepareImage)
        {
            BatchPlan.Dimension(SelectedWidth); BatchPlan.Dimension(SelectedHeight); BatchPlan.Seeds(SelectedSeeds);
            foreach (var id in SelectedProfiles.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                if (_registry.Get(id).Runtime != RuntimeKind.StableDiffusion) throw new ArgumentException("Choose image profiles.");
            settings["profiles"] = SelectedProfiles; settings["width"] = SelectedWidth; settings["height"] = SelectedHeight; settings["seeds"] = SelectedSeeds; settings["negative"] = SelectedNegative;
        }
        if (node.Kind is NodeKind.Branch or NodeKind.Select) settings["feedback"] = Feedback;
    }
    private void RefreshRenderProgress()
    {
        RenderItems.Clear(); if (_activeRun is not null) foreach (var job in Jobs) foreach (var item in _store.LoadRenderItems(job.Record.Id)) RenderItems.Add(item);
        Notify(nameof(BatchPreview));
    }
}

public sealed record ProfileChoice(ModelProfile Profile)
{
    public string Label => Profile.Name + (Profile.Available ? "" : " · Missing files");
    public override string ToString() => Label;
    public string Detail => Profile.Available ? (Profile.Runtime == RuntimeKind.StableDiffusion ? $"{Profile.Steps} steps · CFG {Profile.Cfg}\n" : "") + $"Estimated RAM {Profile.EstimatedRamBytes / 1073741824d:0.#} GiB · VRAM {Profile.EstimatedVramBytes / 1073741824d:0.#} GiB\n{Profile.Notes}" : "Missing: " + string.Join(", ", Profile.MissingFiles);
}

public sealed record ChatItem(ChatMessage Message)
{
    public string Role => Message.Role == "tool" ? "Tool activity" : Message.Role == "user" ? "You" : "Qwen";
    public string Content
    {
        get
        {
            if (Message.Role != "tool") return string.IsNullOrWhiteSpace(Message.Content) && Message.ToolCallsJson is not null ? "Preparing workflow actions…" : Message.Content;
            if (ToolResult.Error(Message.Content) is { } error) return error;
            return Message.Name switch
            {
                "create_image_round" => "Created an image round on the graph.", "submit_graph" => "Queued the saved workflow.",
                "branch_selection" => "Queued a new round from your favourites.", "list_models" => "Read the model library.",
                "get_project" => "Read the project graph.", "get_jobs" => "Read the saved job states.",
                "get_resource_status" => "Read current resource measurements.", "read_artifact" => "Read the saved artifact and its history.",
                "cancel_job" => "Requested cancellation.", _ => "Updated the graph: " + Message.Name
            };
        }
    }
}
