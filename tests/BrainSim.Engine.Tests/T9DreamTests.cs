using BrainSim.Engine.Appraisal;
using BrainSim.Engine.Config;
using BrainSim.Engine.Dreams;
using BrainSim.Engine.Llm;
using BrainSim.Engine.Memory;
using BrainSim.Engine.Sleep;

namespace BrainSim.Engine.Tests;

/// <summary>
/// Spec T9: the same memories, nights with and without REM. Emotional tags drop only with REM, and waking from REM
/// brings the dream into context. The perplexity part needs logprobs, which LM Studio's completion endpoint does
/// not return, and the baseline-performance part needs LoRA; neither applies in reduced mode.
/// </summary>
public class T9DreamTests
{
    private static readonly EngineConfig Config = EngineConfig.LoadDefault();
    private static readonly DateTime Night = new(2026, 11, 5, 1, 0, 0);

    private static (Agent Agent, FakeLmStudio Llm, Action<DateTime> SetClock) Create(EngineConfig config)
    {
        var fake = new FakeLmStudio(replyTokens: 40);
        var client = new LmStudioClient(new HttpClient(fake) { BaseAddress = new Uri(config.Llm.BaseUrl) }, config.Llm);
        var now = Night;
        var agent = new Agent(config, client, new Dictionary<string, LinearProbe>(), () => now);

        // Five memories: two lived under stress, three calm.
        var calm = Enumerable.Repeat(1.0, Substances.Count).ToArray();
        var stressed = (double[])calm.Clone();
        stressed[(int)Substance.Norepinephrine] = 2.0;
        stressed[(int)Substance.Cortisol] = 1.5;
        agent.Memory.AddEpisode("the argument about the broken window", null, stressed, default, Night.AddHours(-5));
        agent.Memory.AddEpisode("someone shouting in the hallway", null, stressed, default, Night.AddHours(-4));
        agent.Memory.AddEpisode("a quiet walk by the lake", null, calm, default, Night.AddHours(-3));
        agent.Memory.AddEpisode("tea with a friend", null, calm, default, Night.AddHours(-2));
        agent.Memory.AddEpisode("reading about octopuses", null, calm, default, Night.AddHours(-1));

        // ADO at its limit forces sleep on the next tick, and the dark window keeps it asleep until 07:00.
        agent.Brain.Chemistry[Substance.Adenosine] = config.Sleep.ForcedAdenosine;
        return (agent, fake, t => now = t);
    }

    private static double[] Tags(Agent agent) =>
        [.. agent.Memory.Snapshot().Where(c => c.Kind == ChunkKind.Episode).OrderBy(c => c.Id).Select(c => c.Tag)];

    [Fact]
    public void Emotional_tags_drop_only_on_nights_with_REM()
    {
        var noRem = Config with { Sleep = Config.Sleep with { RemFractionStart = 0, RemFractionStep = 0 } };
        var (withRem, _, _) = Create(Config);
        var (withoutRem, _, _) = Create(noRem);
        var before = Tags(withRem);

        withRem.Brain.AdvanceTo(Night.AddHours(6.1));
        withoutRem.Brain.AdvanceTo(Night.AddHours(6.1));

        // Four REM periods have ended by 6 hours (the fourth runs from minute 329 to 360), each seeding 3 to 5
        // fragments that lose 20% of their tag.
        Assert.Equal(4, withRem.Dreamer.Journal.Count);
        Assert.All(withRem.Dreamer.Journal, d => Assert.InRange(d.Seeds.Count, 3, 5));
        var after = Tags(withRem);
        Assert.True(after.Sum() < before.Sum() * 0.8);
        for (var i = 0; i < before.Length; i++)
        {
            var uses = Math.Log(after[i] / before[i]) / Math.Log(0.8);
            Assert.Equal(Math.Round(uses), uses, 6); // every drop is a whole number of 20% steps
        }

        Assert.Empty(withoutRem.Dreamer.Journal);
        Assert.Equal(before, Tags(withoutRem));
    }

