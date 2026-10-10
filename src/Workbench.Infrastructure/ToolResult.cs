using System.Text.Json;
using System.Text.Json.Nodes;

namespace Workbench.Infrastructure;

public static class ToolResult
{
    public static string? Error(string json)
    {
        try { return JsonNode.Parse(json) is JsonObject result ? result["error"]?.ToString() : null; }
        catch (JsonException) { return "The saved tool result is not valid JSON."; }
    }
}
