using Workbench.Core;

namespace Workbench.Infrastructure;

public sealed class ModelRegistry(string path)
{
    private ModelProfile[] _profiles = [];
    private string? _textModelId;
    public string Path { get; } = System.IO.Path.GetFullPath(path);
    public ModelProfile[] Profiles => DocumentJson.Copy(_profiles);
    public void Reload()
    {
        var profiles = DocumentJson.Read<ModelProfile[]>(File.ReadAllText(Path));
        if (profiles.Length == 0 || profiles.Any(p => string.IsNullOrWhiteSpace(p.Id)) || profiles.Select(p => p.Id).Distinct().Count() != profiles.Length)
            throw new InvalidDataException("Model profile IDs must be nonempty and unique.");
        _profiles = profiles;
    }
    public ModelProfile Get(string id) => Profiles.SingleOrDefault(p => p.Id == id) ?? throw new ArgumentException($"Unknown model profile: {id}");
    public ModelProfile Qwen => Profiles.FirstOrDefault(p => p.Id == _textModelId && p.Runtime == RuntimeKind.Llama)
        ?? Profiles.FirstOrDefault(p => p.Runtime == RuntimeKind.Llama && p.Available)
        ?? Profiles.FirstOrDefault(p => p.Runtime == RuntimeKind.Llama)
        ?? throw new InvalidOperationException("Register a llama.cpp text profile in models.json.");
    public void SelectTextModel(string id)
    {
        var profile = Get(id);
        if (profile.Runtime != RuntimeKind.Llama || !profile.Available)
            throw new ArgumentException("Choose an available text model.");
        _textModelId = id;
    }
}
