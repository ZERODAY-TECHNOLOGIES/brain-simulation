using System.Text.Json;
using BrainSim.Engine.Config;

namespace BrainSim.Engine.Memory;

/// <summary>
/// memory.embedding.json: the embedding model and prefix the vectors in memory.bin came from. When it differs from
/// the current setup, memory is embedded again (D-39). memory.bin itself keeps its format.
/// </summary>
public sealed record EmbeddingStamp(string Model, string Prefix)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true };

    public static EmbeddingStamp From(LlmConfig llm) => new(llm.EmbeddingModel, llm.EmbeddingPrefix);

    /// <summary>The stamp, or null when there is none or it cannot be read: then memory counts as made by the current model.</summary>
    public static EmbeddingStamp? Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<EmbeddingStamp>(File.ReadAllText(path), Json) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Json));

    /// <summary>For messages: the model, and the prefix when there is one, so a change of prefix alone shows.</summary>
    public override string ToString() => Prefix.Length > 0 ? $"{Model} with prefix \"{Prefix.TrimEnd()}\"" : Model;
}
