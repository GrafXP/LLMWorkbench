using Microsoft.Data.Sqlite;
using Workbench.Core;
using Workbench.Infrastructure;
using Xunit;

namespace Workbench.Tests;

public sealed class QueueReliabilityTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "workbench-tests", Guid.NewGuid().ToString());
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
    private static ProjectDocument OneText()
    {
        var node = GraphNode.Create(NodeKind.Text); node.Settings["text"] = "A saved draft";
        return ProjectDocument.Create() with { Graph = new(Guid.NewGuid(), [node], []) };
    }
    [Fact]
    public async Task CancellingActiveJobReleasesGpuAndRetryCreatesOneArtifact()
    {
        using var store = new SqliteProjectStore(_directory);
        var executor = new CancellableExecutor(); var arbiter = new ResourceArbiter();
        var scheduler = new JobScheduler(store, executor, arbiter); scheduler.Resume();
        var run = scheduler.Submit(OneText(), Guid.NewGuid()); var id = store.LoadJobs(run.Id)[0].Id;
        var processing = scheduler.ProcessNextAsync();
        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(id, arbiter.GpuOwner);
        scheduler.Cancel(id); await processing;
        Assert.Equal(JobState.Cancelled, scheduler.Find(id).State);
        Assert.Null(arbiter.GpuOwner); Assert.Empty(store.LoadArtifacts());
        scheduler.Retry(id); await scheduler.ProcessNextAsync();
        Assert.Equal(JobState.Done, scheduler.Find(id).State);
        Assert.Equal(2, scheduler.Find(id).Attempt);
        Assert.Single(store.LoadArtifacts()); Assert.Null(arbiter.GpuOwner);
    }
    [Fact]
    public async Task InvalidExecutorOutputsFailWithoutPublishingArtifacts()
    {
        using var store = new SqliteProjectStore(_directory);
        var scheduler = new JobScheduler(store, new InvalidExecutor(), new()); scheduler.Resume();
        var run = scheduler.Submit(OneText(), Guid.NewGuid()); await scheduler.ProcessNextAsync();
        Assert.Equal(JobState.Failed, store.LoadJobs(run.Id)[0].State);
        Assert.Empty(store.LoadArtifacts());
    }
    [Fact]
    public void ProjectOwnershipAndIdentityPreventAccidentalOverwrite()
    {
        using var store = new SqliteProjectStore(_directory); var project = OneText(); store.SaveProject(project);
        Assert.Throws<IOException>(() => new SqliteProjectStore(_directory));
        Assert.Throws<InvalidOperationException>(() => store.SaveProject(OneText()));
        Assert.Equal(project.Id, store.LoadProject()!.Id);
    }
    [Fact]
    public void UnsupportedDatabaseSchemaIsNotRewritten()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "project.sqlite");
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            db.Open(); using var command = db.CreateCommand(); command.CommandText = "PRAGMA user_version=999"; command.ExecuteNonQuery();
        }
        Assert.Throws<InvalidDataException>(() => new SqliteProjectStore(_directory));
        using var read = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        read.Open(); using var check = read.CreateCommand(); check.CommandText = "PRAGMA user_version";
        Assert.Equal(999L, check.ExecuteScalar());
    }
    private sealed class CancellableExecutor : INodeExecutor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _attempt;
        public bool Supports(NodeKind kind) => true;
        public ResourceKind ResourceFor(NodeKind kind) => ResourceKind.Gpu;
        public async Task<ExecutionResult> ExecuteAsync(JobContext context, CancellationToken ct)
        {
            if (++_attempt == 1) { Started.SetResult(); await Task.Delay(Timeout.Infinite, ct); }
            return await new BuiltinExecutor().ExecuteAsync(context, ct);
        }
    }
    private sealed class InvalidExecutor : INodeExecutor
    {
        public bool Supports(NodeKind kind) => true;
        public ResourceKind ResourceFor(NodeKind kind) => ResourceKind.Cpu;
        public Task<ExecutionResult> ExecuteAsync(JobContext context, CancellationToken ct) => Task.FromResult(ExecutionResult.Complete([], [Guid.NewGuid()]));
    }
}
