using System.Text.Json;
using System.Text.Json.Serialization;

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

    public static ProbeFile? LoadDefault()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "probes.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<ProbeFile>(File.ReadAllText(path), Json) : null;
    }
}
