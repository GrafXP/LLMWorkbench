using System.Text.Json.Nodes;
using Workbench.Core;
using Workbench.Infrastructure;

var repository = Path.GetFullPath(args.Length > 0 ? args[0] : "../..");
var directory = args.Length > 1 ? Path.GetFullPath(args[1]) : Path.Combine(repository, "local-data", "phase2-live-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
Directory.CreateDirectory(directory);
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(25));
var ct = timeout.Token;
var registry = new ModelRegistry(Path.Combine(repository, "models.json")); registry.Reload();
await using var runtime = new RuntimeManager();
runtime.Changed += status => Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss} {status.Message}");
runtime.Log += line => { lock (directory) File.AppendAllText(Path.Combine(directory, "runtime.log"), line + Environment.NewLine); };
var backend = new LocalGenerationBackend(registry, runtime); var resources = new ResourceArbiter();
Console.WriteLine("Live evidence: " + directory);
// Verify native streamed calls AND the assistant/tool/result continuation on this exact Prism/Qwen template.
var request = new ChatMessage(Guid.NewGuid(), "user", "Call get_resource_status once, then briefly report its result. Do not call any other tool.", DateTimeOffset.UtcNow);
ChatReply reply;
if (!File.Exists(Path.Combine(directory, "native-tool-contract.json")))
using (await resources.AcquireAsync(ResourceKind.Gpu, Guid.NewGuid(), ct))
{
    reply = await backend.CompleteAsync([request], true, null, ct);
    if (reply.ToolCalls.Length != 1 || reply.ToolCalls[0].Name != "get_resource_status") throw new InvalidOperationException("Native tool-call contract failed: " + DocumentJson.Write(reply));
    var call = reply.ToolCalls[0]; var calls = new JsonArray(new JsonObject { ["id"] = call.Id, ["type"] = "function", ["function"] = new JsonObject { ["name"] = call.Name, ["arguments"] = call.Arguments } });
    var continuation = await backend.CompleteAsync([request,
        new(Guid.NewGuid(), "assistant", reply.Content, DateTimeOffset.UtcNow, ToolCallsJson: calls.ToJsonString()),
        new(Guid.NewGuid(), "tool", "{\"status\":\"available\",\"gpu\":\"GTX 1070 Ti\"}", DateTimeOffset.UtcNow, call.Id, call.Name)], true, null, ct);
    if (continuation.ToolCalls.Length != 0 || string.IsNullOrWhiteSpace(continuation.Content)) throw new InvalidOperationException("Matched native tool-result continuation failed.");
    File.WriteAllText(Path.Combine(directory, "native-tool-contract.json"), DocumentJson.Write(new { reply, continuation }));
}
Console.WriteLine("Native streamed tool call and matched result: passed");
Guid selectionId; Guid[] firstImages; Guid[] favourites;
using (var store = new SqliteProjectStore(Path.Combine(directory, "project")))
{
    var commands = new ProjectCommands(store.LoadProject() ?? ProjectDocument.Create("Phase 2 live verification")); store.SaveProject(commands.Current);
    var scheduler = new JobScheduler(store, new GenerationExecutor(store, registry, backend, resources, runtime.UnloadAsync), resources);
    var tools = new AgentTools(store, commands, scheduler, registry, () => null);
    var chat = new AgentChatService(store, backend, tools, resources); chat.Status += Console.WriteLine;
    if (store.LoadRuns().Length == 0)
        await chat.SendAsync("Create and queue an image round: exactly six variations of a peaceful alpine lake and reflected mountains. Vary the lighting and camera composition while preserving the lake and mountains. Use only klein4b at 512 by 512 and seed 42. Call create_image_round with count 6 and then submit_graph. Stop after submitting; I will choose favourites.", ct);
    var firstRun = store.LoadRuns().LastOrDefault() ?? throw new InvalidOperationException("Chat did not submit the round: " + DocumentJson.Write(store.LoadConversation()));
    if (firstRun.Graph.Nodes.Single(n => n.Kind == NodeKind.Variations).Settings["count"] != "6") throw new InvalidOperationException("Chat created the wrong variation count.");
    if (store.LoadRuns().Length == 1)
    {
        foreach (var job in store.LoadJobs(firstRun.Id).Where(j => j.State is JobState.Failed or JobState.Interrupted or JobState.Cancelled)) scheduler.Retry(job.Id);
        scheduler.Resume(); while (await scheduler.ProcessNextAsync(ct)) { }
    }
    var failed = store.LoadJobs(firstRun.Id).FirstOrDefault(j => j.State is JobState.Failed or JobState.Interrupted);
    if (failed is not null) throw new InvalidOperationException("Round failed: " + failed.Message);
    var selection = store.LoadJobs(firstRun.Id).Single(j => firstRun.Graph.Nodes.Single(n => n.Id == j.NodeId).Kind == NodeKind.Select); selectionId = selection.Id;
    firstImages = store.LoadJobs(firstRun.Id).Single(j => firstRun.Graph.Nodes.Single(n => n.Id == j.NodeId).Kind == NodeKind.Image).Outputs; favourites = firstImages.Take(2).ToArray();
    if (firstImages.Length != 6 || resources.GpuOwner is not null || runtime.ManagedPid is not null) throw new InvalidOperationException("First round count or checkpoint resource release failed.");
    File.WriteAllText(Path.Combine(directory, "round1.json"), DocumentJson.Write(new { Project = commands.Current, Jobs = store.LoadJobs(store.LoadRuns()[0].Id), Artifacts = store.LoadArtifacts() }));
    Console.WriteLine("Six real images rendered; human checkpoint released the model. Reopening project.");
}
using (var store = new SqliteProjectStore(Path.Combine(directory, "project")))
{
    var commands = new ProjectCommands(store.LoadProject()!);
    var scheduler = new JobScheduler(store, new GenerationExecutor(store, registry, backend, resources, runtime.UnloadAsync), resources); scheduler.Resume();
    if (scheduler.Find(selectionId).State == JobState.AwaitingSelection) scheduler.Choose(selectionId, favourites, "Preserve the mountains and reflection; explore more dramatic light");
    while (await scheduler.ProcessNextAsync(ct)) { }
    var tools = new AgentTools(store, commands, scheduler, registry, () => null);
    var result = await tools.DispatchAsync(new("live_next_round", "branch_selection", DocumentJson.Write(new { jobId = selectionId, instructions = "Explore dramatic morning light while preserving the lake, mountains and reflection", count = 3, profiles = "klein4b" })));
    if (JsonNode.Parse(result)?["error"] is { } error) throw new InvalidOperationException(error.ToString());
    foreach (var job in store.LoadJobs(store.LoadRuns()[0].Id).Where(j => j.State is JobState.Failed or JobState.Interrupted or JobState.Cancelled)) scheduler.Retry(job.Id);
    while (await scheduler.ProcessNextAsync(ct)) { }
    var images = store.LoadArtifacts().Where(a => a.Kind == ArtifactKind.Image).ToArray();
    if (images.Length != 12 || !firstImages.All(id => images.Any(a => a.Id == id))) throw new InvalidOperationException("Second round failed: " + DocumentJson.Write(store.LoadJobs(store.LoadRuns()[0].Id)));
    var newest = store.LoadRuns()[0];
    if (!store.LoadJobs(newest.Id).Any(j => j.State == JobState.AwaitingSelection)) throw new InvalidOperationException("Second round did not reach selection.");
    File.WriteAllText(Path.Combine(directory, "result.json"), DocumentJson.Write(new { NativeTools = true, FirstRoundImages = firstImages.Length, SecondRoundImages = images.Length - firstImages.Length, Chosen = favourites,
        Project = commands.Current, Runs = store.LoadRuns(), Artifacts = store.LoadArtifacts(), Conversation = store.LoadConversation(), GpuReleased = resources.GpuOwner is null, RuntimeReleased = runtime.ManagedPid is null }));
}
Console.WriteLine("PASS: chat → six real prompts/images → restart → two favourites → six next-round images; lineage and history saved.");
