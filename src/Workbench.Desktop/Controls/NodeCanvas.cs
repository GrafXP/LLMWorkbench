using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Workbench.Core;

namespace Workbench.Desktop.Controls;

/// <summary>Lightweight graph renderer adapting DataStudio2's canvas-space pan/zoom, curved wires, and minimap.</summary>
public sealed class NodeCanvas : FrameworkElement
{
    public static readonly DependencyProperty ModelProperty = DependencyProperty.Register(nameof(Model), typeof(WorkbenchViewModel), typeof(NodeCanvas), new PropertyMetadata(null, ModelChanged));
    public WorkbenchViewModel? Model { get => (WorkbenchViewModel?)GetValue(ModelProperty); set => SetValue(ModelProperty, value); }
    private static void ModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (NodeCanvas)d;
        if (e.OldValue is WorkbenchViewModel old) old.GraphChanged -= view.InvalidateVisual;
        if (e.NewValue is WorkbenchViewModel next) next.GraphChanged += view.InvalidateVisual;
        view.InvalidateVisual();
    }
    private double _zoom = .85;
    private Vector _offset = new(20, 15);
    private bool _panning;
    private Point _lastScreen;
    private Guid? _dragNode;
    private Vector _nodeGrab;
    private Point? _dragPosition;
    private (GraphNode Node, Port Port)? _wire;
    private Point _wireEnd;
    private Rect _minimap;
    private Rect _worldBounds;
    private double _mapScale;
    private static Brush Brush(string hex) { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); brush.Freeze(); return brush; }
    private static readonly Brush BackgroundBrush = Brush("#141A23"), NodeBrush = Brush("#222B38"), HeaderBrush = Brush("#2B3748"), ForegroundBrush = Brush("#E4ECF6"), MutedBrush = Brush("#94A3B8"), AccentBrush = Brush("#69B9FF"), GoldBrush = Brush("#F3C979"), GreenBrush = Brush("#64D5A5"), ErrorBrush = Brush("#EF8C8C");
    public NodeCanvas() { Focusable = true; ClipToBounds = true; }
    private Point ToWorld(Point screen) => new((screen.X - _offset.X) / _zoom, (screen.Y - _offset.Y) / _zoom);
    private Point Position(GraphNode node) => node.Id == _dragNode && _dragPosition.HasValue ? _dragPosition.Value : new(node.X, node.Y);
    private Point PortPosition(GraphNode node, Port port)
    {
        var ports = node.Ports.Where(p => p.IsInput == port.IsInput).ToArray();
        var index = Array.IndexOf(ports, port); var position = Position(node);
        return new(position.X + (port.IsInput ? 0 : GraphNode.Width), position.Y + 79 + index * 24);
    }
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(BackgroundBrush, null, new(0, 0, ActualWidth, ActualHeight));
        var gridBrush = Brush("#293444");
        var spacing = 32 * _zoom; while (spacing < 18) spacing *= 2;
        for (var x = ((_offset.X % spacing) + spacing) % spacing; x < ActualWidth; x += spacing)
            for (var y = ((_offset.Y % spacing) + spacing) % spacing; y < ActualHeight; y += spacing) dc.DrawEllipse(gridBrush, null, new(x, y), .8, .8);
        var graph = Model?.Graph;
        if (graph is null || graph.Nodes.Length == 0)
        {
            Text(dc, "Start with a text draft", new(45, 55), 22, ForegroundBrush);
            Text(dc, "Add nodes from the left panel, or open the example workflow.", new(45, 95), 13, MutedBrush);
            return;
        }
        dc.PushTransform(new MatrixTransform(_zoom, 0, 0, _zoom, _offset.X, _offset.Y));
        var visible = new Rect(ToWorld(new(0, 0)), ToWorld(new(ActualWidth, ActualHeight)));
        var nodes = graph.Nodes.ToDictionary(n => n.Id);
        foreach (var edge in graph.Edges)
        {
            if (!nodes.TryGetValue(edge.SourceNode, out var source) || !nodes.TryGetValue(edge.TargetNode, out var target)) continue;
            var output = source.Ports.Single(p => p.Id == edge.SourcePort);
            var input = target.Ports.Single(p => p.Id == edge.TargetPort);
            Wire(dc, PortPosition(source, output), PortPosition(target, input), source.Id == Model!.SelectedNodeId || target.Id == Model.SelectedNodeId ? AccentBrush : MutedBrush);
        }
        if (_wire is { } wire) Wire(dc, PortPosition(wire.Node, wire.Port), _wireEnd, AccentBrush);
        foreach (var node in graph.Nodes)
        {
            var pos = Position(node); var rect = new Rect(pos, new Size(GraphNode.Width, GraphNode.Height));
            if (!rect.IntersectsWith(visible)) continue;
            var selected = node.Id == Model!.SelectedNodeId;
            dc.DrawRoundedRectangle(NodeBrush, new(selected ? AccentBrush : Brush("#435064"), selected ? 2 : 1), rect, 9, 9);
            dc.DrawRoundedRectangle(HeaderBrush, null, new(pos, new Size(GraphNode.Width, 38)), 9, 9);
            Text(dc, node.Title, pos + new Vector(14, 10), 13, ForegroundBrush, 232);
            var snippet = node.Kind == NodeKind.Text ? node.Settings.GetValueOrDefault("text", "Write a text draft…").ReplaceLineEndings(" ") : node.Kind == NodeKind.Collect ? "Gather connected text drafts" : "Choose favourites to continue";
            Text(dc, string.IsNullOrWhiteSpace(snippet) ? "Write a text draft…" : snippet, pos + new Vector(14, 48), 10, MutedBrush, 232);
            foreach (var port in node.Ports)
            {
                var point = PortPosition(node, port);
                var color = port.Cardinality == Cardinality.Collection ? GoldBrush : AccentBrush;
                dc.DrawEllipse(color, new(BackgroundBrush, 2), point, 6, 6);
                Text(dc, port.Name + (port.Cardinality == Cardinality.Collection ? " []" : ""), new(pos.X + (port.IsInput ? 12 : 146), point.Y - 7), 10, color, 100);
            }
            var status = Model.NodeStatus(node.Id);
            var statusColor = status.StartsWith("Done", StringComparison.Ordinal) ? GreenBrush : status.StartsWith("Failed", StringComparison.Ordinal) ? ErrorBrush : status.StartsWith("Awaiting", StringComparison.Ordinal) ? GoldBrush : MutedBrush;
            Text(dc, status, pos + new Vector(14, 142), 10, statusColor, 232);
        }
        dc.Pop();
        DrawMinimap(dc, graph);
        Text(dc, $"{_zoom * 100:0}% · Drag ports to connect · Middle-drag to pan", new(12, ActualHeight - 24), 10, MutedBrush);
    }
    private void Text(DrawingContext dc, string text, Point point, double size, Brush brush, double width = 1000)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        { MaxTextWidth = Math.Max(1, width), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
        dc.DrawText(formatted, point);
    }
    private static void Wire(DrawingContext dc, Point source, Point target, Brush brush)
    {
        var dx = Math.Max(55, Math.Abs(target.X - source.X) * .5);
        var path = new StreamGeometry();
        using (var g = path.Open()) { g.BeginFigure(source, false, false); g.BezierTo(source + new Vector(dx, 0), target - new Vector(dx, 0), target, true, false); }
        path.Freeze(); dc.DrawGeometry(null, new(brush, 1.6), path);
    }
    private void DrawMinimap(DrawingContext dc, GraphRevision graph)
    {
        if (ActualWidth < 300 || ActualHeight < 180) return;
        _minimap = new(ActualWidth - 176, ActualHeight - 128, 160, 96);
        _worldBounds = new(graph.Nodes.Min(n => n.X) - 30, graph.Nodes.Min(n => n.Y) - 30,
            graph.Nodes.Max(n => n.X) - graph.Nodes.Min(n => n.X) + GraphNode.Width + 60,
            graph.Nodes.Max(n => n.Y) - graph.Nodes.Min(n => n.Y) + GraphNode.Height + 60);
        _mapScale = Math.Min(_minimap.Width / _worldBounds.Width, _minimap.Height / _worldBounds.Height);
        dc.DrawRoundedRectangle(Brush("#D91B2431"), new(MutedBrush, 1), _minimap, 4, 4);
        dc.PushClip(new RectangleGeometry(_minimap));
        foreach (var node in graph.Nodes)
            dc.DrawRectangle(node.Id == Model!.SelectedNodeId ? AccentBrush : MutedBrush, null,
                new(_minimap.X + (node.X - _worldBounds.X) * _mapScale, _minimap.Y + (node.Y - _worldBounds.Y) * _mapScale, GraphNode.Width * _mapScale, GraphNode.Height * _mapScale));
        var topLeft = ToWorld(new(0, 0));
        dc.DrawRectangle(null, new(GoldBrush, 1), new(_minimap.X + (topLeft.X - _worldBounds.X) * _mapScale, _minimap.Y + (topLeft.Y - _worldBounds.Y) * _mapScale, ActualWidth / _zoom * _mapScale, ActualHeight / _zoom * _mapScale));
        dc.Pop();
    }
    public void Fit()
    {
        if (Model?.Graph.Nodes is not { Length: > 0 } nodes) { _zoom = .85; _offset = new(20, 15); InvalidateVisual(); return; }
        var minX = nodes.Min(n => n.X); var minY = nodes.Min(n => n.Y);
        var width = nodes.Max(n => n.X) + GraphNode.Width - minX + 70;
        var height = nodes.Max(n => n.Y) + GraphNode.Height - minY + 70;
        _zoom = Math.Clamp(Math.Min(ActualWidth / width, ActualHeight / height), .15, 1.1);
        _offset = new((ActualWidth - width * _zoom) / 2 + 35 * _zoom - minX * _zoom, (ActualHeight - height * _zoom) / 2 + 35 * _zoom - minY * _zoom);
        InvalidateVisual();
    }
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var screen = e.GetPosition(this); var world = ToWorld(screen);
        _zoom = Math.Clamp(_zoom * (e.Delta > 0 ? 1.12 : 1 / 1.12), .15, 3);
        _offset = new(screen.X - world.X * _zoom, screen.Y - world.Y * _zoom);
        InvalidateVisual(); e.Handled = true;
    }
    private (GraphNode, Port)? HitPort(Point point, bool? input = null)
    {
        if (Model is null) return null;
        foreach (var node in Model.Graph.Nodes.Reverse())
            foreach (var port in node.Ports.Where(p => input is null || p.IsInput == input))
                if ((PortPosition(node, port) - point).Length < 12 / Math.Min(_zoom, 1)) return (node, port);
        return null;
    }
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        Focus(); _lastScreen = e.GetPosition(this); var world = ToWorld(_lastScreen);
        if (e.ChangedButton == MouseButton.Middle || e.ChangedButton == MouseButton.Left && Keyboard.IsKeyDown(Key.Space))
        { _panning = true; CaptureMouse(); Cursor = Cursors.Hand; e.Handled = true; return; }
        if (e.ChangedButton == MouseButton.Left && _minimap.Contains(_lastScreen) && _mapScale > 0)
        {
            var target = new Point(_worldBounds.X + (_lastScreen.X - _minimap.X) / _mapScale, _worldBounds.Y + (_lastScreen.Y - _minimap.Y) / _mapScale);
            _offset = new(ActualWidth / 2 - target.X * _zoom, ActualHeight / 2 - target.Y * _zoom); InvalidateVisual(); e.Handled = true; return;
        }
        if (e.ChangedButton == MouseButton.Left && HitPort(world, false) is { } output)
        { _wire = output; _wireEnd = world; CaptureMouse(); e.Handled = true; return; }
        var node = Model?.Graph.Nodes.Reverse().FirstOrDefault(n => new Rect(Position(n), new Size(GraphNode.Width, GraphNode.Height)).Contains(world));
        if (e.ChangedButton == MouseButton.Left)
        {
            Model?.SelectNode(node?.Id);
            if (node is not null) { _dragNode = node.Id; _nodeGrab = world - Position(node); _dragPosition = Position(node); CaptureMouse(); }
            e.Handled = true;
        }
        if (e.ChangedButton == MouseButton.Right)
        {
            Model?.SelectNode(node?.Id);
            var menu = new System.Windows.Controls.ContextMenu();
            if (node is not null) menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "Delete node", Command = Model?.RemoveCommand });
            else menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "Add text", Command = Model?.AddTextCommand });
            foreach (var edge in Model?.Graph.Edges.Where(edge => edge.TargetNode == node?.Id) ?? [])
            {
                var item = new System.Windows.Controls.MenuItem { Header = $"Disconnect {edge.TargetPort}" };
                var id = edge.Id; item.Click += (_, _) => Model?.Disconnect(id); menu.Items.Add(item);
            }
            ContextMenu = menu; menu.IsOpen = true; e.Handled = true;
        }
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        var screen = e.GetPosition(this);
        if (_panning) { _offset += screen - _lastScreen; _lastScreen = screen; }
        else if (_wire is not null) _wireEnd = ToWorld(screen);
        else if (_dragNode.HasValue) _dragPosition = ToWorld(screen) - _nodeGrab;
        else return;
        InvalidateVisual();
    }
    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (_wire is { } wire && HitPort(ToWorld(e.GetPosition(this)), true) is { } input) Model?.Connect(wire.Node.Id, wire.Port.Id, input.Item1.Id, input.Item2.Id);
        if (_dragNode is { } id && _dragPosition is { } pos)
        {
            var old = Model?.Graph.Nodes.SingleOrDefault(n => n.Id == id);
            if (old is not null && (Math.Abs(old.X - pos.X) > .5 || Math.Abs(old.Y - pos.Y) > .5)) Model?.MoveNode(id, pos.X, pos.Y);
        }
        _panning = false; _wire = null; _dragNode = null; _dragPosition = null; ReleaseMouseCapture(); Cursor = Cursors.Arrow; InvalidateVisual(); e.Handled = true;
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Delete && Model?.RemoveCommand.CanExecute(null) == true) { Model.RemoveCommand.Execute(null); e.Handled = true; }
        if (e.Key == Key.Home) { Fit(); e.Handled = true; }
        if (e.Key == Key.Escape) { _wire = null; _dragNode = null; _dragPosition = null; _panning = false; ReleaseMouseCapture(); InvalidateVisual(); }
    }
}
