using System.IO.Compression;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using Workbench.Core;
using Workbench.Infrastructure;
using Xunit;

namespace Workbench.Tests;

public sealed class ImageLoopTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "workbench-phase2", Guid.NewGuid().ToString("N"));
    private readonly ModelRegistry _registry;
    private readonly FakeBackend _backend = new();
    public ImageLoopTests()
    {
        Directory.CreateDirectory(_root); var modelFile = Path.Combine(_root, "model.bin"); File.WriteAllText(modelFile, "test model identity");
        var profile = new ModelProfile("test", "Test images", RuntimeKind.StableDiffusion, modelFile, new() { ["--model"] = modelFile }, []);
        var path = Path.Combine(_root, "models.json"); File.WriteAllText(path, DocumentJson.Write(new[] { profile, profile with { Id = "text-test", Runtime = RuntimeKind.Llama } }));
        _registry = new(path); _registry.Reload();
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private ProjectCommands Round(int count = 6, string seeds = "42")
    {
        var commands = new ProjectCommands(ProjectDocument.Create());
        WorkflowCommands.AddImageRound(commands, "A lake", "Vary light; preserve the lake", count, "test", 256, 256, seeds); return commands;
    }
    private JobScheduler Scheduler(SqliteProjectStore store, ResourceArbiter? resources = null)
    {
        var arbiter = resources ?? new(); var scheduler = new JobScheduler(store, new GenerationExecutor(store, _registry, _backend, arbiter), arbiter); scheduler.Resume(); return scheduler;
    }
    private static async Task Drain(JobScheduler scheduler) { while (await scheduler.ProcessNextAsync()) { } }

    [Fact]
    public async Task EditedVariationsSurviveRestartAndFeedDownstreamWithoutRegeneratingTheirSiblings()
    {
        var commands = Round(2, "42,43"); var path = Path.Combine(_root, "manual-texts");
        Guid originalId, editedId, siblingId, oldRun;
        using (var store = new SqliteProjectStore(path))
        {
            store.SaveProject(commands.Current); var scheduler = Scheduler(store);
            oldRun = scheduler.Submit(commands.Current, Guid.NewGuid()).Id; await Drain(scheduler);
            var node = commands.Current.Graph.Nodes.Single(n => n.Kind == NodeKind.Variations);
            var originals = store.LoadJobs(oldRun).Single(j => j.NodeId == node.Id).Outputs.Select(id => store.LoadArtifacts().Single(a => a.Id == id)).ToArray();
            originalId = originals[0].Id; siblingId = originals[1].Id;
            var edit = store.SaveTextRevision(originalId, "A manually edited lake\nwith an exact human instruction"); editedId = edit.Id;
            commands.Update(ManualOutputs.Pin(node, [edit, originals[1]])); store.SaveProject(commands.Current);
            Assert.Equal("A lake · variant 1", store.LoadArtifacts().Single(a => a.Id == originalId).Content);
            Assert.Contains(originalId, edit.Parents); Assert.True(File.Exists(Path.Combine(path, edit.RelativePath!)));
        }
        using (var store = new SqliteProjectStore(path))
        {
            var project = store.LoadProject()!; var node = project.Graph.Nodes.Single(n => n.Kind == NodeKind.Variations);
            var scheduler = Scheduler(store); var calls = _backend.VariationCalls;
            var run = scheduler.Submit(ManualOutputs.Downstream(project, node.Id), Guid.NewGuid()); await Drain(scheduler);
            Assert.Equal(project.Graph.Id, run.Graph.SourceRevisionId);
            Assert.DoesNotContain(run.Graph.Nodes, n => n.Kind is NodeKind.Text or NodeKind.Collect);
            Assert.Equal(new[] { editedId, siblingId }, store.LoadJobs(run.Id).Single(j => j.NodeId == node.Id).Outputs);
            Assert.Equal(calls + 2, _backend.VariationCalls); // Only preparation runs, once per saved variation.
            Assert.Equal(2, _backend.RenderedPrompts.Count(p => p.StartsWith("A manually edited lake", StringComparison.Ordinal)));
            Assert.Contains(originalId, store.LoadJobs(oldRun).Single(j => j.NodeId == node.Id).Outputs);
            var full = scheduler.Submit(project, Guid.NewGuid()); await Drain(scheduler);
            Assert.Equal(new[] { editedId, siblingId }, store.LoadJobs(full.Id).Single(j => j.NodeId == node.Id).Outputs);
        }
    }

    [Fact]
    public async Task EditedPreparedPromptPreservesSettingsAndIsRenderedExactlyThroughRetryAndBranching()
    {
        var commands = Round(2); using var store = new SqliteProjectStore(Path.Combine(_root, "manual-prompts"));
        store.SaveProject(commands.Current); var scheduler = Scheduler(store);
        var oldRun = scheduler.Submit(commands.Current, Guid.NewGuid()); await Drain(scheduler);
        var node = commands.Current.Graph.Nodes.Single(n => n.Kind == NodeKind.PrepareImage);
        var originals = store.LoadJobs(oldRun.Id).Single(j => j.NodeId == node.Id).Outputs.Select(id => store.LoadArtifacts().Single(a => a.Id == id)).ToArray();
        var before = DocumentJson.Read<ImageSpecification>(originals[0].Content);
        const string exact = "HUMAN PROMPT\nkeep these exact words";
        var edit = store.SaveTextRevision(originals[0].Id, exact);
        Assert.Equal(DocumentJson.Write(before with { Prompt = exact }), edit.Content);
        Assert.Equal(before.Prompt, DocumentJson.Read<ImageSpecification>(store.LoadArtifacts().Single(a => a.Id == originals[0].Id).Content).Prompt);
        commands.Update(ManualOutputs.Pin(node, [edit, originals[1]])); store.SaveProject(commands.Current);
        var calls = _backend.VariationCalls; _backend.FailRender = _backend.Renders + 2;
        var run = scheduler.Submit(ManualOutputs.Downstream(commands.Current, node.Id), Guid.NewGuid()); await Drain(scheduler);
        var failed = store.LoadJobs(run.Id).Single(j => j.State == JobState.Failed);
        Assert.Equal(exact, _backend.RenderedPrompts[^2]); Assert.Equal(calls, _backend.VariationCalls);
        scheduler.Retry(failed.Id); await Drain(scheduler);
        Assert.Equal(2, store.LoadRenderItems(failed.Id).Count(i => i.State == JobState.Done));
        Assert.Single(store.LoadRenderItems(failed.Id), i => i.Specification.Prompt == exact);
        var selection = store.LoadJobs(run.Id).Single(j => j.State == JobState.AwaitingSelection);
        var image = store.LoadArtifacts().Single(a => a.Kind == ArtifactKind.Image && a.Parents.Contains(edit.Id));
        scheduler.Choose(selection.Id, [image.Id]); await Drain(scheduler);
        WorkflowCommands.AddImageRound(commands, "", "Keep subject", 1, "test", 256, 256, "7", selection.NodeId);
        var branch = commands.Current.Graph.Nodes.Last(n => n.Kind == NodeKind.Branch);
        var next = scheduler.Submit(WorkflowCommands.ExecutionBranch(commands.Current, branch.Id, [image.Id]), Guid.NewGuid()); await Drain(scheduler);
        Assert.DoesNotContain(store.LoadJobs(next.Id), j => j.State == JobState.Failed);
        Assert.Equal(ArtifactKind.Text, store.LoadArtifacts().Single(a => a.Id == store.LoadJobs(next.Id).Single(j => j.NodeId == branch.Id).Outputs[0]).Kind);
    }

    [Fact]
    public void DownstreamTextRunKeepsJoinDependenciesAndRejectsInvalidSavedOutputs()
    {
        var brief = GraphNode.Create(NodeKind.Text); var other = GraphNode.Create(NodeKind.Text);
        var generated = GraphNode.Create(NodeKind.GenerateText); var join = GraphNode.Create(NodeKind.Collect);
        var artifact = new Artifact(Guid.NewGuid(), ArtifactKind.Text, "Human words", null, [], generated.Id, DateTimeOffset.UtcNow);
        generated = ManualOutputs.Pin(generated, [artifact]);
        var project = ProjectDocument.Create() with { Graph = new(Guid.NewGuid(), [brief, generated, other, join],
            [new(Guid.NewGuid(), brief.Id, "text", generated.Id, "source"), new(Guid.NewGuid(), generated.Id, "text", join.Id, "a"), new(Guid.NewGuid(), other.Id, "text", join.Id, "b")]) };
        var downstream = ManualOutputs.Downstream(project, generated.Id); GraphValidator.Validate(downstream.Graph, true);
        Assert.DoesNotContain(downstream.Graph.Nodes, n => n.Id == brief.Id);
        Assert.Contains(downstream.Graph.Nodes, n => n.Id == other.Id);
        Assert.Throws<InvalidOperationException>(() => ManualOutputs.Pin(generated, [artifact with { SourceNode = other.Id }]));
        Assert.Throws<InvalidOperationException>(() => ManualOutputs.Pin(generated, [artifact with { Kind = ArtifactKind.ImageSpecification }]));
        Assert.Throws<InvalidOperationException>(() => ManualOutputs.Pin(generated, [artifact, artifact]));
    }

    [Fact]
    public async Task SixImagesSelectionAndNextRoundRetainLineageAcrossRestart()
    {
        var commands = Round(); Guid choiceId; Guid[] favourites; Guid[] oldImages;
        using (var store = new SqliteProjectStore(Path.Combine(_root, "project")))
        {
            store.SaveProject(commands.Current); var scheduler = Scheduler(store); var run = scheduler.Submit(commands.Current, Guid.NewGuid()); await Drain(scheduler);
            var choice = store.LoadJobs(run.Id).Single(j => j.State == JobState.AwaitingSelection); choiceId = choice.Id;
            oldImages = store.LoadArtifacts().Where(a => a.Kind == ArtifactKind.Image).Select(a => a.Id).ToArray(); favourites = oldImages.Take(2).ToArray();
            Assert.Equal(6, oldImages.Length); Assert.Equal(6, _backend.Renders);
            Assert.All(store.LoadArtifacts().Where(a => a.Kind == ArtifactKind.Image), a => Assert.True(File.Exists(Path.Combine(store.DirectoryPath, a.RelativePath!))));
        }
        using (var store = new SqliteProjectStore(Path.Combine(_root, "project")))
        {
            var scheduler = Scheduler(store); scheduler.Choose(choiceId, favourites, "Keep the reflected mountains"); await Drain(scheduler);
            var selected = scheduler.Find(choiceId); Assert.Equal(favourites, selected.Outputs); Assert.Equal("Keep the reflected mountains", selected.Feedback);
            WorkflowCommands.AddImageRound(commands, "", "Vary the camera", 3, "test", 256, 256, "123", selected.NodeId, selected.Feedback!); store.SaveProject(commands.Current);
            var branch = commands.Current.Graph.Nodes.Last(n => n.Kind == NodeKind.Branch);
            var next = scheduler.Submit(WorkflowCommands.ExecutionBranch(commands.Current, branch.Id, favourites), Guid.NewGuid()); await Drain(scheduler);
            Assert.Equal(commands.Current.Graph.Id, next.Graph.SourceRevisionId);
            Assert.Equal(12, _backend.Renders); Assert.Single(store.LoadJobs(next.Id), j => j.State == JobState.AwaitingSelection);
            Assert.All(oldImages, id => Assert.Contains(store.LoadArtifacts(), a => a.Id == id));
            var sourceTexts = store.LoadArtifacts().Where(a => a.SourceNode == branch.Id).ToArray(); Assert.Equal(2, sourceTexts.Length);
            Assert.All(sourceTexts, a => Assert.Contains(a.Parents, id => favourites.Contains(id)));
            Assert.All(sourceTexts, a => Assert.Equal(selected.Feedback, a.Metadata!["feedback"]));
        }
        using (var store = new SqliteProjectStore(Path.Combine(_root, "project"))) Assert.Equal(12, store.LoadArtifacts().Count(a => a.Kind == ArtifactKind.Image));
    }
    [Fact]
    public async Task FailedBatchRetriesOnlyUnfinishedImagesWithTheSameResolvedSeeds()
    {
        var commands = Round(3, "-1,99"); _backend.FailRender = 3; Guid batchId; RenderItem[] before;
        using (var store = new SqliteProjectStore(Path.Combine(_root, "project")))
        {
            var scheduler = Scheduler(store); var run = scheduler.Submit(commands.Current, Guid.NewGuid()); await Drain(scheduler);
            batchId = store.LoadJobs(run.Id).Single(j => j.State == JobState.Failed).Id; before = store.LoadRenderItems(batchId);
            Assert.Equal(2, before.Count(i => i.State == JobState.Done)); Assert.Equal(6, before.Length);
        }
        using (var store = new SqliteProjectStore(Path.Combine(_root, "project")))
        {
            var resources = new ResourceArbiter(); var scheduler = Scheduler(store, resources); scheduler.Retry(batchId); await Drain(scheduler);
            var after = store.LoadRenderItems(batchId); Assert.Equal(JobState.Done, scheduler.Find(batchId).State);
            Assert.Equal(before.Select(i => i.Seed), after.Select(i => i.Seed)); Assert.Equal(before.Take(2).Select(i => i.ArtifactId), after.Take(2).Select(i => i.ArtifactId));
            Assert.Equal(7, _backend.Renders); Assert.Equal(6, store.LoadArtifacts().Count(a => a.Kind == ArtifactKind.Image)); Assert.Null(resources.GpuOwner);
        }
    }
    [Fact]
    public async Task CancellingAnImageBatchKeepsPublishedItemsAndReleasesItsOwnLease()
    {
        using var store = new SqliteProjectStore(Path.Combine(_root, "project")); var commands = Round(3); var arbiter = new ResourceArbiter(); var scheduler = Scheduler(store, arbiter);
        _backend.BlockRender = 3; var run = scheduler.Submit(commands.Current, Guid.NewGuid());
        await scheduler.ProcessNextAsync(); await scheduler.ProcessNextAsync(); await scheduler.ProcessNextAsync(); await scheduler.ProcessNextAsync();
        var rendering = scheduler.ProcessNextAsync(); await _backend.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var batch = store.LoadJobs(run.Id).Single(j => j.State == JobState.Running); Assert.Equal(batch.Id, arbiter.GpuOwner);
        scheduler.Cancel(batch.Id); await rendering;
        Assert.Null(arbiter.GpuOwner); Assert.Equal(JobState.Cancelled, scheduler.Find(batch.Id).State); Assert.Equal(2, store.LoadRenderItems(batch.Id).Count(i => i.State == JobState.Done));
        scheduler.Retry(batch.Id); await Drain(scheduler); Assert.Equal(3, store.LoadArtifacts().Count(a => a.Kind == ArtifactKind.Image)); Assert.Equal(4, _backend.Renders);
    }
    [Fact]
    public async Task PausingAtAnImageBoundaryDoesNotHoldGpuWhileWaiting()
    {
        using var store = new SqliteProjectStore(Path.Combine(_root, "project")); var commands = Round(2); var arbiter = new ResourceArbiter();
        JobScheduler? scheduler = null; var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new GenerationExecutor(store, _registry, _backend, arbiter, beforeImage: async ct =>
        {
            if (_backend.Renders == 1) { scheduler!.Pause(); reached.TrySetResult(); }
            await scheduler!.WaitAtBoundaryAsync(ct);
        });
        scheduler = new(store, executor, arbiter); scheduler.Resume(); scheduler.Submit(commands.Current, Guid.NewGuid());
        await scheduler.ProcessNextAsync(); await scheduler.ProcessNextAsync(); await scheduler.ProcessNextAsync(); await scheduler.ProcessNextAsync();
        var rendering = scheduler.ProcessNextAsync(); await reached.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Null(arbiter.GpuOwner);
        using (await arbiter.AcquireAsync(ResourceKind.Gpu, Guid.NewGuid(), default)) { Assert.Equal(1, _backend.Renders); }
        scheduler.Resume(); await rendering; Assert.Equal(2, _backend.Renders);
    }
    [Fact]
    public async Task AgentToolDeliveryIsIdempotentAndInvalidEditsLeaveTheGraphIntact()
    {
        using var store = new SqliteProjectStore(Path.Combine(_root, "project")); var commands = new ProjectCommands(ProjectDocument.Create()); store.SaveProject(commands.Current);
        var scheduler = Scheduler(store); var tools = new AgentTools(store, commands, scheduler, _registry, () => null);
        var call = new AgentToolCall("call_create", "create_image_round", """{"brief":"A lake","instructions":"Vary light","count":6,"profiles":"test","width":256,"height":256}""");
        var first = await tools.DispatchAsync(call); var second = await tools.DispatchAsync(call); Assert.Equal(first, second); Assert.Equal(6, commands.Current.Graph.Nodes.Length);
        var invalid = await tools.DispatchAsync(new("call_bad", "connect_nodes", DocumentJson.Write(new { source = commands.Current.Graph.Nodes[0].Id, output = "text", target = commands.Current.Graph.Nodes[^1].Id, input = "items" })));
        Assert.NotNull(JsonNode.Parse(invalid)?["error"]); Assert.Equal(5, commands.Current.Graph.Edges.Length);
        var submit = new AgentToolCall("call_submit", "submit_graph", "{}"); await tools.DispatchAsync(submit); await tools.DispatchAsync(submit); Assert.Single(store.LoadRuns());
        Assert.True(commands.CanUndo); commands.Undo(); Assert.Empty(commands.Current.Graph.Nodes);
    }
    [Fact]
    public async Task SchemaOneProjectsMigrateWithoutLosingPublishedTexts()
    {
        var path = Path.Combine(_root, "project"); Guid artifactId;
        using (var store = new SqliteProjectStore(path))
        {
            var text = GraphNode.Create(NodeKind.Text); text.Settings["text"] = "A phase-one draft";
            var project = ProjectDocument.Create() with { Graph = new(Guid.NewGuid(), [text], []) }; store.SaveProject(project);
            var scheduler = new JobScheduler(store, new BuiltinExecutor(), new()); scheduler.Resume(); scheduler.Submit(project, Guid.NewGuid()); await Drain(scheduler);
            artifactId = Assert.Single(store.LoadArtifacts()).Id;
        }
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = Path.Combine(path, "project.sqlite"), Pooling = false }.ToString()))
        {
            db.Open(); using var command = db.CreateCommand();
            command.CommandText = "DROP TABLE render_items; DROP TABLE conversation; DROP TABLE tool_receipts; PRAGMA user_version=1;"; command.ExecuteNonQuery();
        }
        using (var migrated = new SqliteProjectStore(path))
        {
            var artifact = Assert.Single(migrated.LoadArtifacts()); Assert.Equal(artifactId, artifact.Id); Assert.Equal("A phase-one draft", artifact.Content);
            Assert.True(File.Exists(Path.Combine(path, artifact.RelativePath!))); Assert.Empty(migrated.LoadConversation());
            Assert.Equal(JobState.Done, Assert.Single(migrated.LoadJobs(Assert.Single(migrated.LoadRuns()).Id)).State);
        }
    }
    [Fact]
    public async Task MalformedAgentCallsStopBeforeAFourthMutationAndRepairMatchedResults()
    {
        using var store = new SqliteProjectStore(Path.Combine(_root, "project")); var commands = Round(1); store.SaveProject(commands.Current);
        var resources = new ResourceArbiter(); var scheduler = Scheduler(store, resources);
        var service = new AgentChatService(store, new BadToolBackend(), new AgentTools(store, commands, scheduler, _registry, () => null), resources);
        await service.SendAsync("Render the graph", default);
        Assert.Empty(store.LoadRuns()); Assert.Null(resources.GpuOwner);
        var history = store.LoadConversation(); Assert.Equal(4, history.Count(m => m.Role == "tool"));
        Assert.Contains(history, m => m.ToolCallId == "fourth" && m.Content.Contains("Interrupted before execution", StringComparison.Ordinal));
        Assert.Contains(history, m => m.Role == "assistant" && m.Content.Contains("three invalid tool calls", StringComparison.Ordinal));
    }
    [Fact]
    public void StreamedToolArgumentsAreAssembledByIndexAndTruncationIsRejected()
    {
        var stream = new ChatStreamAssembler();
        stream.Add("""{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_a","function":{"name":"create_","arguments":"{\"count\":"}}]}}]}""");
        stream.Add("""{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"name":"image_round","arguments":"6}"}}]},"finish_reason":"tool_calls"}]}""");
        var call = Assert.Single(stream.Finish().ToolCalls); Assert.Equal("create_image_round", call.Name); Assert.Equal("{\"count\":6}", call.Arguments);
        var truncated = new ChatStreamAssembler(); truncated.Add("""{"choices":[{"delta":{"content":"unfinished"},"finish_reason":"length"}]}"""); Assert.Throws<InvalidDataException>(() => truncated.Finish());
    }
    [Fact]
    public async Task AgentSubmitsWorkAndReleasesGpuBeforeGenerationBegins()
    {
        using var store = new SqliteProjectStore(Path.Combine(_root, "project")); var commands = Round(1); store.SaveProject(commands.Current);
        var arbiter = new ResourceArbiter(); var scheduler = Scheduler(store, arbiter); var tools = new AgentTools(store, commands, scheduler, _registry, () => null);
        var chat = new AgentChatService(store, new ToolBackend(arbiter), tools, arbiter);
        await chat.SendAsync("Render the graph", default); Assert.Null(arbiter.GpuOwner); Assert.Single(store.LoadRuns());
        await Drain(scheduler); Assert.Equal(1, _backend.Renders);
        var messages = store.LoadConversation(); Assert.Contains(messages, m => m.Role == "tool" && m.ToolCallId == "submit_1");
    }
    private sealed class ToolBackend(ResourceArbiter resources) : IChatBackend
    {
        private int _turn;
        public Task<ChatReply> CompleteAsync(ChatMessage[] messages, bool tools, Action<string>? token, CancellationToken ct)
        {
            Assert.NotNull(resources.GpuOwner);
            return Task.FromResult(++_turn == 1 ? new ChatReply("", [new("submit_1", "submit_graph", "{}")]) : new ChatReply("Queued", []));
        }
    }
    [Fact]
    public async Task ArrayToolResultsContinueTheAgentTurnAndTextChoiceSurvivesReopening()
    {
        var second = _registry.Qwen with { Id = "second-text", Name = "Second Qwen", UseActionEnvelope = true };
        File.WriteAllText(_registry.Path, DocumentJson.Write(_registry.Profiles.Append(second).ToArray())); _registry.Reload();
        _registry.SelectTextModel(second.Id); Assert.Equal(second.Id, _registry.Qwen.Id);
        await using var runtime = new RuntimeManager(); var local = new LocalGenerationBackend(_registry, runtime);
        Assert.True(local.UseActionEnvelope);
        _registry.SelectTextModel("text-test"); Assert.False(local.UseActionEnvelope);
        Assert.Throws<ArgumentException>(() => _registry.SelectTextModel("test"));
        var projectPath = Path.Combine(_root, "array-project");
        using (var store = new SqliteProjectStore(projectPath))
        {
            store.SavePreference("textModelId", second.Id);
            var commands = Round(1); store.SaveProject(commands.Current); var resources = new ResourceArbiter();
            var service = new AgentChatService(store, new ArrayToolBackend(), new AgentTools(store, commands, Scheduler(store, resources), _registry, () => null), resources);
            await service.SendAsync("List models and job states", default);
            Assert.Equal("Model list read", store.LoadConversation().Last().Content); Assert.Null(resources.GpuOwner);
            Assert.All(store.LoadConversation().Where(m => m.Role == "tool"), m => Assert.Null(ToolResult.Error(m.Content)));
            Assert.Equal("failure", ToolResult.Error("{\"error\":\"failure\"}"));
        }
        using (var reopened = new SqliteProjectStore(projectPath))
        {
            _registry.SelectTextModel(reopened.LoadPreference("textModelId")!); Assert.Equal(second.Id, _registry.Qwen.Id);
        }
    }
    private sealed class ArrayToolBackend : IChatBackend
    {
        private int _turn;
        public Task<ChatReply> CompleteAsync(ChatMessage[] messages, bool tools, Action<string>? token, CancellationToken ct)
        {
            if (++_turn == 1) return Task.FromResult(new ChatReply("", [new("models_array", "list_models", "{}"), new("jobs_array", "get_jobs", "{}")]));
            Assert.Equal(2, messages.Count(m => m.Role == "tool"));
            return Task.FromResult(new ChatReply("Model list read", []));
        }
    }
    private sealed class BadToolBackend : IChatBackend
    {
        public Task<ChatReply> CompleteAsync(ChatMessage[] messages, bool tools, Action<string>? token, CancellationToken ct) => Task.FromResult(new ChatReply("",
            [new("first", "nonexistent", "{}"), new("second", "nonexistent", "{}"), new("third", "nonexistent", "{}"), new("fourth", "submit_graph", "{}")]));
    }
    private sealed class FakeBackend : IGenerationBackend
    {
        public int Renders; public int FailRender; public int BlockRender;
        public int VariationCalls;
        public List<string> RenderedPrompts { get; } = [];
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ChatReply> CompleteAsync(ChatMessage[] messages, bool tools, Action<string>? token, CancellationToken ct) => Task.FromResult(new ChatReply("test reply", []));
        public Task<string[]> VariationsAsync(string source, string instructions, int count, CancellationToken ct)
        { VariationCalls++; return Task.FromResult(Enumerable.Range(1, count).Select(i => source + " · variant " + i).ToArray()); }
        public async Task<byte[]> RenderAsync(ImageSpecification specification, long seed, Action<string>? progress, CancellationToken ct)
        {
            RenderedPrompts.Add(specification.Prompt);
            if (++Renders == FailRender) throw new IOException("Transient test failure");
            if (Renders == BlockRender) { Blocked.SetResult(); await Task.Delay(Timeout.Infinite, ct); }
            return TestPng(specification.Width, specification.Height);
        }
    }
    private static byte[] TestPng(int width, int height)
    {
        using var output = new MemoryStream(); output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        void Chunk(string name, byte[] data)
        {
            Span<byte> number = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(number, data.Length); output.Write(number);
            var bytes = Encoding.ASCII.GetBytes(name).Concat(data).ToArray(); output.Write(bytes); uint crc = uint.MaxValue;
            foreach (var b in bytes) { crc ^= b; for (var i = 0; i < 8; i++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320u : crc >> 1; }
            BinaryPrimitives.WriteUInt32BigEndian(number, ~crc); output.Write(number);
        }
        var header = new byte[13]; BinaryPrimitives.WriteInt32BigEndian(header, width); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height); header[8] = 8; header[9] = 2; Chunk("IHDR", header);
        using var compressed = new MemoryStream(); using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, true)) zlib.Write(new byte[(width * 3 + 1) * height]);
        Chunk("IDAT", compressed.ToArray()); Chunk("IEND", []); return output.ToArray();
    }
}
