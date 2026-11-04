using System.Text.Json;
using BrainSim.Engine.Appraisal;
using BrainSim.Engine.Config;
using BrainSim.Engine.Llm;
using BrainSim.Engine.Memory;

namespace BrainSim.Engine.Tests;

/// <summary>
/// Spec T7 in its ACT-R form: a new fact given with DA high is recalled after it has left the context;
/// the same fact given with DA low or CORT high is not.
/// </summary>
public class T7MemoryTests
{
    private const string Fact = "My sister's name is Ilinca.";
    private const string Extracted = "The user's sister is called Ilinca.";
    private const string Question = "What is my sister called?";
    private static readonly EngineConfig Config = EngineConfig.LoadDefault();
    private static readonly DateTime Start = new(2026, 11, 4, 10, 0, 0);

    /// <summary>The fact and its extraction share a direction; the question about it sits at cosine 0.8; the rest is orthogonal.</summary>
    /// <remarks>Small talk varies a little between messages, so the appraisal sees real novelty statistics, as with nomic.</remarks>
    private static float[] Embed(string text)
    {
        float[] v = text.Contains("sister") ? (text.Contains('?') ? [0.8f, 0.6f, 0, 0, 0] : [1, 0, 0, 0, 0])
            : text.Contains("lighthouse") ? [0, 0, 0, 1, 0]
            : [0, 0, 1, 0, 0.1f * (text.Sum(c => c) % 7)];
        var norm = MathF.Sqrt(v.Sum(x => x * x));
        return [.. v.Select(x => x / norm)];
    }

    private static string Reply(JsonDocument request)
    {
        var messages = request.RootElement.GetProperty("messages");
        var extraction = messages[0].GetProperty("content").GetString() == FactExtractor.Instruction;
        if (!extraction)
        {
            return "ok";
        }

        return messages[1].GetProperty("content").GetString()!.Contains("sister") ? "- " + Extracted : "";
    }

    private static (Agent Agent, FakeLmStudio Llm, Action<DateTime> SetClock, Func<DateTime> Clock) Create(Func<JsonDocument, string>? reply = null)
    {
        var fake = new FakeLmStudio(replyTokens: 5) { Embed = Embed, Reply = reply ?? Reply };
        var client = new LmStudioClient(new HttpClient(fake) { BaseAddress = new Uri(Config.Llm.BaseUrl) }, Config.Llm);
        var now = Start;
        var agent = new Agent(Config, client, new Dictionary<string, LinearProbe>(), () => now);
        return (agent, fake, t => now = t, () => now);
    }

    private static JsonElement LastReplyRequest(FakeLmStudio llm) => llm.ChatRequests[^1].RootElement;

    private static bool Remembers(FakeLmStudio llm) =>
        LastReplyRequest(llm).GetProperty("messages").EnumerateArray()
            .Any(m => m.GetProperty("content").GetString()!.StartsWith("Things you remember:") && m.GetProperty("content").GetString()!.Contains("Ilinca"));

    private static async Task<bool> TellThenAsk(Substance held, double level)
    {
        var ct = TestContext.Current.CancellationToken;
        var (agent, llm, setClock, clock) = Create();

        agent.Brain.Chemistry.Hold[(int)held] = level;
        setClock(clock().AddMinutes(1));
        await agent.SendAsync(Fact, cancellationToken: ct);
        await agent.Extraction!.WaitAsync(TimeSpan.FromSeconds(10), ct);
        agent.Brain.Chemistry.Hold[(int)held] = double.NaN;

        // 25 more turns, one minute apart: the fact is out of the 20-message context and the chemistry is back at rest.
        for (var i = 0; i < 25; i++)
        {
            setClock(clock().AddMinutes(1));
            await agent.SendAsync($"filler message {i}", cancellationToken: ct);
        }

        setClock(clock().AddMinutes(1));
        await agent.SendAsync(Question, cancellationToken: ct);
        return Remembers(llm);
    }

    [Fact]
    public async Task A_fact_told_with_high_dopamine_is_recalled_after_it_leaves_the_context()
    {
        Assert.True(await TellThenAsk(Substance.Dopamine, 2.0), "told with DA high: recalled");
        Assert.False(await TellThenAsk(Substance.Dopamine, 0.5), "told with DA low: not recalled");
        Assert.False(await TellThenAsk(Substance.Cortisol, 2.0), "told under stress: not recalled");
    }

    [Fact]
    public async Task The_first_message_sends_no_memory()
    {
        var (agent, llm, _, _) = Create();

        var reply = await agent.SendAsync("hello there", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(reply.Recalled);
        Assert.Equal(0, reply.Familiarity);
        Assert.DoesNotContain(LastReplyRequest(llm).GetProperty("messages").EnumerateArray(),
            m => m.GetProperty("content").GetString()!.StartsWith("Things you remember:"));
    }

    [Fact]
    public async Task Familiar_messages_are_less_surprising()
    {
        var ct = TestContext.Current.CancellationToken;
        var (familiar, _, setA, clockA) = Create();
        var (stranger, _, setB, clockB) = Create();
        familiar.Memory.AddEpisode("the lighthouse at dawn", [0, 0, 0, 1], Enumerable.Repeat(1.0, Substances.Count).ToArray(), default, Start);

        // Same conversation for both, so their appraisers hold the same novelty statistics.
        foreach (var (agent, set, clock) in new[] { (familiar, setA, clockA), (stranger, setB, clockB) })
        {
            for (var i = 0; i < 8; i++)
            {
                set(clock().AddSeconds(30));
                await agent.SendAsync($"small talk {i}", cancellationToken: ct);
            }
        }

        setA(clockA().AddSeconds(30));
        setB(clockB().AddSeconds(30));
        var a = await familiar.SendAsync("tell me about the lighthouse", cancellationToken: ct);
        var b = await stranger.SendAsync("tell me about the lighthouse", cancellationToken: ct);

        Assert.True(b.Signals.Surprise > 0, "the lighthouse is new to the appraisal");
        Assert.True(a.Familiarity > 0);
        Assert.Equal(b.Signals.Surprise * (1 - Config.Memory.FamiliarityDamping * a.Familiarity), a.Signals.Surprise, 9);
    }

    [Fact]
    public async Task Losing_LM_Studio_keeps_the_conversation_going()
    {
        var ct = TestContext.Current.CancellationToken;

        var (noEmbeddings, llm, _, _) = Create();
        llm.EmbeddingsDown = true;
        var reply = await noEmbeddings.SendAsync(Fact, cancellationToken: ct);
        Assert.Equal("ok", reply.Text);
        Assert.Empty(reply.Recalled);

        var (noExtraction, _, _, _) = Create(request =>
            request.RootElement.GetProperty("messages")[0].GetProperty("content").GetString() == FactExtractor.Instruction
                ? throw new HttpRequestException("LM Studio went away")
                : "ok");
        await noExtraction.SendAsync(Fact, cancellationToken: ct);
        await noExtraction.Extraction!.WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.Single(noExtraction.Memory.Snapshot(), c => c.Kind == ChunkKind.Episode);
        Assert.DoesNotContain(noExtraction.Memory.Snapshot(), c => c.Kind == ChunkKind.Fact);
    }
}
