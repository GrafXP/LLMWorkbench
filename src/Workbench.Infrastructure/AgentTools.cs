using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
using Workbench.Core;

namespace Workbench.Infrastructure;

public sealed class AgentTools(SqliteProjectStore store, ProjectCommands commands, JobScheduler scheduler, ModelRegistry registry,
    Func<ResourceSnapshot?> resourceStatus, Func<Func<string>, Task<string>>? onCommandThread = null)
{
    public static JsonArray Schemas()
    {
        var tools = new JsonArray();
        void Add(string name, string description, string properties = "{}", params string[] required) => tools.Add(new JsonObject
        {
            ["type"] = "function", ["function"] = new JsonObject { ["name"] = name, ["description"] = description,
                ["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = JsonNode.Parse(properties), ["required"] = new JsonArray(required.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()), ["additionalProperties"] = false } }
        });
        Add("get_project", "Read graph nodes, IDs, ports, settings and current revision.");
        Add("list_models", "List available profiles, missing files, prompt guides and defaults.");
        Add("create_image_round", "Append an undoable Qwen variations → prepared prompts → image batch → human selection round. Does not run it. Count is variations per source.",
            """{"brief":{"type":"string"},"instructions":{"type":"string"},"count":{"type":"integer","minimum":1,"maximum":12},"profiles":{"type":"string","description":"Comma separated image profile IDs"},"width":{"type":"integer"},"height":{"type":"integer"},"seeds":{"type":"string","description":"Comma separated integers; -1 is resolved once to a random seed"}}""", "brief", "instructions", "count", "profiles");
        Add("submit_graph", "Queue the graph's saved revision. Returns run ID immediately; rendering happens after this chat turn releases the GPU.");
        Add("get_jobs", "Read saved run/job states and individual image progress. Never wait for a job within a chat turn.");
        Add("get_resource_status", "Read current measured system resources; missing readings remain unavailable.");
        Add("read_artifact", "Read one saved artifact and its lineage. Image content is its prompt; this text-only agent cannot judge the pixels.", """{"id":{"type":"string"}}""", "id");
        Add("add_node", "Add a typed node without running it. Supported kinds: Text, Collect, Select, GenerateText, Variations, PrepareImage, Image, Branch.",
            """{"kind":{"type":"string"},"title":{"type":"string"},"text":{"type":"string"},"type":{"type":"string","enum":["Text","Image"]}}""", "kind");
        Add("configure_node", "Update title and settings through the shared undoable command service. Settings cannot change node kind or port types.",
            """{"id":{"type":"string"},"title":{"type":"string"},"settings":{"type":"object","additionalProperties":{"type":"string"}}}""", "id");
        Add("connect_nodes", "Connect compatible ports in the DAG. Cycles and incompatible types/cardinalities are rejected.",
            """{"source":{"type":"string"},"output":{"type":"string"},"target":{"type":"string"},"input":{"type":"string"}}""", "source", "output", "target", "input");
        Add("branch_selection", "Append and submit the next round from a completed human selection job. Reuses chosen image identities, never rerenders the earlier round.",
            """{"jobId":{"type":"string"},"instructions":{"type":"string"},"count":{"type":"integer","minimum":1,"maximum":12},"profiles":{"type":"string"},"feedback":{"type":"string"}}""", "jobId", "instructions", "count", "profiles");
        Add("cancel_job", "Cancel one queued or active workbench job.", """{"id":{"type":"string"}}""", "id");
        return tools;
    }
    public Task<string> DispatchAsync(AgentToolCall call) => onCommandThread is null ? Task.FromResult(Dispatch(call)) : onCommandThread(() => Dispatch(call));
    private string Dispatch(AgentToolCall call)
    {
        var receiptKey = call.Id;
        if (store.ToolReceipt(receiptKey) is { } previous) return previous;
        try
        {
            var schema = Schemas().SingleOrDefault(t => t?["function"]?["name"]?.GetValue<string>() == call.Name)?["function"]?["parameters"];
            if (schema is null) throw new ArgumentException($"Unknown tool: {call.Name}");
            var args = JsonNode.Parse(call.Arguments)?.AsObject() ?? throw new ArgumentException("Arguments must be a JSON object.");
            var properties = schema["properties"]!.AsObject();
            if (args.Any(kv => !properties.ContainsKey(kv.Key))) throw new ArgumentException("Unexpected tool argument.");
            foreach (var key in schema["required"]!.AsArray().Select(v => v!.GetValue<string>())) if (args[key] is null) throw new ArgumentException($"Missing argument: {key}");
            string S(string key, string fallback = "") => args[key]?.GetValue<string>() ?? fallback;
            int N(string key, int fallback) => args[key]?.GetValue<int>() ?? fallback;
            Guid Id(string key) => Guid.Parse(S(key));
            object result;
            switch (call.Name)
            {
                case "get_project": result = commands.Current; break;
                case "list_models": result = registry.Profiles.Select(p => new { p.Id, p.Name, p.Runtime, p.Available, p.MissingFiles, p.Steps, p.Cfg, p.PromptGuide, p.Notes }); break;
                case "get_jobs": result = store.LoadRuns().Take(5).Select(r => new { r.Id, Jobs = store.LoadJobs(r.Id).Select(j => new { Job = j, Images = store.LoadRenderItems(j.Id).Select(i => new { i.Id, i.State, i.Seed, i.ArtifactId, i.Attempt, i.Message }) }) }); break;
                case "get_resource_status": result = (object?)resourceStatus() ?? new { message = "Telemetry unavailable" }; break;
                case "read_artifact": result = store.LoadArtifacts().Single(a => a.Id == Id("id")); break;
                case "create_image_round":
                    ValidateProfiles(S("profiles"));
                    result = new { selectionNode = WorkflowCommands.AddImageRound(commands, S("brief"), S("instructions"), N("count", 6), S("profiles"), N("width", 512), N("height", 512), S("seeds", "42")), state = "Draft; call submit_graph to execute" }; break;
                case "submit_graph":
                    var run = scheduler.Submit(commands.Current, CommandId(call.Id)); scheduler.Resume(); result = new { runId = run.Id, state = "Queued; GPU passes to generation when this turn ends" }; break;
                case "add_node":
                    if (!Enum.TryParse<NodeKind>(S("kind"), out var kind) || kind is NodeKind.Speech or NodeKind.Music || !Enum.IsDefined(kind)) throw new ArgumentException("Unsupported node kind.");
                    var type = S("type", "Text") == "Image" ? ArtifactKind.Image : ArtifactKind.Text;
                    var node = GraphNode.Create(kind, 60 + commands.Current.Graph.Nodes.Length % 4 * 300, 60 + commands.Current.Graph.Nodes.Length / 4 * 220, type);
                    if (args["title"] is not null) node = node with { Title = S("title") };
                    if (kind == NodeKind.Text) node.Settings["text"] = S("text");
                    commands.Add(node); result = node; break;
                case "configure_node":
                    var existing = commands.Current.Graph.Nodes.Single(n => n.Id == Id("id")); var settings = new Dictionary<string, string>(existing.Settings);
                    if (args["settings"] is JsonObject changes) foreach (var setting in changes) settings[setting.Key] = setting.Value!.GetValue<string>();
                    if (settings.ContainsKey("artifactIds")) throw new ArgumentException("Artifact imports are available through branch_selection only.");
                    var updated = existing with { Title = S("title", existing.Title), Settings = settings }; commands.Update(updated); result = updated; break;
                case "connect_nodes": commands.Connect(Id("source"), S("output"), Id("target"), S("input")); result = new { revision = commands.Current.Graph.Id }; break;
                case "branch_selection":
                    ValidateProfiles(S("profiles")); var job = scheduler.Find(Id("jobId"));
                    if (job.State != JobState.Done || job.Selection is not { Length: > 0 }) throw new InvalidOperationException("Finish the human selection first.");
                    BatchPlan.Count(job.Outputs.Length * N("count", 3), S("profiles").Split(',').Distinct().Count(), 1);
                    WorkflowCommands.AddImageRound(commands, "", S("instructions"), N("count", 3), S("profiles"), selectionNode: job.NodeId, feedback: S("feedback", job.Feedback ?? ""));
                    var branch = commands.Current.Graph.Nodes.Last(n => n.Kind == NodeKind.Branch);
                    var next = scheduler.Submit(WorkflowCommands.ExecutionBranch(commands.Current, branch.Id, job.Outputs), CommandId(call.Id)); scheduler.Resume(); result = new { runId = next.Id }; break;
                case "cancel_job": scheduler.Cancel(Id("id")); result = new { state = "Cancellation requested" }; break;
                default: throw new ArgumentException("Unsupported tool.");
            }
            var json = DocumentJson.Write(result); store.SaveToolReceipt(receiptKey, json, commands.Current); return json;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Text.Json.JsonException or FormatException or OverflowException or KeyNotFoundException)
        {
            return DocumentJson.Write(new { error = ex.Message });
        }
    }
    private void ValidateProfiles(string value)
    {
        var ids = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (ids.Length is < 1 or > 8) throw new ArgumentException("Choose 1–8 image profiles.");
        foreach (var id in ids) if (registry.Get(id) is not { Runtime: RuntimeKind.StableDiffusion, Available: true }) throw new ArgumentException($"Image profile {id} is unavailable.");
    }
    private static Guid CommandId(string callId) => new(SHA256.HashData(Encoding.UTF8.GetBytes(callId)).AsSpan(0, 16));
}
