using BrainSim.Engine.Effectors;

namespace BrainSim.Engine.Memory;

public enum ChunkKind
{
    Episode,
    Fact,
}

/// <summary>One encoding, recall or sleep replay of a chunk. Weight comes from E4 at that moment.</summary>
public readonly record struct Presentation(DateTime Time, double Weight);

/// <summary>
/// An ACT-R chunk (docs/memory.md "Chunks"). Episodes are whole turns, with an emotional tag for dreams and a
/// salience for NREM replay; facts are short sentences extracted from an episode.
/// Mutated only on the engine loop; the UI reads copies through <see cref="DeclarativeMemory.Snapshot"/>.
/// </summary>
public sealed class Chunk
{
    public required int Id { get; init; }
    public required ChunkKind Kind { get; init; }
    public required string Text { get; init; }
    public float[]? Embedding { get; init; }
    public required DateTime Created { get; init; }
    public List<Presentation> Presentations { get; init; } = [];
    public double Tag { get; set; }
    public double Salience { get; set; }
    public int? EpisodeId { get; init; }
    public bool Archived { get; set; }
    public DateTime? LastRecalled { get; set; }
    public int Recalls { get; set; }

    public Chunk Copy() => new()
    {
        Id = Id, Kind = Kind, Text = Text, Embedding = Embedding, Created = Created, Presentations = [.. Presentations],
        Tag = Tag, Salience = Salience, EpisodeId = EpisodeId, Archived = Archived, LastRecalled = LastRecalled, Recalls = Recalls,
    };
}

/// <summary>The four E4 channels as the memory reads them (docs/memory.md "E4").</summary>
public readonly record struct MemoryModulation(double Encoding, double Noise, double Threshold, double Consolidation)
{
    public static MemoryModulation From(Modulation m) => new(
        m["memory_encoding"], m["memory_noise"], m["memory_retrieval_threshold"], m["memory_consolidation"]);
}

public sealed record Recall(Chunk Chunk, double Activation);
