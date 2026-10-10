namespace Workbench.Core;

/// <summary>Saved human revisions replace a node's outputs in future runs, while historical artifacts stay immutable.</summary>
public static class ManualOutputs
{
    public const string Setting = "manualArtifactIds";
    public static Guid[] Read(GraphNode node) => node.Settings.TryGetValue(Setting, out var value)
        ? DocumentJson.Read<Guid[]>(value) : [];

    public static GraphNode Pin(GraphNode node, Artifact[] outputs)
    {
        var port = node.Ports.Single(p => !p.IsInput);
        if (node.Kind is not (NodeKind.Text or NodeKind.GenerateText or NodeKind.Variations or NodeKind.PrepareImage or NodeKind.Branch) ||
            outputs.Length == 0 || outputs.Any(a => a.SourceNode != node.Id || a.Kind != port.Type) ||
            outputs.Select(a => a.Id).Distinct().Count() != outputs.Length ||
            port.Cardinality == Cardinality.Single && outputs.Length != 1 ||
            port.Type is not (ArtifactKind.Text or ArtifactKind.ImageSpecification))
            throw new InvalidOperationException("Save a complete set of text outputs for their originating node.");
        return node with { Settings = new(node.Settings) { [Setting] = DocumentJson.Write(outputs.Select(a => a.Id).ToArray()) } };
    }

    public static ExecutionResult? Resolve(JobContext context, IProjectStore store)
    {
        if (!context.Node.Settings.ContainsKey(Setting)) return null;
        var artifacts = store.LoadArtifacts().ToDictionary(a => a.Id);
        var outputs = Read(context.Node).Select(id => artifacts.TryGetValue(id, out var a) ? a
            : throw new InvalidDataException("A saved manual text revision is missing.")).ToArray();
        Pin(context.Node, outputs); // Enforce output ownership, type and cardinality for saved/agent-authored settings too.
        return ExecutionResult.Complete([], outputs.Select(a => a.Id).ToArray());
    }

    public static ProjectDocument Downstream(ProjectDocument project, Guid root)
    {
        var graph = project.Graph;
        var source = graph.Nodes.Single(n => n.Id == root);
        if (source.Kind != NodeKind.Text && Read(source).Length == 0)
            throw new InvalidOperationException("Save your text edits before running downstream.");
        var reachable = new HashSet<Guid> { root };
        var queue = new Queue<Guid>(); queue.Enqueue(root);
        while (queue.TryDequeue(out var id))
            foreach (var edge in graph.Edges.Where(e => e.SourceNode == id))
                if (reachable.Add(edge.TargetNode)) queue.Enqueue(edge.TargetNode);
        // Keep other dependencies of the downstream branch, so joins never run with missing inputs.
        queue = new Queue<Guid>(reachable.Where(id => id != root));
        while (queue.TryDequeue(out var id))
            foreach (var edge in graph.Edges.Where(e => e.TargetNode == id && id != root))
                if (reachable.Add(edge.SourceNode)) queue.Enqueue(edge.SourceNode);
        var nodes = graph.Nodes.Where(n => reachable.Contains(n.Id)).Select(n => n.Id == root
            ? n with { Ports = n.Ports.Where(p => !p.IsInput).ToArray() } : n).ToArray();
        return project with { Graph = new(Guid.NewGuid(), nodes,
            graph.Edges.Where(e => reachable.Contains(e.SourceNode) && reachable.Contains(e.TargetNode) && e.TargetNode != root).ToArray(), graph.Id) };
    }
}