    [Fact]
    public async Task Waking_from_REM_brings_the_dream_into_context()
    {
        var ct = TestContext.Current.CancellationToken;
        var (agent, llm, setClock) = Create(Config);

        // Cycle 1 REM runs from minute 81 to 90 after onset; the dream starts once REM has settled for 30 s.
        agent.Brain.AdvanceTo(Night.AddMinutes(81).AddSeconds(35));
        Assert.Equal(SleepPhase.Rem, agent.Brain.Phase);
        Assert.NotNull(agent.Dreamer.Generation);
        await agent.Dreamer.Generation!.WaitAsync(TimeSpan.FromSeconds(10), ct);

        // REM chemistry sets the dream's character: NE near 0 gives a temperature close to 1.0.
        var request = llm.CompletionRequests.First().RootElement;
        Assert.InRange(request.GetProperty("temperature").GetDouble(), 0.95, 1.1);
        // Segments of at most segment_tokens; the dream as a whole is sized to the rest of this REM.
        Assert.Equal(Config.Dreams.SegmentTokens, request.GetProperty("max_tokens").GetInt32());

        // No dialogue shape and no instructions: clauses joined by commas, ending in the open joiner.
        var prompt = request.GetProperty("prompt").GetString()!;
        Assert.DoesNotContain("\n", prompt);
        Assert.EndsWith(Config.Dreams.Joiner, prompt);
        Assert.Contains(request.GetProperty("stop").EnumerateArray(), s => s.GetString() == "\n");

        // A minute later, tokens have been revealed at one per 2 seconds of REM.
        agent.Brain.AdvanceTo(agent.Brain.Now.AddMinutes(1));
        Assert.StartsWith("d0 d1 ", agent.Dreamer.Current!.Text);

        setClock(agent.Brain.Now);
        var reply = await agent.SendAsync("good morning", cancellationToken: ct);
        Assert.Null(reply.Signals.Reward); // the dream in context is not an earlier answer to reward

        var dream = agent.Dreamer.Journal[^1];
        Assert.True(dream.Interrupted);
        var revealed = (int)((dream.End - dream.Start).TotalSeconds / Config.Dreams.SecondsPerToken);
        Assert.Equal(revealed, dream.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.True(dream.Chemistry[(int)Substance.Norepinephrine] < 0.15);
        Assert.True(dream.Chemistry[(int)Substance.Acetylcholine] > 1.1);

        Assert.StartsWith("d0 d1", dream.Text);
        var system = llm.ChatRequests[^1].RootElement.GetProperty("messages")[0].GetProperty("content").GetString();
        Assert.Equal($"{Config.Llm.SystemPrompt}\n\nYou were dreaming when this message woke you, and you still remember the dream: {dream.Text}", system);
    }

    [Fact]
    public void The_agent_replays_its_episodes_in_NREM_sleep()
    {
        var (agent, _, _) = Create(Config);
        var replayed = new List<Chunk>();
        agent.Memory.Replayed += replayed.Add;

        agent.Brain.AdvanceTo(Night.AddMinutes(80)); // cycle 1 up to its REM: N1, N2, N3

        Assert.NotEmpty(replayed);
    }

    [Fact]
    public void With_no_memories_REM_has_nothing_to_dream_about()
    {
        var fake = new FakeLmStudio(replyTokens: 40);
        var client = new LmStudioClient(new HttpClient(fake) { BaseAddress = new Uri(Config.Llm.BaseUrl) }, Config.Llm);
        var agent = new Agent(Config, client, new Dictionary<string, LinearProbe>(), () => Night);
        var started = 0;
        agent.Dreamer.Started += _ => started++;
        agent.Brain.Chemistry[Substance.Adenosine] = Config.Sleep.ForcedAdenosine;

        agent.Brain.AdvanceTo(Night.AddMinutes(81).AddSeconds(35));

        Assert.Equal(SleepPhase.Rem, agent.Brain.Phase);
        Assert.Equal(0, started);
        Assert.Null(agent.Dreamer.Current);
        Assert.Empty(fake.CompletionRequests);
    }

    [Fact]
    public async Task A_dream_the_model_cannot_generate_is_reported()
    {
        var (agent, llm, _) = Create(Config);
        var failures = new List<string>();
        agent.Dreamer.Failed += failures.Add;
        llm.Down = true;

        agent.Brain.AdvanceTo(Night.AddMinutes(81).AddSeconds(35));
        await agent.Dreamer.Generation!.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(["The dream could not be generated: LM Studio is not running"], failures);
    }

    [Fact]
    public async Task A_dream_that_ends_with_REM_stays_in_the_journal_only()
    {
        var ct = TestContext.Current.CancellationToken;
        var (agent, llm, setClock) = Create(Config);

        agent.Brain.AdvanceTo(Night.AddMinutes(81).AddSeconds(35));
        await agent.Dreamer.Generation!.WaitAsync(TimeSpan.FromSeconds(10), ct);
        agent.Brain.AdvanceTo(Night.AddMinutes(100)); // into cycle 2, N1 and N2

        Assert.Null(agent.Dreamer.Current);
        Assert.False(agent.Dreamer.Journal.Single().Interrupted);

        setClock(agent.Brain.Now);
        await agent.SendAsync("hello", cancellationToken: ct);
        var messages = llm.ChatRequests[^1].RootElement.GetProperty("messages").EnumerateArray();
        Assert.DoesNotContain(messages, m => m.GetProperty("content").GetString()!.Contains("dream"));
    }

    [Fact]
    public async Task An_old_checkpoint_keeps_its_dream_fragments()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new FakeLmStudio(replyTokens: 40);
        var client = new LmStudioClient(new HttpClient(fake) { BaseAddress = new Uri(Config.Llm.BaseUrl) }, Config.Llm);
        var agent = new Agent(Config, client, new Dictionary<string, LinearProbe>(), () => Night);
        string[] texts = ["a red kite over the field", "the train that never came", "my grandmother's kitchen"];

        agent.Dreamer.Restore(new DreamState([.. texts.Select((t, i) => new Fragment(Night.AddHours(-3 + i), t, 0.5))], []));

        var episodes = agent.Memory.Snapshot().Where(c => c.Kind == ChunkKind.Episode).ToList();
        Assert.Equal(texts, episodes.Select(e => e.Text));
        Assert.All(episodes, e => Assert.Null(e.Embedding));
        Assert.All(episodes, e => Assert.Equal(0.5, e.Tag));
        Assert.Empty(agent.Memory.RecallFacts([1, 0, 0, 0], default, Night));
        Assert.Equal(0, agent.Memory.Familiarity([1, 0, 0, 0], default, Night));

        agent.Brain.Chemistry[Substance.Adenosine] = Config.Sleep.ForcedAdenosine;
        agent.Brain.AdvanceTo(Night.AddMinutes(81).AddSeconds(35));
        await agent.Dreamer.Generation!.WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.All(agent.Dreamer.Current!.Seeds, seed => Assert.Contains(seed, texts));
    }

    [Fact]
    public async Task A_model_that_only_returns_punctuation_ends_the_dream()
    {
        var fake = new FakeLmStudio(replyTokens: 40) { Completion = " . , " };
        var client = new LmStudioClient(new HttpClient(fake) { BaseAddress = new Uri(Config.Llm.BaseUrl) }, Config.Llm);
        var agent = new Agent(Config, client, new Dictionary<string, LinearProbe>(), () => Night);
        agent.Memory.AddEpisode("a quiet walk by the lake", null, Enumerable.Repeat(1.0, Substances.Count).ToArray(), default, Night.AddHours(-1));
        agent.Brain.Chemistry[Substance.Adenosine] = Config.Sleep.ForcedAdenosine;

        agent.Brain.AdvanceTo(Night.AddMinutes(81).AddSeconds(35));
        await agent.Dreamer.Generation!.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(3, fake.CompletionRequests.Count); // three empty segments in a row, then it stops asking
        Assert.Equal("", agent.Dreamer.Current!.Text);
    }
}
