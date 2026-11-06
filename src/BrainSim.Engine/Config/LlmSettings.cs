using System.Security.Cryptography;
using System.Text.Json;

namespace BrainSim.Engine.Config;

/// <summary>
/// The LLM backend chosen in the Settings dialog. It replaces the llm section of config.yaml at startup,
/// so any OpenAI-compatible server works (LM Studio, Ollama, llama.cpp, vLLM, a hosted API). See D-35.
/// </summary>
public sealed record LlmSettings(
    string BaseUrl, string EmbeddingBaseUrl, string ChatModel, string EmbeddingModel, string EmbeddingPrefix, string ApiKey,
    string EmbeddingApiKey = "")
{
    public static LlmSettings From(LlmConfig llm) =>
        new(llm.BaseUrl, llm.EmbeddingBaseUrl, llm.ChatModel, llm.EmbeddingModel, llm.EmbeddingPrefix, llm.ApiKey, llm.EmbeddingApiKey);

    public LlmConfig ApplyTo(LlmConfig llm) => llm with
    {
        BaseUrl = WithSlash(BaseUrl),
        EmbeddingBaseUrl = string.IsNullOrWhiteSpace(EmbeddingBaseUrl) ? "" : WithSlash(EmbeddingBaseUrl),
        ChatModel = ChatModel.Trim(),
        EmbeddingModel = EmbeddingModel.Trim(),
        EmbeddingPrefix = EmbeddingPrefix,
        ApiKey = ApiKey.Trim(),
        EmbeddingApiKey = EmbeddingApiKey.Trim(),
    };

    /// <summary>What is wrong with these settings, for the dialog, or null when they can be saved.</summary>
    public string? Problem() =>
        !IsHttp(BaseUrl) ? "The server address must start with http:// or https://."
        : !string.IsNullOrWhiteSpace(EmbeddingBaseUrl) && !IsHttp(EmbeddingBaseUrl) ? "The embedding server address must start with http:// or https://, or be empty."
        : string.IsNullOrWhiteSpace(ChatModel) ? "The chat model is required."
        : string.IsNullOrWhiteSpace(EmbeddingModel) ? "The embedding model is required."
        : null;

    // Without the slash, "http://host/v1" + "models" resolves to "http://host/models".
    private static string WithSlash(string url) => url.Trim().TrimEnd('/') + "/";

    private static bool IsHttp(string url) =>
        Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}

/// <summary>
/// settings.json in the data directory. The API key is stored only as <paramref name="protect"/> leaves it
/// (DPAPI in the app), never in clear.
/// </summary>
public static class LlmSettingsFile
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        RespectRequiredConstructorParameters = true, // a field missing or null reads as a corrupt file
        RespectNullableAnnotations = true,
    };

    public static void Save(string path, LlmSettings settings, Func<string, string> protect)
    {
        string Protected(string key) => key.Length > 0 ? protect(key) : "";
        var stored = new Stored(settings.BaseUrl, settings.EmbeddingBaseUrl, settings.ChatModel, settings.EmbeddingModel,
            settings.EmbeddingPrefix, Protected(settings.ApiKey), Protected(settings.EmbeddingApiKey));
        File.WriteAllText(path, JsonSerializer.Serialize(stored, Json));
    }

    /// <summary>
    /// The saved settings, or null when there are none. A file that cannot be read is set aside as settings.json.bad,
    /// so the app starts on config.yaml; a key that cannot be decrypted (another Windows user) is dropped.
    /// </summary>
    public static LlmSettings? Load(string path, Func<string, string> unprotect)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        Stored? stored;
        try
        {
            stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(path), Json);
        }
        catch (JsonException)
        {
            stored = null;
        }

        if (stored is null)
        {
            File.Move(path, path + ".bad", overwrite: true);
            return null;
        }

        return new LlmSettings(stored.BaseUrl, stored.EmbeddingBaseUrl, stored.ChatModel, stored.EmbeddingModel,
            stored.EmbeddingPrefix, Key(stored.ApiKeyProtected, unprotect), Key(stored.EmbeddingApiKeyProtected, unprotect));
    }

    private static string Key(string protectedKey, Func<string, string> unprotect)
    {
        if (protectedKey.Length == 0)
        {
            return "";
        }

        try
        {
            return unprotect(protectedKey);
        }
        catch (CryptographicException)
        {
            return "";
        }
    }

    /// <summary>The embedding key came with D-39: a file from before it has none, and still loads.</summary>
    private sealed record Stored(
        string BaseUrl, string EmbeddingBaseUrl, string ChatModel, string EmbeddingModel, string EmbeddingPrefix, string ApiKeyProtected,
        string EmbeddingApiKeyProtected = "");
}
