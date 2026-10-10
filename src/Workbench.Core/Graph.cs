using System.Text.Json;
using System.Text.Json.Serialization;

namespace Workbench.Core;

public enum ArtifactKind { Text, ImageSpecification, SpeechSpecification, MusicSpecification, Image, SpeechAudio, MusicAudio }
public enum Cardinality { Single, Collection }
public enum NodeKind { Text, Collect, Select, Image, Speech, Music, GenerateText, Variations, PrepareImage, Branch }
public enum JobState { Queued, WaitingForInput, Running, AwaitingSelection, Done, Failed, Cancelled, Interrupted, Blocked }
public enum ResourceKind { Cpu, Gpu }

public sealed record Port(string Id, string Name, ArtifactKind Type, Cardinality Cardinality, bool IsInput, bool Required = true);
public sealed record GraphNode(Guid Id, NodeKind Kind, string Title, double X, double Y, Port[] Ports, Dictionary<string, string> Settings)
{
    public const double Width = 260;
    public const double Height = 164;
    public static GraphNode Create(NodeKind kind, double x = 0, double y = 0, ArtifactKind type = ArtifactKind.Text) => kind switch
    {
        NodeKind.Text => new(Guid.NewGuid(), kind, "Text draft", x, y,
            [new("text", "Text", ArtifactKind.Text, Cardinality.Single, false)], new() { ["text"] = "" }),
        NodeKind.Collect => new(Guid.NewGuid(), kind, "Collect texts", x, y,
            [new("a", "Text 1", type, Cardinality.Single, true),
             new("b", "Text 2", type, Cardinality.Single, true, false),
             new("c", "Text 3", type, Cardinality.Single, true, false),
             new("items", "Texts", type, Cardinality.Collection, false)], new()),
        NodeKind.Select => new(Guid.NewGuid(), kind, "Choose favourites", x, y,
            [new("items", "Candidates", type, Cardinality.Collection, true),
             new("chosen", "Selected", type, Cardinality.Collection, false)], new()),
        NodeKind.GenerateText => new(Guid.NewGuid(), kind, "Write with Qwen", x, y,
            [new("source", "Brief", ArtifactKind.Text, Cardinality.Single, true), new("text", "Text", ArtifactKind.Text, Cardinality.Single, false)],
            new() { ["instructions"] = "Write an image description from this brief." }),
        NodeKind.Variations => new(Guid.NewGuid(), kind, "Text variations", x, y,
            [new("items", "Source texts", ArtifactKind.Text, Cardinality.Collection, true), new("texts", "Variations", ArtifactKind.Text, Cardinality.Collection, false)],
            new() { ["count"] = "6", ["instructions"] = "Vary composition, lighting and atmosphere; preserve the main subject." }),
        NodeKind.PrepareImage => new(Guid.NewGuid(), kind, "Prepare image prompts", x, y,
            [new("texts", "Texts", ArtifactKind.Text, Cardinality.Collection, true), new("specs", "Image prompts", ArtifactKind.ImageSpecification, Cardinality.Collection, false)],
            new() { ["profiles"] = "klein4b", ["width"] = "512", ["height"] = "512", ["seeds"] = "42", ["negative"] = "" }),
        NodeKind.Image => new(Guid.NewGuid(), kind, "Render image batch", x, y,
            [new("prompt", "Image prompts", ArtifactKind.ImageSpecification, Cardinality.Collection, true), new("outputs", "Images", ArtifactKind.Image, Cardinality.Collection, false)], new()),
        NodeKind.Branch => new(Guid.NewGuid(), kind, "Branch from favourites", x, y,
            [new("chosen", "Selected images", ArtifactKind.Image, Cardinality.Collection, true), new("texts", "Source texts", ArtifactKind.Text, Cardinality.Collection, false)],
            new() { ["feedback"] = "" }),
        _ => new(Guid.NewGuid(), kind, $"Generate {kind.ToString().ToLowerInvariant()}", x, y,
            [new("prompt", "Specification", kind switch { NodeKind.Image => ArtifactKind.ImageSpecification, NodeKind.Speech => ArtifactKind.SpeechSpecification, _ => ArtifactKind.MusicSpecification }, Cardinality.Single, true),
             new("outputs", "Outputs", kind switch { NodeKind.Image => ArtifactKind.Image, NodeKind.Speech => ArtifactKind.SpeechAudio, _ => ArtifactKind.MusicAudio }, Cardinality.Collection, false)], new())
    };
}
public sealed record GraphEdge(Guid Id, Guid SourceNode, string SourcePort, Guid TargetNode, string TargetPort);
public sealed record GraphRevision(Guid Id, GraphNode[] Nodes, GraphEdge[] Edges, Guid? SourceRevisionId = null)
{
    public static GraphRevision Empty => new(Guid.NewGuid(), [], []);
}
public sealed record ProjectDocument(Guid Id, string Name, GraphRevision Graph, DateTimeOffset ModifiedUtc)
{
    public static ProjectDocument Create(string name = "Untitled project") => new(Guid.NewGuid(), name, GraphRevision.Empty, DateTimeOffset.UtcNow);
}

