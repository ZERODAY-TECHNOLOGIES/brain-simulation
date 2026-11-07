using BrainSim.Engine.Appraisal;
using BrainSim.Engine.Config;
using BrainSim.Engine.Hosting;
using BrainSim.Engine.Journal;
using BrainSim.Engine.Llm;
using BrainSim.Engine.Memory;

namespace BrainSim.Engine.Tests;

/// <summary>When the embedding model changes, memory is embedded again with the new one (D-39).</summary>
public sealed class MemoryReembedTests : IDisposable
{
    private static readonly EngineConfig Config = EngineConfig.LoadDefault();
    private static readonly DateTime Then = new(2026, 11, 1, 10, 0, 0);
    private readonly string _dir = Directory.CreateTempSubdirectory("brainsim-reembed").FullName;
    private readonly List<string> _embedded = [];

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string StampPath => Path.Combine(_dir, "memory.embedding.json");

    /// <summary>A memory made with an older, two-dimensional model: one fact and one episode.</summary>
    private void SeedMemory(EmbeddingStamp? stamp)
    {
        MemoryFile.Save(Path.Combine(_dir, "memory.bin"),
        [
            new Chunk { Id = 1, Kind = ChunkKind.Episode, Text = "hello there\nHi! How can I help?", Embedding = [1, 0], Created = Then, Presentations = [new(Then, 1)] },
            new Chunk { Id = 2, Kind = ChunkKind.Fact, Text = "The user's sister is called Ilinca.", Embedding = [0, 1], Created = Then, Presentations = [new(Then, 1)], EpisodeId = 1 },
        ]);
        stamp?.Save(StampPath);
    }

    private EngineHost Host(bool embeddingsDown = false)
    {
        var fake = new FakeLmStudio(5)
        {
            EmbeddingsDown = embeddingsDown,
            Embed = text =>
            {
                lock (_embedded)
                {
                    _embedded.Add(text);
                }

                return [0.5f, 0.5f, 0.5f, 0.5f];
            },
        };
        var llm = new LmStudioClient(new HttpClient(fake) { BaseAddress = new Uri(Config.Llm.BaseUrl) }, Config.Llm);
        return new EngineHost(Config, llm, new Dictionary<string, LinearProbe>(), _dir);
    }

    [Fact]
    public async Task Memory_made_with_another_model_is_embedded_again_and_stamped()
    {
        SeedMemory(new EmbeddingStamp("old-model", ""));
        await using var host = Host();

        await host.StartAsync();
        await host.Reembedding.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.All(host.Agent.Memory.Snapshot(), c => Assert.Equal(4, c.Embedding!.Length));
        var prefix = Config.Llm.EmbeddingPrefix;
        Assert.Equal([prefix + "The user's sister is called Ilinca.", prefix + "hello there"], _embedded.Order(StringComparer.Ordinal)); // an episode is embedded from its message
        Assert.False(host.Agent.Memory.Stale);
        Assert.Equal(EmbeddingStamp.From(Config.Llm), EmbeddingStamp.Load(StampPath));
        Assert.All(MemoryFile.Load(Path.Combine(_dir, "memory.bin")), c => Assert.Equal(4, c.Embedding!.Length));
        Assert.Contains(host.Events.Snapshot(), e => e.Kind == EventKinds.Memory && e.Text.StartsWith("Memory re-embedded", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_memory_without_a_stamp_is_taken_as_made_by_the_current_model()
    {
        SeedMemory(stamp: null);
        await using var host = Host();

        await host.StartAsync();
        await host.Reembedding;

        Assert.Empty(_embedded);
        Assert.False(host.Agent.Memory.Stale);
        Assert.Equal(EmbeddingStamp.From(Config.Llm), EmbeddingStamp.Load(StampPath));
    }

    [Fact]
    public async Task A_memory_of_the_current_model_is_left_alone()
    {
        SeedMemory(EmbeddingStamp.From(Config.Llm));
        await using var host = Host();

        await host.StartAsync();
        await host.Reembedding;

        Assert.Empty(_embedded);
        Assert.Equal(2, host.Agent.Memory.Snapshot()[0].Embedding!.Length);
    }

    [Fact]
    public async Task When_the_embedding_server_fails_recall_stays_off_and_the_next_start_tries_again()
    {
        SeedMemory(new EmbeddingStamp("old-model", ""));
        await using var host = Host(embeddingsDown: true);

        await host.StartAsync();
        await host.Reembedding.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.True(host.Agent.Memory.Stale);
        Assert.Equal(new EmbeddingStamp("old-model", ""), EmbeddingStamp.Load(StampPath));
        Assert.Contains(host.Events.Snapshot(), e => e.Kind == EventKinds.Error && e.Text.Contains("re-embedded", StringComparison.Ordinal));
    }

    [Fact]
    public void While_stale_nothing_is_recalled_or_familiar_and_new_facts_are_not_merged_by_old_vectors()
    {
        var memory = new DeclarativeMemory(Config.Memory);
        memory.Load([new Chunk { Id = 1, Kind = ChunkKind.Fact, Text = "old", Embedding = [1, 0, 0, 0], Created = Then, Presentations = [new(Then, 5)] }]);
        var m = new MemoryModulation(0, 0, 0, 0);
        var now = Then.AddMinutes(1);
        Assert.NotEmpty(memory.RecallFacts([1, 0, 0, 0], m, now)); // a fresh, strong fact is recalled when not stale

        memory.Stale = true;

        Assert.Empty(memory.RecallFacts([1, 0, 0, 0], m, now));
        Assert.Equal(0, memory.Familiarity([1, 0, 0, 0], m, now));
        var added = memory.AddOrStrengthenFact("new", [1, 0, 0, 0], 1, m, now);
        Assert.NotEqual(1, added.Id);
    }

    [Fact]
    public void Replacing_embeddings_touches_only_the_chunks_it_has_vectors_for()
    {
        var memory = new DeclarativeMemory(Config.Memory);
        memory.Load([new Chunk { Id = 1, Kind = ChunkKind.Fact, Text = "old", Embedding = [1, 0], Created = Then }]);
        memory.Stale = true;
        var learnedMeanwhile = memory.AddFact("new", [0, 0, 1, 0], 1, new MemoryModulation(0, 0, 0, 0), Then);

        memory.ReplaceEmbeddings(new Dictionary<int, float[]> { [1] = [0, 1, 0, 0] });

        var chunks = memory.Snapshot().ToDictionary(c => c.Id);
        Assert.Equal([0, 1, 0, 0], chunks[1].Embedding!);
        Assert.Equal([0, 0, 1, 0], chunks[learnedMeanwhile.Id].Embedding!);
        Assert.False(memory.Stale);
    }
}
