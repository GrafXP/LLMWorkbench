namespace Workbench.Core;

public sealed record Artifact(Guid Id, ArtifactKind Kind, string Content, string? RelativePath, Guid[] Parents, Guid SourceNode, DateTimeOffset CreatedUtc,
    Dictionary<string, string>? Metadata = null);
public sealed record RunSnapshot(Guid Id, Guid CommandId, Guid ProjectId, GraphRevision Graph, DateTimeOffset CreatedUtc);
public sealed record JobRecord(Guid Id, Guid RunId, Guid NodeId, JobState State, Dictionary<string, Guid[]> Inputs, Guid[] Outputs,
    int Attempt, string Message, DateTimeOffset? StartedUtc = null, DateTimeOffset? FinishedUtc = null, Guid[]? Selection = null, string? Feedback = null);
public sealed record ExecutionResult(JobState State, Artifact[] Artifacts, Guid[] Outputs, string Message)
{
    public static ExecutionResult Complete(Artifact[] artifacts, Guid[]? outputs = null) => new(JobState.Done, artifacts, outputs ?? artifacts.Select(a => a.Id).ToArray(), "Complete");
}
public sealed record JobContext(RunSnapshot Run, GraphNode Node, JobRecord Job, Dictionary<string, Artifact[]> Inputs);
public sealed record ExecutionEvent(Guid RunId, Guid NodeId, Guid JobId, JobState State, string Message, DateTimeOffset Timestamp);

public interface INodeExecutor
{
    bool Supports(NodeKind kind);
    ResourceKind ResourceFor(NodeKind kind);
    Task<ExecutionResult> ExecuteAsync(JobContext context, CancellationToken cancellationToken);
}
public interface IProgressExecutor { event Action<Guid, string>? Progress; }
public interface IProjectStore
{
    ProjectDocument? LoadProject();
    void SaveProject(ProjectDocument project);
    RunSnapshot Submit(ProjectDocument project, Guid commandId);
    RunSnapshot[] LoadRuns();
    JobRecord[] LoadJobs(Guid runId);
    Artifact[] LoadArtifacts();
    void SaveJob(JobRecord job);
    void Complete(JobRecord job, Artifact[] artifacts);
    void RecoverInterrupted();
}

/// <summary>A lease spans actual execution only; disposal releases it even on failure/cancellation.</summary>
public sealed class ResourceArbiter
{
    private readonly SemaphoreSlim _gpu = new(1, 1);
    public Guid? GpuOwner { get; private set; }
    public event Action? Changed;
    public async Task<IDisposable> AcquireAsync(ResourceKind resource, Guid jobId, CancellationToken ct)
    {
        if (resource == ResourceKind.Cpu) return new Lease(() => { });
        await _gpu.WaitAsync(ct);
        GpuOwner = jobId;
        Changed?.Invoke();
        return new Lease(() => { GpuOwner = null; _gpu.Release(); Changed?.Invoke(); });
    }
    private sealed class Lease(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
