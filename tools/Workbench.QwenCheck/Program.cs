using Microsoft.Data.Sqlite;
using Workbench.Core;
using Workbench.Infrastructure;

var repository = Path.GetFullPath(args[0]);
var directory = Path.GetFullPath(args[1]); Directory.CreateDirectory(directory);
var registryPath = Path.Combine(repository, "models.json");
if (args.Contains("--microbatch-one"))
{
    var profiles = DocumentJson.Read<ModelProfile[]>(File.ReadAllText(registryPath));
    registryPath = Path.Combine(directory, "models.json");
    File.WriteAllText(registryPath, DocumentJson.Write(profiles.Select(p => p.Runtime == RuntimeKind.Llama ? p with { Arguments = [.. p.Arguments, "-b", "512", "-ub", "1"] } : p).ToArray()));
}
var registry = new ModelRegistry(registryPath); registry.Reload();
using var collector = new WindowsResourceCollector();
ResourceSnapshot? snapshot = collector.Read();
using var sampling = new CancellationTokenSource();
var samples = new List<ResourceSnapshot>();
var samplingTask = Task.Run(async () =>
{
    try
    {
        while (true)
        {
            snapshot = collector.Read(); lock (samples) samples.Add(snapshot);
            await Task.Delay(500, sampling.Token);
        }
    }
    catch (OperationCanceledException) { }
});
await using var runtime = new RuntimeManager();
runtime.Log += line => { lock (directory) File.AppendAllText(Path.Combine(directory, "runtime.log"), line + Environment.NewLine); };
runtime.Changed += state => Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss} {state.Message}");
var backend = new LocalGenerationBackend(registry, runtime);
var results = new List<object>();
try
{
    foreach (var profile in registry.Profiles.Where(p => p.Runtime == RuntimeKind.Llama && (args.Length < 3 || args[2].StartsWith("--") || p.Id == args[2])))
    {
        await Task.Delay(1500);
        registry.SelectTextModel(profile.Id); lock (samples) samples.Clear();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var ct = timeout.Token;
        var baseline = snapshot?.Gpu?.VramUsed; ulong? loaded = null; string? error = null; string? savedReply = null;
        var started = DateTimeOffset.UtcNow;
        try
        {
            await runtime.EnsureAsync(profile, ct); await Task.Delay(1000, ct); loaded = snapshot?.Gpu?.VramUsed;
            Console.WriteLine($"{profile.Id}: baseline {baseline / 1048576} MiB, loaded {loaded / 1048576} MiB");
            using var store = new SqliteProjectStore(Path.Combine(directory, profile.Id));
            var commands = new ProjectCommands(ProjectDocument.Create("Qwen diagnostics")); store.SaveProject(commands.Current);
            var resources = new ResourceArbiter(); var scheduler = new JobScheduler(store, new GenerationExecutor(store, registry, backend, resources), resources);
            var chat = new AgentChatService(store, backend, new AgentTools(store, commands, scheduler, registry, () => snapshot), resources);
            chat.Status += Console.WriteLine;
            await chat.SendAsync("Call list_models once and get_jobs once, then briefly describe the available text models and saved jobs. Do not call any other tool.", ct);
            var history = store.LoadConversation();
            if (!history.Any(m => m.Name == "list_models" && m.Role == "tool") || !history.Any(m => m.Name == "get_jobs" && m.Role == "tool") || history.Last().Content.StartsWith("Chat stopped:") || history.Last().Content.StartsWith("Chat cancelled."))
                throw new InvalidOperationException("Array tool continuation failed: " + history.Last().Content);
            Console.WriteLine(profile.Id + ": array tools and continuation passed");
            var variations = await backend.VariationsAsync("Golden morning light pierces through the clouds, casting long, dramatic shadows across the jagged peaks mirrored with striking clarity in the serene alpine lake.", "Explore dramatic morning light while preserving the lake, mountains and reflection", 3, ct);
            if (variations.Length != 3) throw new InvalidOperationException("Variation count failed.");
            Console.WriteLine(profile.Id + ": constrained variation JSON passed");
            var userDatabase = Path.Combine(repository, "..", "outputs", "workbench-v2", "projects", "default", "project.sqlite");
            if (File.Exists(userDatabase))
            {
                using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = userDatabase, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()); db.Open();
                using var command = db.CreateCommand(); command.CommandText = "SELECT json FROM conversation ORDER BY rowid";
                using var reader = command.ExecuteReader(); var messages = new List<ChatMessage>();
                while (reader.Read()) messages.Add(DocumentJson.Read<ChatMessage>(reader.GetString(0)));
                // Reproduce the latest user request without executing any returned actions or altering their project.
                var lastUser = messages.FindLastIndex(m => m.Role == "user");
                if (lastUser >= 0)
                {
                    var reply = await backend.CompleteAsync([new(Guid.NewGuid(), "system", "You are the local visual workflow assistant. Use the available tools when requested.", DateTimeOffset.UtcNow), .. messages.Take(lastUser + 1)], true, null, ct);
                    savedReply = DocumentJson.Write(reply); Console.WriteLine(profile.Id + ": saved chat request passed");
                }
            }
        }
        catch (Exception ex) { error = ex.ToString(); Console.WriteLine(profile.Id + ": FAIL: " + ex.Message[..Math.Min(350, ex.Message.Length)]); }
        finally { await runtime.UnloadAsync(); }
        ResourceSnapshot[] measured; lock (samples) measured = samples.ToArray();
        results.Add(new { Profile = profile, StartedUtc = started, FinishedUtc = DateTimeOffset.UtcNow, BaselineVram = baseline, LoadedVram = loaded, PeakVram = measured.Max(s => s.Gpu?.VramUsed), Error = error, SavedChatReply = savedReply, RuntimeReleased = runtime.ManagedPid is null });
        File.WriteAllText(Path.Combine(directory, "result.json"), DocumentJson.Write(results));
    }
}
finally { sampling.Cancel(); await samplingTask; }
Console.WriteLine("Report: " + directory);
if (results.Any(r => System.Text.Json.JsonSerializer.SerializeToNode(r)?["Error"] is not null)) Environment.ExitCode = 1;
