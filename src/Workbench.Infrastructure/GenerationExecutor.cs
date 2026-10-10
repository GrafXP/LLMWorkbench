using Workbench.Core;

namespace Workbench.Infrastructure;

public sealed class GenerationExecutor(SqliteProjectStore store, ModelRegistry registry, IGenerationBackend backend,
    ResourceArbiter arbiter, Func<Task>? releaseModel = null, Func<CancellationToken, Task>? beforeImage = null) : INodeExecutor, IProgressExecutor
{
    private readonly BuiltinExecutor _builtins = new();
    public event Action<Guid, string>? Progress;
    public bool Supports(NodeKind kind) => _builtins.Supports(kind) || kind is NodeKind.GenerateText or NodeKind.Variations or NodeKind.PrepareImage or NodeKind.Image or NodeKind.Branch;
    public ResourceKind ResourceFor(NodeKind kind) => kind is NodeKind.GenerateText or NodeKind.Variations or NodeKind.PrepareImage ? ResourceKind.Gpu : ResourceKind.Cpu;
    public async Task<ExecutionResult> ExecuteAsync(JobContext c, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (ManualOutputs.Resolve(c, store) is { } saved) return saved;
        if (c.Node.Kind == NodeKind.Select && releaseModel is not null)
        {
            using var lease = await arbiter.AcquireAsync(ResourceKind.Gpu, c.Job.Id, ct); await releaseModel();
        }
        if (_builtins.Supports(c.Node.Kind)) return await _builtins.ExecuteAsync(c, ct);
        if (c.Node.Kind == NodeKind.Image) return await ImagesAsync(c, ct);
        if (c.Node.Kind == NodeKind.Branch) return Branch(c);
        var outputs = new List<Artifact>();
        var sources = c.Inputs.Values.SelectMany(a => a).ToArray();
        if (c.Node.Kind is NodeKind.Variations or NodeKind.GenerateText)
        {
            var count = c.Node.Kind == NodeKind.GenerateText ? 1 : int.Parse(c.Node.Settings.GetValueOrDefault("count", "6"));
            if (count is < 1 or > 12 || sources.Length * count > 256) throw new ArgumentException("Choose 1–12 variations per source, at most 256 texts.");
            foreach (var source in sources)
            {
                Progress?.Invoke(c.Job.Id, $"Qwen · writing {count} text(s) from source {outputs.Count / count + 1}/{sources.Length}");
                var instruction = c.Node.Settings.GetValueOrDefault("instructions", "Vary the description.");
                if (source.Metadata?.GetValueOrDefault("feedback") is { Length: > 0 } feedback) instruction += "\nSelection feedback: " + feedback;
                foreach (var text in await backend.VariationsAsync(source.Content, instruction, count, ct))
                    outputs.Add(New(c, ArtifactKind.Text, text, [source.Id], new() { ["instructions"] = instruction, ["preservation"] = "Semantic constraints require human review.",
                        ["textModel"] = DocumentJson.Write(registry.Qwen), ["textModelFiles"] = Evidence(registry.Qwen) }));
            }
        }
        else if (c.Node.Kind == NodeKind.PrepareImage)
        {
            var settings = c.Node.Settings;
            var profiles = settings.GetValueOrDefault("profiles", "klein4b").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct().Select(registry.Get).ToArray();
            var seeds = BatchPlan.Seeds(settings.GetValueOrDefault("seeds", "42"));
            BatchPlan.Count(sources.Length, profiles.Length, seeds.Length);
            var width = BatchPlan.Dimension(settings.GetValueOrDefault("width", "512")); var height = BatchPlan.Dimension(settings.GetValueOrDefault("height", "512"));
            foreach (var profile in profiles)
            {
                if (profile.Runtime != RuntimeKind.StableDiffusion || !profile.Available) throw new InvalidOperationException($"{profile.Name} is not an available image profile. Missing: {string.Join(", ", profile.MissingFiles)}");
                foreach (var source in sources)
                {
                    Progress?.Invoke(c.Job.Id, $"Preparing {outputs.Count + 1}/{sources.Length * profiles.Length} · {profile.Name}");
                    var prompt = (await backend.VariationsAsync(source.Content, "Adapt this image description to the target model. Preserve its subject and intent. Return one finished prompt, no commentary.\nModel guidance: " + profile.PromptGuide, 1, ct))[0];
                    var spec = new ImageSpecification(prompt, settings.GetValueOrDefault("negative", ""), profile, width, height, profile.Steps, profile.Cfg, seeds);
                    outputs.Add(New(c, ArtifactKind.ImageSpecification, DocumentJson.Write(spec), [source.Id], new() { ["model"] = profile.Id, ["sourceText"] = source.Content,
                        ["preparationModel"] = DocumentJson.Write(registry.Qwen), ["preparationModelFiles"] = Evidence(registry.Qwen) }));
                }
            }
        }
        else throw new InvalidOperationException("Unsupported generation node.");
        return ExecutionResult.Complete([.. outputs]);
    }
    private ExecutionResult Branch(JobContext c)
    {
        var all = store.LoadArtifacts().ToDictionary(a => a.Id);
        var selected = c.Inputs.Values.SelectMany(a => a).ToArray();
        if (selected.Length == 0 && c.Node.Settings.TryGetValue("artifactIds", out var ids))
            selected = ids.Split(',').Select(id => all[Guid.Parse(id)]).ToArray();
        if (selected.Length == 0 || selected.Any(a => a.Kind != ArtifactKind.Image)) throw new InvalidOperationException("Choose completed images to start another round.");
        var texts = selected.Select(image =>
        {
            var spec = image.Parents.Select(id => all[id]).Single(a => a.Kind == ArtifactKind.ImageSpecification);
            var source = spec.Parents.Select(id => all[id]).Single(a => a.Kind == ArtifactKind.Text);
            return New(c, ArtifactKind.Text, source.Content, [image.Id, source.Id], new() { ["feedback"] = c.Node.Settings.GetValueOrDefault("feedback", ""), ["selectedImage"] = image.Id.ToString() });
        }).ToArray();
        return ExecutionResult.Complete(texts);
    }
    private async Task<ExecutionResult> ImagesAsync(JobContext c, CancellationToken ct)
    {
        var items = store.LoadRenderItems(c.Job.Id);
        if (items.Length == 0)
        {
            items = c.Inputs.Values.SelectMany(a => a).SelectMany(a =>
            {
                var spec = DocumentJson.Read<ImageSpecification>(a.Content);
                return spec.Seeds.Select(seed => new RenderItem(Guid.NewGuid(), c.Job.Id, a.Id, spec, seed));
            }).ToArray();
            BatchPlan.Count(items.Length, 1, 1); store.SaveRenderItems(items);
        }
        foreach (var original in items.Where(i => i.State != JobState.Done))
        {
            ct.ThrowIfCancellationRequested();
            if (beforeImage is not null) await beforeImage(ct);
            var item = original with { State = JobState.Running, Attempt = original.Attempt + 1, StartedUtc = DateTimeOffset.UtcNow, FinishedUtc = null };
            store.SaveRenderItems([item]);
            try
            {
                using var lease = await arbiter.AcquireAsync(ResourceKind.Gpu, c.Job.Id, ct);
                var completed = store.LoadRenderItems(c.Job.Id).Count(i => i.State == JobState.Done);
                var png = await backend.RenderAsync(item.Specification, item.Seed, message => Progress?.Invoke(c.Job.Id, $"{completed}/{items.Length} images · {message}"), ct);
                ct.ThrowIfCancellationRequested();
                var files = item.Specification.Profile.Files.ToDictionary(kv => kv.Key, kv =>
                { var info = new FileInfo(kv.Value); return $"{info.FullName} | {info.Length} bytes | {info.LastWriteTimeUtc:O}"; });
                var artifact = New(c, ArtifactKind.Image, item.Specification.Prompt, [item.SpecificationId], new()
                {
                    ["seed"] = item.Seed.ToString(), ["model"] = item.Specification.Profile.Id, ["specification"] = DocumentJson.Write(item.Specification),
                    ["files"] = DocumentJson.Write(files), ["runtime"] = item.Specification.Profile.Executable,
                    ["runtimeFingerprint"] = File.Exists(item.Specification.Profile.Executable) ? $"{new FileInfo(item.Specification.Profile.Executable).Length} | {File.GetLastWriteTimeUtc(item.Specification.Profile.Executable):O}" : "Test backend",
                    ["renderItem"] = item.Id.ToString()
                });
                store.CompleteRenderItem(item, artifact, png);
                Progress?.Invoke(c.Job.Id, $"{completed + 1}/{items.Length} images complete");
            }
            catch (Exception ex)
            {
                store.SaveRenderItems([item with { State = ex is OperationCanceledException ? JobState.Cancelled : JobState.Failed, Message = ex.Message, FinishedUtc = DateTimeOffset.UtcNow }]); throw;
            }
        }
        return ExecutionResult.Complete([], store.LoadRenderItems(c.Job.Id).Select(i => i.ArtifactId!.Value).ToArray());
    }
    private static Artifact New(JobContext c, ArtifactKind kind, string text, Guid[] parents, Dictionary<string, string>? metadata = null) =>
        new(Guid.NewGuid(), kind, text, null, parents, c.Node.Id, DateTimeOffset.UtcNow, metadata);
    private static string Evidence(ModelProfile profile) => DocumentJson.Write(profile.Files.Append(new("executable", profile.Executable)).ToDictionary(k => k.Key, k =>
    { var file = new FileInfo(k.Value); return file.Exists ? $"{file.FullName} | {file.Length} bytes | {file.LastWriteTimeUtc:O}" : $"Missing: {file.FullName}"; }));
}
