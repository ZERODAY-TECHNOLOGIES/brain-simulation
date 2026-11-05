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

    public static ProbeFile? Load(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<ProbeFile>(File.ReadAllText(path), Json) : null;
}
