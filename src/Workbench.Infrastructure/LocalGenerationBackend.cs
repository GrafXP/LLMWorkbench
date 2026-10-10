using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Workbench.Core;

namespace Workbench.Infrastructure;

public sealed class LocalGenerationBackend(ModelRegistry registry, RuntimeManager runtime) : IGenerationBackend
{
    public bool UseActionEnvelope => registry.Qwen.UseActionEnvelope;
    public Task<ChatReply> CompleteAsync(ChatMessage[] messages, bool tools, Action<string>? token, CancellationToken ct) =>
        ChatAsync(messages, tools, null, 4096, token, ct);

    public async Task<string[]> VariationsAsync(string source, string instructions, int count, CancellationToken ct)
    {
        if (count is < 1 or > 12) throw new ArgumentException("Choose 1–12 variations per source.");
        var schema = JsonNode.Parse("""{"type":"object","properties":{"texts":{"type":"array","items":{"type":"string"}}},"required":["texts"],"additionalProperties":false}""")!;
        schema["properties"]!["texts"]!["minItems"] = count; schema["properties"]!["texts"]!["maxItems"] = count;
        var messages = new[] { new ChatMessage(Guid.NewGuid(), "system", "You write visual descriptions. Return only the requested JSON object. Preserve explicitly locked words and names. Semantic preservation is an instruction, not a guarantee.", DateTimeOffset.UtcNow),
            new ChatMessage(Guid.NewGuid(), "user", $"Source:\n{source}\n\nInstructions:\n{instructions}\n\nWrite exactly {count} distinct text(s), as {{\"texts\":[\"...\"]}}.", DateTimeOffset.UtcNow) };
        var reply = await ChatAsync(messages, false, schema, Math.Min(8192, 600 * count + 256), null, ct);
        var texts = JsonNode.Parse(reply.Content)?["texts"]?.AsArray().Select(n => n?.GetValue<string>() ?? "").ToArray();
        if (texts is null || texts.Length != count || texts.Any(string.IsNullOrWhiteSpace)) throw new InvalidDataException("Qwen returned an invalid variation collection.");
        return texts;
    }
    private async Task<ChatReply> ChatAsync(ChatMessage[] messages, bool tools, JsonNode? schema, int maxTokens, Action<string>? token, CancellationToken ct, bool recoverWorker = true, ModelProfile? profile = null)
    {
        profile ??= registry.Qwen;
        var useActionEnvelope = profile.UseActionEnvelope;
        var http = await runtime.EnsureAsync(profile, ct);
        var list = new JsonArray();
        foreach (var message in messages)
        {
            var m = new JsonObject { ["role"] = message.Role, ["content"] = message.Content + (message.ContextJson is { Length: > 0 } context ? "\nCurrent desktop selection: " + context : "") };
            if (message.ToolCallId is not null) m["tool_call_id"] = message.ToolCallId;
            if (message.Name is not null) m["name"] = message.Name;
            if (message.ToolCallsJson is not null) m["tool_calls"] = JsonNode.Parse(message.ToolCallsJson);
            list.Add(m);
        }
        var body = new JsonObject { ["messages"] = list, ["stream"] = true, ["max_tokens"] = maxTokens, ["temperature"] = 0.6,
            ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false } };
        if (tools && !useActionEnvelope) { body["tools"] = AgentTools.Schemas(); body["tool_choice"] = "auto"; body["parallel_tool_calls"] = false; }
        if (tools && useActionEnvelope)
        {
            // Validated fallback for runtime/template combinations without reliable native tool parsing.
            list.Insert(0, new JsonObject { ["role"] = "system", ["content"] = "Reply with JSON {content:string, actions:[{name:string, arguments:string}]}. Arguments contains encoded JSON for one of these tools: " + AgentTools.Schemas().ToJsonString() });
            schema = JsonNode.Parse("""{"type":"object","properties":{"content":{"type":"string"},"actions":{"type":"array","maxItems":4,"items":{"type":"object","properties":{"name":{"type":"string"},"arguments":{"type":"string"}},"required":["name","arguments"],"additionalProperties":false}}},"required":["content","actions"],"additionalProperties":false}""");
            // The fallback transports matched tool results as ordinary user messages.
            foreach (var m in list.OfType<JsonObject>())
            {
                if (m["role"]?.GetValue<string>() == "assistant" && m["tool_calls"] is JsonArray prior)
                    m["content"] = new JsonObject { ["content"] = m["content"]?.DeepClone(), ["actions"] = new JsonArray(prior.Select(c => (JsonNode?)new JsonObject { ["name"] = c?["function"]?["name"]?.DeepClone(), ["arguments"] = c?["function"]?["arguments"]?.DeepClone() }).ToArray()) }.ToJsonString();
                if (m["role"]?.GetValue<string>() == "tool") { m["role"] = "user"; m["content"] = "Tool result " + m["name"] + ": " + m["content"]; m.Remove("tool_call_id"); m.Remove("name"); }
                m.Remove("tool_calls");
            }
        }
        if (schema is not null) body["response_format"] = new JsonObject { ["type"] = "json_schema", ["json_schema"] = new JsonObject { ["name"] = "reply", ["strict"] = true, ["schema"] = schema.DeepClone() } };
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Qwen refused the request ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync(ct)}");
            await using var stream = await response.Content.ReadAsStreamAsync(ct); using var reader = new StreamReader(stream);
            var assembler = new ChatStreamAssembler();
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                var data = line[5..].Trim(); if (data == "[DONE]") break;
                var added = assembler.Add(data); if (added.Length > 0) token?.Invoke(added);
            }
            var reply = assembler.Finish();
            if (tools && useActionEnvelope)
            {
                var envelope = JsonNode.Parse(reply.Content)!;
                return new(envelope["content"]!.GetValue<string>(), envelope["actions"]!.AsArray().Select(a => new AgentToolCall("call_" + Guid.NewGuid().ToString("N"), a!["name"]!.GetValue<string>(), a["arguments"]!.GetValue<string>())).ToArray());
            }
            return reply;
        }
        catch (OperationCanceledException) { await runtime.UnloadAsync(); throw; }
        catch (Exception ex) when (!ct.IsCancellationRequested && runtime.ManagedPid is null && ex is HttpRequestException or IOException)
        {
            // No tool has been dispatched from an unfinished completion. One fresh worker can safely retry it.
            var detail = runtime.Tail();
            await runtime.UnloadAsync();
            if (!recoverWorker) throw new InvalidOperationException("Qwen worker exited while completing the request. " + detail, ex);
            return await ChatAsync(messages, tools, schema, maxTokens, token, ct, recoverWorker: false, profile);
        }
    }

    public async Task<byte[]> RenderAsync(ImageSpecification specification, long seed, Action<string>? progress, CancellationToken ct)
    {
        var http = await runtime.EnsureAsync(specification.Profile, ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromMinutes(30));
        try
        {
            using var accepted = await http.PostAsJsonAsync("/sdcpp/v1/img_gen", new
            {
                prompt = specification.Prompt, negative_prompt = specification.Negative, width = specification.Width, height = specification.Height,
                seed, batch_count = 1, sample_params = new { sample_steps = specification.Steps, guidance = new { txt_cfg = specification.Cfg } }, output_format = "png"
            }, timeout.Token);
            var body = await accepted.Content.ReadAsStringAsync(timeout.Token);
            if (!accepted.IsSuccessStatusCode) throw new HttpRequestException($"Image request rejected: {body}");
            var id = JsonNode.Parse(body)?["id"]?.GetValue<string>() ?? throw new InvalidDataException("sd-server returned no job ID.");
            while (true)
            {
                await Task.Delay(500, timeout.Token);
                using var response = await http.GetAsync($"/sdcpp/v1/jobs/{Uri.EscapeDataString(id)}", timeout.Token);
                response.EnsureSuccessStatusCode(); var job = JsonNode.Parse(await response.Content.ReadAsStringAsync(timeout.Token))!;
                var status = job["status"]?.GetValue<string>();
                progress?.Invoke($"{status} · {job["progress"]?.ToJsonString() ?? "backend has no measured progress"}");
                if (status == "completed")
                {
                    var encoded = job["result"]?["images"]?[0]?["b64_json"]?.GetValue<string>() ?? throw new InvalidDataException("sd-server completed without an image.");
                    var png = Convert.FromBase64String(encoded); runtime.ModelLoaded(specification.Profile); return png;
                }
                if (status is "failed" or "cancelled") throw new InvalidOperationException(job["error"]?["message"]?.GetValue<string>() ?? $"Image generation {status}: {runtime.Tail()}");
            }
        }
        catch { await runtime.UnloadAsync(); throw; }
    }
}

