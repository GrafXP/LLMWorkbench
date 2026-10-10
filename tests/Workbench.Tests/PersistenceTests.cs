using Workbench.Core;
using Workbench.Infrastructure;
using Xunit;

namespace Workbench.Tests;

public sealed class PersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "workbench-tests", Guid.NewGuid().ToString());
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
    private static ProjectDocument Graph()
    {
        var commands = new ProjectCommands(ProjectDocument.Create("Selection test"));
        var a = GraphNode.Create(NodeKind.Text); a.Settings["text"] = "First candidate";
        var b = GraphNode.Create(NodeKind.Text); b.Settings["text"] = "Second candidate";
        var collect = GraphNode.Create(NodeKind.Collect); var select = GraphNode.Create(NodeKind.Select);
        var downstream = GraphNode.Create(NodeKind.Select);
        foreach (var n in new[] { a, b, collect, select, downstream }) commands.Add(n);
        commands.Connect(a.Id, "text", collect.Id, "a"); commands.Connect(b.Id, "text", collect.Id, "b");
        commands.Connect(collect.Id, "items", select.Id, "items"); commands.Connect(select.Id, "chosen", downstream.Id, "items");
        return commands.Current;
    }
    [Fact]
    public async Task SelectionCheckpointSurvivesRestartAndKeepsCompletedArtifacts()
    {
        var graph = Graph(); Guid runId; Guid choiceId; Guid artifactId;
        using (var store = new SqliteProjectStore(_directory))
        {
            store.SaveProject(graph);
            var scheduler = new JobScheduler(store, new BuiltinExecutor(), new()); scheduler.Resume();
            runId = scheduler.Submit(graph, Guid.NewGuid()).Id;
            while (await scheduler.ProcessNextAsync()) { }
            var jobs = store.LoadJobs(runId);
            choiceId = jobs.Single(j => j.State == JobState.AwaitingSelection).Id;
            artifactId = store.LoadArtifacts()[0].Id;
            Assert.Equal(2, store.LoadArtifacts().Length);
            Assert.Contains(jobs, j => j.State == JobState.WaitingForInput);
        }
        using (var store = new SqliteProjectStore(_directory))
        {
            Assert.Equal(graph.Id, store.LoadProject()!.Id);
            var scheduler = new JobScheduler(store, new BuiltinExecutor(), new()); scheduler.Resume();
            scheduler.Choose(choiceId, [artifactId]);
            while (await scheduler.ProcessNextAsync()) { }
            Assert.Equal(2, store.LoadArtifacts().Length);
            Assert.Equal([artifactId], scheduler.Find(choiceId).Outputs);
            Assert.All(store.LoadArtifacts(), a => Assert.True(File.Exists(Path.Combine(_directory, a.RelativePath!))));
        }
    }
    [Fact]
    public async Task RunUsesSavedTextAndSubmissionIsIdempotent()
    {
        using var store = new SqliteProjectStore(_directory);
        var graph = Graph(); var command = Guid.NewGuid();
        var scheduler = new JobScheduler(store, new BuiltinExecutor(), new()); scheduler.Resume();
        var run = scheduler.Submit(graph, command);
        graph.Graph.Nodes[0].Settings["text"] = "Edited after submission";
        Assert.Equal(run.Id, scheduler.Submit(graph, command).Id);
        while (await scheduler.ProcessNextAsync()) { }
        Assert.Equal("First candidate", store.LoadArtifacts()[0].Content);
        Assert.Single(store.LoadRuns());
    }
    [Fact]
    public void ReopeningRecoversRunningJobsWithoutTouchingCompletedJobs()
    {
        Guid runningId; Guid doneId;
        using (var store = new SqliteProjectStore(_directory))
        {
            var run = store.Submit(Graph(), Guid.NewGuid()); var jobs = store.LoadJobs(run.Id);
            runningId = jobs[0].Id; doneId = jobs[1].Id;
            store.SaveJob(jobs[0] with { State = JobState.Running });
            store.SaveJob(jobs[1] with { State = JobState.Done });
        }
        using (var store = new SqliteProjectStore(_directory))
        {
            var jobs = store.LoadJobs(store.LoadRuns()[0].Id);
            Assert.Equal(JobState.Interrupted, jobs.Single(j => j.Id == runningId).State);
            Assert.Equal(JobState.Done, jobs.Single(j => j.Id == doneId).State);
        }
    }
    [Fact]
    public async Task FailedInputBlocksDescendantsAndRetryRecoversThem()
    {
        using var store = new SqliteProjectStore(_directory);
        var executor = new FailOnceExecutor(); var scheduler = new JobScheduler(store, executor, new()); scheduler.Resume();
        var run = scheduler.Submit(Graph(), Guid.NewGuid());
        while (await scheduler.ProcessNextAsync()) { }
        var failed = store.LoadJobs(run.Id).Single(j => j.State == JobState.Failed);
        Assert.Contains(store.LoadJobs(run.Id), j => j.State == JobState.Blocked);
        scheduler.Retry(failed.Id);
        while (await scheduler.ProcessNextAsync()) { }
        Assert.DoesNotContain(store.LoadJobs(run.Id), j => j.State is JobState.Failed or JobState.Blocked);
    }
    private sealed class FailOnceExecutor : INodeExecutor
    {
        private bool _failed;
        private readonly BuiltinExecutor _inner = new();
        public bool Supports(NodeKind kind) => _inner.Supports(kind);
        public ResourceKind ResourceFor(NodeKind kind) => ResourceKind.Cpu;
        public Task<ExecutionResult> ExecuteAsync(JobContext context, CancellationToken ct)
        {
            if (!_failed) { _failed = true; throw new InvalidOperationException("Transient error"); }
            return _inner.ExecuteAsync(context, ct);
        }
    }
}
