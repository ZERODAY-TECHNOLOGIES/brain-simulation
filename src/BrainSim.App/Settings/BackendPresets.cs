namespace BrainSim.App.Settings;

/// <summary>A chat server as most people run it.</summary>
public sealed record ChatPreset(string Name, string BaseUrl)
{
    public override string ToString() => Name;
}

/// <summary>An embedding server with the model and task prefix it usually serves.</summary>
public sealed record EmbeddingPreset(string Name, string BaseUrl, string Model, string Prefix)
{
    public override string ToString() => Name;
}

/// <summary>The usual addresses of the common OpenAI-compatible vendors, so Settings is not typed by hand (D-39).</summary>
public static class BackendPresets
{
    public static readonly IReadOnlyList<ChatPreset> Chat =
    [
        new("LM Studio", "http://localhost:1234/v1/"),
        new("Ollama", "http://localhost:11434/v1/"),
        new("OpenAI", "https://api.openai.com/v1/"),
        new("OpenRouter", "https://openrouter.ai/api/v1/"),
    ];

    /// <summary>nomic-embed wants a task prefix; OpenAI's models take none.</summary>
    public static readonly IReadOnlyList<EmbeddingPreset> Embedding =
    [
        new("LM Studio", "http://localhost:1234/v1/", "text-embedding-nomic-embed-text-v1.5", "classification: "),
        new("Ollama", "http://localhost:11434/v1/", "nomic-embed-text", "classification: "),
        new("OpenAI", "https://api.openai.com/v1/", "text-embedding-3-small", ""),
    ];

    /// <summary>
    /// The embedding fields a preset gives. On the chat server itself the embedding server stays empty, so the chat key
    /// is used; anywhere else it is named, and gets the embedding key.
    /// </summary>
    public static (string Url, string Model, string Prefix) Fill(EmbeddingPreset preset, string chatUrl) =>
        (SameServer(preset.BaseUrl, chatUrl) ? "" : preset.BaseUrl, preset.Model, preset.Prefix);

    private static bool SameServer(string a, string b) =>
        string.Equals(a.Trim().TrimEnd('/'), b.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
}