/// <summary>Assembles fragmented OpenAI-compatible tool names, IDs, and JSON arguments by stream index.</summary>
public sealed class ChatStreamAssembler
{
    private readonly StringBuilder _content = new();
    private readonly SortedDictionary<int, (StringBuilder Id, StringBuilder Name, StringBuilder Arguments)> _calls = new();
    private string? _finish;
    public string Add(string payload)
    {
        var root = JsonNode.Parse(payload)!;
        if (root["error"] is { } error) throw new InvalidDataException("Qwen stream error: " + error);
        var choice = root["choices"]?.AsArray().FirstOrDefault(); if (choice is null) return "";
        _finish = choice["finish_reason"]?.GetValue<string>() ?? _finish;
        var delta = choice["delta"]; var text = delta?["content"]?.GetValue<string>() ?? ""; _content.Append(text);
        if (_content.Length > 100_000) throw new InvalidDataException("Qwen reply exceeds the supported size.");
        if (delta?["tool_calls"] is JsonArray calls) foreach (var call in calls)
        {
            var index = call?["index"]?.GetValue<int>() ?? 0;
            if (index is < 0 or > 7) throw new InvalidDataException("Too many tool calls.");
            if (!_calls.TryGetValue(index, out var fields)) _calls[index] = fields = (new(), new(), new());
            fields.Id.Append(call?["id"]?.GetValue<string>()); fields.Name.Append(call?["function"]?["name"]?.GetValue<string>());
            fields.Arguments.Append(call?["function"]?["arguments"]?.GetValue<string>());
            if (fields.Arguments.Length > 100_000) throw new InvalidDataException("Tool arguments exceed the supported size.");
        }
        return text;
    }
    public ChatReply Finish()
    {
        if (_finish is not ("stop" or "tool_calls")) throw new InvalidDataException($"Qwen completion was interrupted or exceeded its token budget ({_finish ?? "no finish marker"}).");
        return new(_content.ToString(), _calls.Values.Select(c => new AgentToolCall(c.Id.Length > 0 ? c.Id.ToString() : "call_" + Guid.NewGuid().ToString("N"), c.Name.ToString(), c.Arguments.ToString())).ToArray());
    }
}