public static class DocumentJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Read<T>(string value) => JsonSerializer.Deserialize<T>(value, Options) ?? throw new InvalidDataException("The saved document is empty.");
    public static T Copy<T>(T value) => Read<T>(Write(value));
}

public sealed class GraphValidationException(string message) : InvalidOperationException(message);

public static class GraphValidator
{
    public static GraphNode[] Validate(GraphRevision graph, bool requireInputs = false)
    {
        if (graph.Nodes.Select(n => n.Id).Distinct().Count() != graph.Nodes.Length)
            throw new GraphValidationException("Node IDs must be unique.");
        if (graph.Edges.Select(e => e.Id).Distinct().Count() != graph.Edges.Length)
            throw new GraphValidationException("Connection IDs must be unique.");
        var nodes = graph.Nodes.ToDictionary(n => n.Id);
        foreach (var node in graph.Nodes)
        {
            if (!double.IsFinite(node.X) || !double.IsFinite(node.Y)) throw new GraphValidationException("Node positions must be finite.");
            if (node.Ports.Select(p => p.Id).Distinct().Count() != node.Ports.Length)
                throw new GraphValidationException($"{node.Title} has duplicate port IDs.");
        }
        var targets = new HashSet<(Guid, string)>();
        foreach (var edge in graph.Edges)
        {
            if (!nodes.TryGetValue(edge.SourceNode, out var source) || !nodes.TryGetValue(edge.TargetNode, out var target))
                throw new GraphValidationException("A connection refers to a missing node.");
            var output = source.Ports.SingleOrDefault(p => p.Id == edge.SourcePort && !p.IsInput);
            var input = target.Ports.SingleOrDefault(p => p.Id == edge.TargetPort && p.IsInput);
            if (output is null || input is null) throw new GraphValidationException("Connections must run from an output to an input.");
            if (output.Type != input.Type || output.Cardinality != input.Cardinality)
                throw new GraphValidationException($"Cannot connect {output.Type} {output.Cardinality} to {input.Type} {input.Cardinality}.");
            if (!targets.Add((edge.TargetNode, edge.TargetPort))) throw new GraphValidationException("An input accepts one connection.");
        }
        if (requireInputs)
            foreach (var node in graph.Nodes)
                foreach (var port in node.Ports.Where(p => p.IsInput && p.Required))
                    if (!targets.Contains((node.Id, port.Id))) throw new GraphValidationException($"Connect {node.Title}: {port.Name} before running.");
        var pending = graph.Nodes.ToDictionary(n => n.Id, n => graph.Edges.Count(e => e.TargetNode == n.Id));
        var ready = new Queue<GraphNode>(graph.Nodes.Where(n => pending[n.Id] == 0));
        var sorted = new List<GraphNode>();
        while (ready.TryDequeue(out var node))
        {
            sorted.Add(node);
            foreach (var edge in graph.Edges.Where(e => e.SourceNode == node.Id))
                if (--pending[edge.TargetNode] == 0) ready.Enqueue(nodes[edge.TargetNode]);
        }
        if (sorted.Count != graph.Nodes.Length) throw new GraphValidationException("This connection would create a cycle. Create a new round instead.");
        return [.. sorted];
    }
}
