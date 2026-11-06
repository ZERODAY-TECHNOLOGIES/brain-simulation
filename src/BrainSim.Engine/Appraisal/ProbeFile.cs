using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BrainSim.Engine.Config;

namespace BrainSim.Engine.Appraisal;

/// <summary>probes.json: the probes that passed the AUC gate, with the embedding setup they were trained on.</summary>
public sealed record ProbeFile(string EmbeddingModel, string EmbeddingPrefix, DateTime TrainedAt, Dictionary<string, LinearProbe> Probes)
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>The probes.json shipped next to the binaries, or null when there is none.</summary>
    public static ProbeFile? LoadDefault() => Load(Path.Combine(AppContext.BaseDirectory, "probes.json"));

    /// <summary>Probe weights only mean something for the embedding model and prefix they were trained on.</summary>
    public bool Fits(LlmConfig llm) => EmbeddingModel == llm.EmbeddingModel && EmbeddingPrefix == llm.EmbeddingPrefix;

    /// <summary>A file name for one embedding model and prefix: the model, readable, and a hash of the prefix.</summary>
    public static string Slug(LlmConfig llm) => Slug(llm.EmbeddingModel, llm.EmbeddingPrefix);

    private static string Slug(string model, string prefix) =>
        $"{string.Concat(model.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' ? c : '_'))}-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(prefix)))[..8].ToLowerInvariant()}";

    /// <summary>Saves into <paramref name="dir"/> (the data folder's probes), replacing an earlier training for the same setup.</summary>
    public void SaveTo(string dir)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, Slug(EmbeddingModel, EmbeddingPrefix) + ".json"), JsonSerializer.Serialize(this, Json));
    }

    /// <summary>
    /// The probes for this embedding setup: retrained ones in <paramref name="dir"/> first, then the shipped probes.json,
    /// or null when none fit (D-39).
    /// </summary>
    public static ProbeFile? Pick(string dir, LlmConfig llm) =>
        Load(Path.Combine(dir, Slug(llm) + ".json")) is { } retrained && retrained.Fits(llm) ? retrained
        : LoadDefault() is { } shipped && shipped.Fits(llm) ? shipped
        : null;

    public static ProbeFile? Load(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<ProbeFile>(File.ReadAllText(path), Json) : null;
}
