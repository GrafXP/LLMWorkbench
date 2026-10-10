namespace Workbench.Core;

public enum RuntimeKind { Llama, StableDiffusion }
public sealed record ModelProfile(string Id, string Name, RuntimeKind Runtime, string Executable,
    Dictionary<string, string> Files, string[] Arguments, int Steps = 4, double Cfg = 1,
    string PromptGuide = "", string Notes = "", long EstimatedRamBytes = 0, long EstimatedVramBytes = 0, bool UseActionEnvelope = false)
{
    public string[] MissingFiles => new[] { Executable }.Concat(Files.Values).Where(p => !File.Exists(p)).ToArray();
    public bool Available => MissingFiles.Length == 0;
}
public sealed record ImageSpecification(string Prompt, string Negative, ModelProfile Profile, int Width, int Height, int Steps, double Cfg, long[] Seeds);
public sealed record RenderItem(Guid Id, Guid JobId, Guid SpecificationId, ImageSpecification Specification, long Seed,
    JobState State = JobState.Queued, Guid? ArtifactId = null, int Attempt = 0, string Message = "Queued",
    DateTimeOffset? StartedUtc = null, DateTimeOffset? FinishedUtc = null);
public sealed record ChatMessage(Guid Id, string Role, string Content, DateTimeOffset CreatedUtc, string? ToolCallId = null, string? Name = null, string? ToolCallsJson = null, string? ContextJson = null);
public sealed record AgentToolCall(string Id, string Name, string Arguments);
public sealed record ChatReply(string Content, AgentToolCall[] ToolCalls);
public interface IChatBackend
{
    Task<ChatReply> CompleteAsync(ChatMessage[] messages, bool tools, Action<string>? token, CancellationToken ct);
}
public interface IGenerationBackend : IChatBackend
{
    Task<string[]> VariationsAsync(string source, string instructions, int count, CancellationToken ct);
    Task<byte[]> RenderAsync(ImageSpecification specification, long seed, Action<string>? progress, CancellationToken ct);
}
public static class BatchPlan
{
    public static long[] Seeds(string value)
    {
        var seeds = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(s => long.TryParse(s, out var seed) && seed >= -1 ? seed : throw new ArgumentException("Seeds must be integers, or -1 for random.")).ToArray();
        if (seeds.Length is < 1 or > 16) throw new ArgumentException("Choose 1–16 seeds.");
        return seeds.Select(s => s == -1 ? Random.Shared.NextInt64(0, int.MaxValue) : s).ToArray();
    }
    public static int Count(int texts, int models, int seeds)
    {
        var count = checked(texts * models * seeds);
        if (count is < 1 or > 256) throw new ArgumentException("A batch must contain 1–256 images.");
        return count;
    }
    public static int Dimension(string value)
    {
        if (!int.TryParse(value, out var number) || number is < 256 or > 1536 || number % 64 != 0)
            throw new ArgumentException("Image dimensions must be multiples of 64 between 256 and 1536.");
        return number;
    }
}
