using System.Net;
using System.Text;
using System.Text.Json;
using BrainSim.Engine.Appraisal;
using BrainSim.Engine.Config;
using BrainSim.Engine.Llm;

namespace BrainSim.Engine.Tests;

public class AgentTests
{
    private static readonly EngineConfig Config = EngineConfig.LoadDefault();
    private static readonly DateTime Start = new(2026, 11, 4, 10, 0, 0);

    private static readonly Dictionary<string, LinearProbe> Probes = new()
    {
        ["positive"] = new LinearProbe([0, 0, 20, 0], -10, 1, "test"),
        ["negative"] = new LinearProbe([0, 0, 0, 20], -10, 1, "test"),
    };

    private static (Agent Agent, FakeLmStudio Llm, Func<DateTime, DateTime> SetClock) Create(int replyTokens = 40)
    {
        var fake = new FakeLmStudio(replyTokens);
        var client = new LmStudioClient(new HttpClient(fake) { BaseAddress = new Uri(Config.Llm.BaseUrl) }, Config.Llm);
        var now = Start;
        var agent = new Agent(Config, client, Probes, () => now);
        return (agent, fake, t => now = t);
    }

    [Fact]
    public async Task Idle_time_is_caught_up_then_one_tick_per_token_and_effort_raises_adenosine()
    {
        var (agent, _, setClock) = Create(replyTokens: 40);
        var ticksBefore = agent.Brain.Chemistry.Tick;
        var adoBefore = agent.Brain.Chemistry[Substance.Adenosine];
        setClock(Start.AddMinutes(1));

        var reply = await agent.SendAsync("hello", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(40, reply.Tokens);
        Assert.Equal(ticksBefore + 600 + 40, agent.Brain.Chemistry.Tick); // a minute of idle ticks at 10 per second
        Assert.Null(reply.Signals.Reward);
        Assert.False(agent.Brain.IsGenerating);

        agent.Brain.AdvanceTo(agent.Brain.Now.AddSeconds(2));
        Assert.True(agent.Brain.Chemistry[Substance.Adenosine] - adoBefore >= Config.Appraisal.EffortGain * 40 - 1e-9);
    }

    [Fact]
    public async Task Sampling_is_sent_with_the_request_and_praise_on_the_next_turn_is_rewarded()
    {
        var (agent, llm, setClock) = Create();
        var ct = TestContext.Current.CancellationToken;

        var first = await agent.SendAsync("hello", cancellationToken: ct);
        var request = llm.ChatRequests[0].RootElement;
        Assert.Equal(first.Sampling.Temperature, request.GetProperty("temperature").GetDouble(), 9);
        Assert.Equal(first.Sampling.MaxTokens, request.GetProperty("max_tokens").GetInt32());
        Assert.Equal("system", request.GetProperty("messages")[0].GetProperty("role").GetString());

        setClock(Start.AddMinutes(1));
        var second = await agent.SendAsync("thanks, that was great", cancellationToken: ct);
        Assert.InRange(second.Signals.Reward!.Value, 0.99, 1.0);
        Assert.True(second.Signals.RewardError > 0.9);
        Assert.Equal(4, llm.ChatRequests[1].RootElement.GetProperty("messages").GetArrayLength()); // system, user, assistant, user
        Assert.Equal(Config.Appraisal.RewardLearningRate * second.Signals.Reward.Value, agent.Appraiser.ExpectedReward, 9);

        // The dopamine burst arrives over the next ticks.
        var daBefore = agent.Brain.Chemistry[Substance.Dopamine];
        agent.Brain.AdvanceTo(agent.Brain.Now.AddSeconds(1));
        Assert.True(agent.Brain.Chemistry[Substance.Dopamine] > 1.5, $"DA was {agent.Brain.Chemistry[Substance.Dopamine]} after {daBefore}");
    }

    private static string SystemMessage(JsonDocument request) =>
        request.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;

    [Fact]
    public async Task A_threat_shapes_the_reply_it_triggers()
    {
        var probes = new Dictionary<string, LinearProbe>(Probes) { ["hostility"] = new LinearProbe([20, 0, 0, 0], -10, 1, "test") };
        var fake = new FakeLmStudio(replyTokens: 20) { Embed = text => text.Contains("idiot") ? [1, 0, 0, 0] : [0.5f, 0.5f, 0.5f, 0.5f] };
        var client = new LmStudioClient(new HttpClient(fake) { BaseAddress = new Uri(Config.Llm.BaseUrl) }, Config.Llm);
        var agent = new Agent(Config, client, probes, () => Start);
        var rest = Effectors.Sampling.Compute(agent.Brain.Chemistry.C, Config.Sampling);
        double[]? projected = null;

        var reply = await agent.SendAsync(
            "you idiot",
            onAppraised: (_, _, _, _) => projected = agent.Brain.Chemistry.Projected(),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.InRange(reply.Signals.Threat, 0.99, 1.0);
        var anxious = Config.Mood.Channels["calm_anxious"].Negative;
        Assert.Contains(anxious.Feeling, reply.Mood);
        Assert.Contains(anxious.Shows, reply.Mood);
        Assert.Contains(reply.Mood!, SystemMessage(fake.ChatRequests[0]));
        Assert.Equal(Effectors.Sampling.Compute(projected!, Config.Sampling), reply.Sampling);
        Assert.True(reply.Sampling.Temperature > rest.Temperature + 0.1, $"T {reply.Sampling.Temperature} vs {rest.Temperature} at rest");
        Assert.DoesNotContain(agent.History, m => m.Content.Contains(anxious.Feeling)); // sent with this request only
    }

    [Fact]
    public async Task At_rest_the_request_carries_the_persona_and_no_mood_note()
    {
        var (agent, llm, _) = Create();

        var reply = await agent.SendAsync("hello", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(reply.Mood);
        Assert.Equal(Config.Llm.SystemPrompt, SystemMessage(llm.ChatRequests[0]));
        Assert.Contains("brain simulation", Config.Llm.SystemPrompt);
    }

    [Fact]
    public async Task A_checkpoint_resumes_exactly()
    {
        var ct = TestContext.Current.CancellationToken;
        var (original, _, setOriginal) = Create();
        await original.SendAsync("hello", cancellationToken: ct);
        setOriginal(Start.AddMinutes(5));
        await original.SendAsync("thanks", cancellationToken: ct);

        var path = Path.Combine(Path.GetTempPath(), $"brainsim-checkpoint-{Guid.NewGuid():N}.json");
        try
        {
            await original.Checkpoint().SaveAsync(path, ct);
            var (resumed, _, setResumed) = Create();
            var checkpoint = (await AgentCheckpoint.LoadAsync(path, ct))!;
            resumed.Restore(checkpoint);
            resumed.Restore(checkpoint); // restoring again replaces the conversation, it does not append to it

            // Same inputs from here on must give the same state, bit for bit.
            foreach (var (agent, set) in new[] { (original, setOriginal), (resumed, setResumed) })
            {
                set(Start.AddMinutes(30));
                await agent.SendAsync("thanks again", cancellationToken: ct);
                agent.Brain.AdvanceTo(Start.AddHours(1));
            }

            Assert.Equal(original.Brain.Chemistry.C, resumed.Brain.Chemistry.C);
            Assert.Equal(original.Brain.Receptors.Rho, resumed.Brain.Receptors.Rho);
            Assert.Equal(original.Appraiser.ExpectedReward, resumed.Appraiser.ExpectedReward);
            Assert.Equal(original.History, resumed.History);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task The_history_kept_is_capped_at_400_messages_dropping_the_oldest()
    {
        var (agent, _, _) = Create(replyTokens: 1);

        for (var i = 0; i < 201; i++)
        {
            await agent.SendAsync($"message {i}", cancellationToken: TestContext.Current.CancellationToken);
        }

        Assert.Equal(400, agent.History.Count);
        Assert.Equal("message 1", agent.History[0].Content);
    }

    [Fact]
    public async Task Each_token_is_ticked_at_its_own_time_and_at_the_clock_once_it_has_moved_on()
    {
        var (agent, _, setClock) = Create(replyTokens: 40);
        var times = new List<DateTime>();

        await agent.SendAsync("hello", onToken: _ =>
        {
            times.Add(agent.Brain.Now);
            if (times.Count == 20)
            {
                setClock(Start.AddSeconds(1));
            }
        }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(Start.AddTicks(1), times[0]); // the clock stands still, yet every token gets a later time
        Assert.All(times.Zip(times.Skip(1)), pair => Assert.True(pair.Second > pair.First));
        Assert.Equal(Start.AddSeconds(1), times[20]);
        Assert.Equal(Start.AddSeconds(1).AddTicks(1), times[21]);
    }

    [Fact]
    public async Task Sleep_waits_for_the_reply_to_finish()
    {
        var (agent, _, _) = Create(replyTokens: 40);
        agent.Brain.Chemistry[Substance.Adenosine] = Config.Sleep.ForcedAdenosine;
        var asleepWhileReplying = false;

        var reply = await agent.SendAsync("hello", onToken: _ => asleepWhileReplying |= agent.Brain.Asleep,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(40, reply.Tokens);
        Assert.False(asleepWhileReplying);
        agent.Brain.AdvanceTo(agent.Brain.Now.AddSeconds(1));
        Assert.True(agent.Brain.Asleep);
    }

    [Fact]
    public async Task Recalled_facts_ride_in_the_system_message_one_per_line()
    {
        var (agent, llm, _) = Create();
        agent.Memory.AddFact("The user has a black cat.", [0.5f, 0.5f, 0.5f, 0.5f], 0, default, Start);
        agent.Memory.AddFact("The user lives in Cluj.", [0.5f, 0.5f, 0.5f, 0.5f], 0, default, Start);

        var reply = await agent.SendAsync("hello", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, reply.Recalled.Count);
        var system = llm.ChatRequests[0].RootElement.GetProperty("messages")[0].GetProperty("content").GetString();
        Assert.Equal($"{Config.Llm.SystemPrompt}\n\nThings you remember:\n- {reply.Recalled[0]}\n- {reply.Recalled[1]}", system);
    }

    [Fact]
    public async Task Without_embeddings_the_expected_reward_still_releases_serotonin()
    {
        var ct = TestContext.Current.CancellationToken;
        var serotonin = new List<double>();
        foreach (var expected in new[] { 0.0, 1.0 })
        {
            var (agent, llm, _) = Create();
            agent.Appraiser.Restore(new AppraiserState(expected, [], [], null));
            llm.EmbeddingsDown = true;

            await agent.SendAsync("hello", cancellationToken: ct);
            agent.Brain.AdvanceTo(agent.Brain.Now.AddSeconds(2));
            serotonin.Add(agent.Brain.Chemistry[Substance.Serotonin]);
        }

        Assert.True(serotonin[1] > serotonin[0], $"5-HT {serotonin[1]} with V = 1, {serotonin[0]} with V = 0");
    }
}
