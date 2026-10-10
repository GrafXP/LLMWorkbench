using System.Collections.Concurrent;
using Workbench.Core;

namespace Workbench.Infrastructure;

/// <summary>Runs saved snapshots, never live view models. Human checkpoints release all execution leases.</summary>
public sealed class JobScheduler(IProjectStore store, INodeExecutor executor, ResourceArbiter resources)
{
    private readonly SemaphoreSlim _execution = new(1, 1);
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _active = new();
    private volatile bool _paused = true;
    public bool IsPaused => _paused;
    public event Action<ExecutionEvent>? Changed;
    public void Pause() => _paused = true;
    public void Resume() => _paused = false;
    public RunSnapshot Submit(ProjectDocument project, Guid commandId)
    {
        if (project.Graph.Nodes.Length == 0) throw new InvalidOperationException("Add a text node to the graph first.");
        foreach (var node in project.Graph.Nodes)
            if (!executor.Supports(node.Kind)) throw new InvalidOperationException($"No runtime is connected for {node.Title}.");
        return store.Submit(project, commandId);
    }
    public async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!await ProcessNextAsync(ct)) await Task.Delay(180, ct);
        }
    }
    public async Task<bool> ProcessNextAsync(CancellationToken ct = default)
    {
        if (_paused) return false;
        await _execution.WaitAsync(ct);
        try
        {
            if (_paused) return false;
            foreach (var run in store.LoadRuns().Reverse())
            {
                var jobs = store.LoadJobs(run.Id);
                foreach (var job in jobs.Where(j => j.State is JobState.Queued or JobState.WaitingForInput or JobState.Blocked))
                {
                    var dependencies = run.Graph.Edges.Where(e => e.TargetNode == job.NodeId).ToArray();
                    var upstream = dependencies.Select(e => jobs.Single(j => j.NodeId == e.SourceNode)).ToArray();
                    if (upstream.Any(j => j.State is JobState.Failed or JobState.Cancelled or JobState.Interrupted or JobState.Blocked))
                    {
                        if (job.State != JobState.Blocked) Save(job with { State = JobState.Blocked, Message = "Waiting for an upstream job to be retried." });
                        continue;
                    }
                    if (upstream.Any(j => j.State != JobState.Done))
                    {
                        if (job.State != JobState.WaitingForInput) Save(job with { State = JobState.WaitingForInput, Message = upstream.Any(j => j.State == JobState.AwaitingSelection) ? "Waiting for a selection." : "Waiting for inputs." });
                        continue;
                    }
                    var bindings = dependencies.ToDictionary(e => e.TargetPort, e => jobs.Single(j => j.NodeId == e.SourceNode).Outputs);
                    await ExecuteAsync(run, job with { Inputs = bindings }, ct);
                    return true;
                }
            }
            return false;
        }
        finally { _execution.Release(); }
    }
    private async Task ExecuteAsync(RunSnapshot run, JobRecord job, CancellationToken sessionToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
        _active[job.Id] = cts;
        try
        {
            // Recheck after publishing the cancellation handle: a queued cancellation must win.
            if (Find(job.Id).State == JobState.Cancelled) return;
            var node = run.Graph.Nodes.Single(n => n.Id == job.NodeId);
            job = job with { State = JobState.Running, Attempt = job.Attempt + 1, StartedUtc = DateTimeOffset.UtcNow, FinishedUtc = null, Message = "Running" };
            Save(job);
            var artifacts = store.LoadArtifacts().ToDictionary(a => a.Id);
            var inputs = job.Inputs.ToDictionary(kv => kv.Key, kv => kv.Value.Select(id => artifacts.TryGetValue(id, out var artifact) ? artifact : throw new InvalidDataException("An input artifact is missing.")).ToArray());
            foreach (var port in node.Ports.Where(p => p.IsInput))
            {
                var values = inputs.GetValueOrDefault(port.Id, []);
                if (port.Required && values.Length == 0 || values.Length > 0 && port.Cardinality == Cardinality.Single && values.Length != 1)
                    throw new InvalidDataException($"{port.Name} has an invalid number of input artifacts.");
                if (values.Any(a => a.Kind != port.Type)) throw new InvalidDataException($"{port.Name} received an incompatible artifact type.");
            }
            using var lease = await resources.AcquireAsync(executor.ResourceFor(node.Kind), job.Id, cts.Token);
            var result = await executor.ExecuteAsync(new(run, node, job, inputs), cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            ValidateResult(node, result, artifacts);
            job = job with { State = result.State, Outputs = result.Outputs, Message = result.Message, FinishedUtc = DateTimeOffset.UtcNow };
            store.Complete(job, result.Artifacts);
            Publish(job);
        }
        catch (OperationCanceledException)
        {
            Save(job with { State = sessionToken.IsCancellationRequested ? JobState.Interrupted : JobState.Cancelled, Message = sessionToken.IsCancellationRequested ? "Session stopped. Retry to continue." : "Cancelled", FinishedUtc = DateTimeOffset.UtcNow });
        }
        catch (Exception ex)
        {
            Save(job with { State = JobState.Failed, Message = ex.Message, FinishedUtc = DateTimeOffset.UtcNow });
        }
        finally { _active.TryRemove(job.Id, out _); }
    }
    public void Cancel(Guid jobId)
    {
        var job = Find(jobId);
        if (job.State is JobState.Done or JobState.Failed or JobState.Cancelled or JobState.Interrupted) return;
        Save(job with { State = JobState.Cancelled, Message = "Cancelled", FinishedUtc = DateTimeOffset.UtcNow });
        if (_active.TryGetValue(jobId, out var cts)) cts.Cancel();
    }
    public void Retry(Guid jobId)
    {
        var job = Find(jobId);
        if (job.State is not (JobState.Failed or JobState.Cancelled or JobState.Interrupted or JobState.Blocked))
            throw new InvalidOperationException("Only a failed, cancelled, interrupted, or blocked job can be retried.");
        Save(job with { State = JobState.Queued, Outputs = [], Message = "Queued for retry", FinishedUtc = null });
    }
    public void Choose(Guid jobId, Guid[] artifactIds)
    {
        var job = Find(jobId);
        if (job.State != JobState.AwaitingSelection) throw new InvalidOperationException("This job is not waiting for a selection.");
        var candidates = job.Inputs.Values.SelectMany(x => x).ToHashSet();
        if (artifactIds.Length == 0 || artifactIds.Any(id => !candidates.Contains(id)))
            throw new InvalidOperationException("Choose at least one artifact from this job's candidates.");
        Save(job with { State = JobState.Queued, Selection = artifactIds.Distinct().ToArray(), Message = "Selection saved" });
    }
    public JobRecord Find(Guid id) => store.LoadRuns().SelectMany(r => store.LoadJobs(r.Id)).Single(j => j.Id == id);
    private static void ValidateResult(GraphNode node, ExecutionResult result, Dictionary<Guid, Artifact> existing)
    {
        if (result.State is not (JobState.Done or JobState.AwaitingSelection)) throw new InvalidDataException("The executor returned an invalid completion state.");
        if (result.State == JobState.AwaitingSelection)
        {
            if (result.Outputs.Length > 0 || result.Artifacts.Length > 0) throw new InvalidDataException("A selection checkpoint cannot publish final outputs.");
            return;
        }
        var output = node.Ports.Single(p => !p.IsInput);
        if (output.Cardinality == Cardinality.Single && result.Outputs.Length != 1) throw new InvalidDataException("The executor must return one output for a single-artifact port.");
        var produced = new Dictionary<Guid, Artifact>(existing);
        foreach (var artifact in result.Artifacts)
        {
            if (artifact.SourceNode != node.Id || artifact.Parents.Any(id => !existing.ContainsKey(id))) throw new InvalidDataException("The output artifact has invalid lineage.");
            if (!produced.TryAdd(artifact.Id, artifact)) throw new InvalidDataException("The executor reused an immutable artifact ID.");
        }
        foreach (var id in result.Outputs)
            if (!produced.TryGetValue(id, out var artifact) || artifact.Kind != output.Type) throw new InvalidDataException("The executor returned a missing or incompatible output artifact.");
    }
    private void Save(JobRecord job) { store.SaveJob(job); Publish(job); }
    private void Publish(JobRecord job) => Changed?.Invoke(new(job.RunId, job.NodeId, job.Id, job.State, job.Message, DateTimeOffset.UtcNow));
}
