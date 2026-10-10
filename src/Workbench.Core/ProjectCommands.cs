namespace Workbench.Core;

/// <summary>Shared graph commands for the desktop and a future agent dispatcher. Edits never execute jobs.</summary>
public sealed class ProjectCommands
{
    private readonly Stack<ProjectDocument> _undo = new();
    private readonly Stack<ProjectDocument> _redo = new();
    private ProjectDocument _current;
    public ProjectCommands(ProjectDocument document) => _current = DocumentJson.Copy(document);
    // Do not expose mutable arrays/settings owned by the command history.
    public ProjectDocument Current => DocumentJson.Copy(_current);
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public event Action? Changed;

    public void Add(GraphNode node) => Edit(g => g with { Nodes = [.. g.Nodes, DocumentJson.Copy(node)] });
    public void Remove(Guid id) => Edit(g => g with { Nodes = g.Nodes.Where(n => n.Id != id).ToArray(), Edges = g.Edges.Where(e => e.SourceNode != id && e.TargetNode != id).ToArray() });
    public void Update(GraphNode node) => Edit(g => g with { Nodes = g.Nodes.Select(n => n.Id == node.Id ? DocumentJson.Copy(node) : n).ToArray() });
    public void Disconnect(Guid id) => Edit(g => g with { Edges = g.Edges.Where(e => e.Id != id).ToArray() });
    public void Connect(Guid source, string output, Guid target, string input) => Edit(g => g with
    {
        Edges = [.. g.Edges.Where(e => e.TargetNode != target || e.TargetPort != input), new GraphEdge(Guid.NewGuid(), source, output, target, input)]
    });
    public void ApplyGraph(GraphRevision graph) => Edit(_ => DocumentJson.Copy(graph));
    public void Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A project needs a name.", nameof(name));
        Commit(_current with { Name = name.Trim(), ModifiedUtc = DateTimeOffset.UtcNow });
    }
    private void Edit(Func<GraphRevision, GraphRevision> edit)
    {
        var next = edit(_current.Graph) with { Id = Guid.NewGuid() };
        GraphValidator.Validate(next);
        Commit(_current with { Graph = next, ModifiedUtc = DateTimeOffset.UtcNow });
    }
    private void Commit(ProjectDocument next)
    {
        _undo.Push(_current);
        _current = next;
        _redo.Clear();
        Changed?.Invoke();
    }
    public void Undo()
    {
        if (!_undo.TryPop(out var previous)) return;
        _redo.Push(_current); _current = previous; Changed?.Invoke();
    }
    public void Redo()
    {
        if (!_redo.TryPop(out var next)) return;
        _undo.Push(_current); _current = next; Changed?.Invoke();
    }
}
