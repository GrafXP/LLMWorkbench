namespace Workbench.Core;

/// <summary>Both chat and the desktop construct rounds with these validated, undoable graph transactions.</summary>
public static class WorkflowCommands
{
    public static Guid AddImageRound(ProjectCommands commands, string brief, string instructions, int count,
        string profiles, int width = 512, int height = 512, string seeds = "42", Guid? selectionNode = null, string feedback = "")
    {
        if (count is < 1 or > 12) throw new ArgumentException("Choose 1–12 variations per source text.");
        BatchPlan.Dimension(width.ToString()); BatchPlan.Dimension(height.ToString()); var seedCount = BatchPlan.Seeds(seeds).Length;
        if (selectionNode is null) BatchPlan.Count(count, profiles.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct().Count(), seedCount);
        var graph = commands.Current.Graph;
        var y = graph.Nodes.Length == 0 ? 60 : graph.Nodes.Max(n => n.Y) + GraphNode.Height + 100;
        var nodes = new List<GraphNode>(); var edges = new List<GraphEdge>();
        GraphNode source; string output;
        if (selectionNode is { } selected)
        {
            var previous = graph.Nodes.Single(n => n.Id == selected);
            if (previous.Kind != NodeKind.Select || previous.Ports.Single(p => !p.IsInput).Type != ArtifactKind.Image)
                throw new GraphValidationException("Branch from an image selection node.");
            source = GraphNode.Create(NodeKind.Branch, 60, y); source.Settings["feedback"] = feedback;
            edges.Add(new(Guid.NewGuid(), selected, "chosen", source.Id, "chosen")); output = "texts";
        }
        else
        {
            if (string.IsNullOrWhiteSpace(brief)) throw new ArgumentException("Write a brief first.");
            var draft = GraphNode.Create(NodeKind.Text, 60, y); draft.Settings["text"] = brief; nodes.Add(draft);
            source = GraphNode.Create(NodeKind.Collect, 360, y);
            edges.Add(new(Guid.NewGuid(), draft.Id, "text", source.Id, "a")); output = "items";
        }
        nodes.Add(source);
        var variations = GraphNode.Create(NodeKind.Variations, 660, y);
        variations.Settings["count"] = count.ToString(); variations.Settings["instructions"] = instructions;
        var prepare = GraphNode.Create(NodeKind.PrepareImage, 960, y);
        prepare.Settings["profiles"] = profiles; prepare.Settings["width"] = width.ToString(); prepare.Settings["height"] = height.ToString(); prepare.Settings["seeds"] = seeds;
        var image = GraphNode.Create(NodeKind.Image, 1260, y);
        var select = GraphNode.Create(NodeKind.Select, 1560, y, ArtifactKind.Image);
        nodes.AddRange([variations, prepare, image, select]);
        edges.AddRange([new(Guid.NewGuid(), source.Id, output, variations.Id, "items"),
            new(Guid.NewGuid(), variations.Id, "texts", prepare.Id, "texts"), new(Guid.NewGuid(), prepare.Id, "specs", image.Id, "prompt"),
            new(Guid.NewGuid(), image.Id, "outputs", select.Id, "items")]);
        commands.ApplyGraph(graph with { Nodes = [.. graph.Nodes, .. nodes], Edges = [.. graph.Edges, .. edges] });
        return select.Id;
    }

    // Branch runs reuse an explicit completed selection by identity. Previous rounds stay in the project and run history.
    public static ProjectDocument ExecutionBranch(ProjectDocument project, Guid root, Guid[] chosen)
    {
        var graph = project.Graph; var reachable = new HashSet<Guid> { root }; var queue = new Queue<Guid>(); queue.Enqueue(root);
        while (queue.TryDequeue(out var id)) foreach (var e in graph.Edges.Where(e => e.SourceNode == id)) if (reachable.Add(e.TargetNode)) queue.Enqueue(e.TargetNode);
        var nodes = graph.Nodes.Where(n => reachable.Contains(n.Id)).Select(n => n.Id == root
            ? n with { Ports = n.Ports.Where(p => !p.IsInput).ToArray(), Settings = new(n.Settings) { ["artifactIds"] = string.Join(',', chosen) } } : n).ToArray();
        return project with { Graph = new(Guid.NewGuid(), nodes, graph.Edges.Where(e => reachable.Contains(e.SourceNode) && reachable.Contains(e.TargetNode)).ToArray(), graph.Id) };
    }
}
