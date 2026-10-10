using Workbench.Core;
using Xunit;

namespace Workbench.Tests;

public class GraphTests
{
    [Fact]
    public void RejectsIncompatibleTypesAndCardinalityWithoutMutatingProject()
    {
        var commands = new ProjectCommands(ProjectDocument.Create());
        var text = GraphNode.Create(NodeKind.Text);
        var selection = GraphNode.Create(NodeKind.Select);
        var images = GraphNode.Create(NodeKind.Collect, type: ArtifactKind.Image);
        commands.Add(text); commands.Add(selection); commands.Add(images);
        var revision = commands.Current.Graph.Id;
        Assert.Throws<GraphValidationException>(() => commands.Connect(text.Id, "text", selection.Id, "items"));
        Assert.Throws<GraphValidationException>(() => commands.Connect(text.Id, "text", images.Id, "a"));
        Assert.Equal(revision, commands.Current.Graph.Id);
        Assert.Empty(commands.Current.Graph.Edges);
    }
    [Fact]
    public void RejectedCycleDoesNotRemoveAnExistingConnection()
    {
        var commands = new ProjectCommands(ProjectDocument.Create());
        var collect = GraphNode.Create(NodeKind.Collect);
        var a = GraphNode.Create(NodeKind.Select); var b = GraphNode.Create(NodeKind.Select);
        commands.Add(collect); commands.Add(a); commands.Add(b);
        commands.Connect(collect.Id, "items", a.Id, "items");
        commands.Connect(a.Id, "chosen", b.Id, "items");
        Assert.Throws<GraphValidationException>(() => commands.Connect(b.Id, "chosen", a.Id, "items"));
        Assert.Contains(commands.Current.Graph.Edges, e => e.SourceNode == collect.Id && e.TargetNode == a.Id);
    }
    [Fact]
    public void SnapshotsAndUndoPreserveTextAndStablePorts()
    {
        var commands = new ProjectCommands(ProjectDocument.Create());
        var node = GraphNode.Create(NodeKind.Text);
        node.Settings["text"] = "First draft";
        commands.Add(node);
        var snapshot = commands.Current;
        node.Settings["text"] = "Second draft";
        commands.Update(node);
        Assert.Equal("First draft", snapshot.Graph.Nodes[0].Settings["text"]);
        commands.Undo();
        Assert.Equal("First draft", commands.Current.Graph.Nodes[0].Settings["text"]);
        commands.Redo();
        Assert.Equal("Second draft", commands.Current.Graph.Nodes[0].Settings["text"]);
        Assert.Equal("text", commands.Current.Graph.Nodes[0].Ports[0].Id);
    }
    [Fact]
    public async Task GpuLeaseIsExclusiveAndCancellationDoesNotLeakIt()
    {
        var arbiter = new ResourceArbiter(); var first = Guid.NewGuid();
        using (await arbiter.AcquireAsync(ResourceKind.Gpu, first, CancellationToken.None))
        {
            using var cts = new CancellationTokenSource();
            var waiting = arbiter.AcquireAsync(ResourceKind.Gpu, Guid.NewGuid(), cts.Token);
            Assert.False(waiting.IsCompleted);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waiting);
            Assert.Equal(first, arbiter.GpuOwner);
        }
        using (await arbiter.AcquireAsync(ResourceKind.Gpu, Guid.NewGuid(), CancellationToken.None)) Assert.NotNull(arbiter.GpuOwner);
        Assert.Null(arbiter.GpuOwner);
    }
}
