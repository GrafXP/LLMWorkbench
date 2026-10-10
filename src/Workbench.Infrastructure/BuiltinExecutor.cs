using Workbench.Core;

namespace Workbench.Infrastructure;

public sealed class BuiltinExecutor : INodeExecutor
{
    public bool Supports(NodeKind kind) => kind is NodeKind.Text or NodeKind.Collect or NodeKind.Select;
    public ResourceKind ResourceFor(NodeKind kind) => ResourceKind.Cpu;
    public Task<ExecutionResult> ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.Node.Settings.ContainsKey(ManualOutputs.Setting))
            throw new InvalidOperationException("Saved manual outputs require the project's generation executor.");
        return Task.FromResult(context.Node.Kind switch
        {
            NodeKind.Text => Text(context),
            NodeKind.Collect => ExecutionResult.Complete([], context.Inputs.Values.SelectMany(a => a).Select(a => a.Id).Distinct().ToArray()),
            NodeKind.Select => Select(context),
            _ => throw new InvalidOperationException("No runtime is connected for this operation.")
        });
    }
    private static ExecutionResult Text(JobContext context)
    {
        var text = context.Node.Settings.GetValueOrDefault("text", "");
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("Write some text before running this draft.");
        return ExecutionResult.Complete([new Artifact(Guid.NewGuid(), ArtifactKind.Text, text, null, [], context.Node.Id, DateTimeOffset.UtcNow)]);
    }
    private static ExecutionResult Select(JobContext context)
    {
        var candidates = context.Inputs.Values.SelectMany(a => a).Select(a => a.Id).ToHashSet();
        if (candidates.Count == 0) throw new InvalidOperationException("No candidates are available.");
        if (context.Job.Selection is not { Length: > 0 } choices)
            return new(JobState.AwaitingSelection, [], [], $"Choose favourites from {candidates.Count} candidates.");
        if (choices.Any(id => !candidates.Contains(id))) throw new InvalidOperationException("The selection contains an artifact outside this run's candidates.");
        return ExecutionResult.Complete([], choices.Distinct().ToArray());
    }
}
