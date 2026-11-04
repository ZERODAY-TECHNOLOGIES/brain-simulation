using BrainSim.Engine.Config;
using BrainSim.Engine.Memory;

using static BrainSim.Engine.Tests.Vectors;

namespace BrainSim.Engine.Tests;

public class MemoryActivationTests
{
    private static readonly EngineConfig Config = EngineConfig.LoadDefault();
    private static readonly DateTime Now = new(2026, 11, 4, 12, 0, 0);
    private static readonly double[] Calm = Enumerable.Repeat(1.0, Substances.Count).ToArray();

    private static Chunk Fact(DeclarativeMemory memory, float[] embedding, DateTime at, string text = "a fact") =>
        memory.AddFact(text, embedding, episodeId: 0, default, at);

    [Fact]
    public void Base_level_decays_as_inverse_square_root()
    {
        var memory = new DeclarativeMemory(Config.Memory);
        var chunk = Fact(memory, Unit(1, 0), Now);

        var difference = memory.BaseLevel(chunk, Now.AddSeconds(100)) - memory.BaseLevel(chunk, Now.AddSeconds(400));

        Assert.Equal(0.5 * Math.Log(4), difference, 9);
    }

    [Fact]
    public void More_presentations_raise_base_level()
    {
        var memory = new DeclarativeMemory(Config.Memory);
        var once = Fact(memory, Unit(1, 0), Now);
        var twice = Fact(memory, Unit(0, 1), Now);
        twice.Presentations.Add(new Presentation(Now, 1));

        Assert.True(memory.BaseLevel(twice, Now.AddMinutes(5)) > memory.BaseLevel(once, Now.AddMinutes(5)));
    }

    [Fact]
    public void Spreading_is_zero_below_the_cosine_floor_and_two_at_identity()
    {
        var memory = new DeclarativeMemory(Config.Memory);
        var chunk = Fact(memory, Unit(1, 0), Now);
        var at04 = Unit(0.4f, MathF.Sqrt(1 - 0.16f)); // cosine 0.4 with (1, 0)

        Assert.Equal(0, memory.Spreading(at04, chunk), 9);
        Assert.Equal(2.0, memory.Spreading(Unit(1, 0), chunk), 9);
    }

    [Fact]
    public void Encoding_weight_follows_dopamine_and_cortisol()
    {
        var memory = new DeclarativeMemory(Config.Memory);

        Assert.Equal(1.48, memory.EncodingWeight(new MemoryModulation(0.12, 0, 0, 0)), 9);
        Assert.Equal(0.52, memory.EncodingWeight(new MemoryModulation(-0.12, 0, 0, 0)), 9);
        Assert.Equal(3.0, memory.EncodingWeight(new MemoryModulation(10, 0, 0, 0)), 9);
        Assert.Equal(0.2, memory.EncodingWeight(new MemoryModulation(-10, 0, 0, 0)), 9);
    }

    [Fact]
    public void Noise_rises_with_NE_and_threshold_with_CORT()
    {
        var memory = new DeclarativeMemory(Config.Memory);

        Assert.True(memory.NoiseScale(new MemoryModulation(0, 0.12, 0, 0)) > 0.25);
        Assert.Equal(0.25, memory.NoiseScale(default), 9);
        Assert.Equal(-2.52, memory.Threshold(new MemoryModulation(0, 0, 0.12, 0)), 9);
    }

    [Fact]
    public void Modulation_channels_map_to_memory_modulation()
    {
        var brain = new Brain(Config, Now);
        brain.Chemistry.Hold[(int)Substance.Cortisol] = 2.0;
        brain.Tick(Now.AddMilliseconds(100));

        var m = MemoryModulation.From(brain.Modulation);

        Assert.True(m.Threshold > 0, "CORT raises the retrieval threshold");
        Assert.True(m.Encoding < 0, "CORT weakens encoding");
    }

    [Fact]
    public void Recall_returns_at_most_k_facts_above_threshold_highest_first_and_strengthens_them()
    {
        var memory = new DeclarativeMemory(Config.Memory);
        var query = Unit(1, 0, 0);
        var facts = Enumerable.Range(0, 6).Select(i => Fact(memory, query, Now.AddSeconds(-60 - i * 10), $"fact {i}")).ToList();
        memory.AddEpisode("an episode", query, Calm, default, Now.AddSeconds(-30));

        var recalled = memory.RecallFacts(query, default, Now);

        Assert.Equal(4, recalled.Count);
        Assert.All(recalled, r => Assert.Equal(ChunkKind.Fact, r.Chunk.Kind));
        Assert.Equal(recalled.OrderByDescending(r => r.Activation).Select(r => r.Chunk.Id), recalled.Select(r => r.Chunk.Id));
        Assert.All(recalled, r => Assert.Equal((2, 1), (r.Chunk.Presentations.Count, r.Chunk.Recalls)));
        Assert.Equal(2, facts.Count(f => f.Recalls == 0));
    }

    [Fact]
    public void Recall_and_familiarity_need_a_cue_however_fresh_the_fact()
    {
        var memory = new DeclarativeMemory(Config.Memory);
        Fact(memory, Unit(1, 0), Now.AddSeconds(-10)); // fresh: base level far above the threshold

        var unrelated = Unit(0, 1);

        Assert.Empty(memory.RecallFacts(unrelated, default, Now));
        Assert.Equal(0, memory.Familiarity(unrelated, default, Now));
        Assert.Single(memory.RecallFacts(Unit(1, 0), default, Now));
    }

    [Fact]
    public void Empty_memory_recalls_nothing_and_is_unfamiliar()
    {
        var memory = new DeclarativeMemory(Config.Memory);

        Assert.Empty(memory.RecallFacts(Unit(1, 0), default, Now));
        Assert.Equal(0, memory.Familiarity(Unit(1, 0), default, Now));
    }

    [Fact]
    public void Noise_is_reproducible_with_a_seed()
    {
        IReadOnlyList<double> Run()
        {
            var memory = new DeclarativeMemory(Config.Memory, seed: 11);
            for (var i = 0; i < 5; i++)
            {
                Fact(memory, Unit(1, i * 0.1f), Now.AddSeconds(-100 * (i + 1)));
            }

            return [.. memory.RecallFacts(Unit(1, 0), default, Now).Select(r => r.Activation)];
        }

        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void Archive_keeps_chunks_but_drops_them_from_recall()
    {
        var memory = new DeclarativeMemory(Config.Memory with { ArchiveLimit = 10, RecallCount = 20 });
        var query = Unit(1, 0);
        var facts = Enumerable.Range(0, 15).Select(i => Fact(memory, query, Now.AddMinutes(-15 + i), $"fact {i}")).ToList();

        Assert.Equal(15, memory.Snapshot().Count);
        Assert.Equal(facts.Take(5), facts.Where(f => f.Archived)); // the five oldest are the weakest
        var recalled = memory.RecallFacts(query, default, Now);
        Assert.DoesNotContain(recalled, r => r.Chunk.Archived);
    }
}
