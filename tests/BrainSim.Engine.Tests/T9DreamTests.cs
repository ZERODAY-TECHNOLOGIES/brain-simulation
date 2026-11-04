using BrainSim.Engine.Appraisal;
using BrainSim.Engine.Config;
using BrainSim.Engine.Llm;
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
        agent.Dreamer.Remember("the argument about the broken window", stressed, Night.AddHours(-5));
        agent.Dreamer.Remember("someone shouting in the hallway", stressed, Night.AddHours(-4));
        agent.Dreamer.Remember("a quiet walk by the lake", calm, Night.AddHours(-3));
        agent.Dreamer.Remember("tea with a friend", calm, Night.AddHours(-2));
        agent.Dreamer.Remember("reading about octopuses", calm, Night.AddHours(-1));

        // ADO at its limit forces sleep on the next tick, and the dark window keeps it asleep until 07:00.
        agent.Brain.Chemistry[Substance.Adenosine] = config.Sleep.ForcedAdenosine;
        return (agent, fake, t => now = t);
    }

    [Fact]
    public void Emotional_tags_drop_only_on_nights_with_REM()
    {
        var noRem = Config with { Sleep = Config.Sleep with { RemFractionStart = 0, RemFractionStep = 0 } };
        var (withRem, _, _) = Create(Config);
        var (withoutRem, _, _) = Create(noRem);
        var before = withRem.Dreamer.Fragments.Select(f => f.Tag).ToArray();

        withRem.Brain.AdvanceTo(Night.AddHours(6.1));
        withoutRem.Brain.AdvanceTo(Night.AddHours(6.1));

        // Four REM periods have ended by 6 hours (the fourth runs from minute 329 to 360), each seeding 3 to 5
        // fragments that lose 20% of their tag.
        Assert.Equal(4, withRem.Dreamer.Journal.Count);
        Assert.All(withRem.Dreamer.Journal, d => Assert.InRange(d.Seeds.Count, 3, 5));
        var after = withRem.Dreamer.Fragments.Select(f => f.Tag).ToArray();
        Assert.True(after.Sum() < before.Sum() * 0.8);
        for (var i = 0; i < before.Length; i++)
        {
            var uses = Math.Log(after[i] / before[i]) / Math.Log(0.8);
            Assert.Equal(Math.Round(uses), uses, 6); // every drop is a whole number of 20% steps
        }

        Assert.Empty(withoutRem.Dreamer.Journal);
        Assert.Equal(before, withoutRem.Dreamer.Fragments.Select(f => f.Tag));
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
        var request = llm.CompletionRequests.Single().RootElement;
        Assert.InRange(request.GetProperty("temperature").GetDouble(), 0.95, 1.1);
        // The rest of this REM at 1 token per 2 s: it started at minute 81, settled for 30 s, and ends at minute 90.
        Assert.InRange(request.GetProperty("max_tokens").GetInt32(), 250, 256);
        Assert.Contains("\n\n", request.GetProperty("prompt").GetString());

        // A minute later, tokens have been revealed at one per 2 seconds of REM.
        agent.Brain.AdvanceTo(agent.Brain.Now.AddMinutes(1));
        Assert.StartsWith("d0 d1 ", agent.Dreamer.Current!.Text);

        setClock(agent.Brain.Now);
        await agent.SendAsync("good morning", cancellationToken: ct);

        var dream = agent.Dreamer.Journal[^1];
        Assert.True(dream.Interrupted);
        var revealed = (int)((dream.End - dream.Start).TotalSeconds / Config.Dreams.SecondsPerToken);
        Assert.Equal(revealed, dream.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.True(dream.Chemistry[(int)Substance.Norepinephrine] < 0.15);
        Assert.True(dream.Chemistry[(int)Substance.Acetylcholine] > 1.1);

        var messages = llm.ChatRequests[^1].RootElement.GetProperty("messages").EnumerateArray().ToList();
        Assert.Contains(messages, m => m.GetProperty("role").GetString() == "system"
            && m.GetProperty("content").GetString()!.Contains("still remember the dream: d0 d1"));
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
}
