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
    public async Task One_tick_per_token_and_effort_raises_adenosine()
    {
        var (agent, _, _) = Create(replyTokens: 40);
        var ticksBefore = agent.Brain.Chemistry.Tick;
        var adoBefore = agent.Brain.Chemistry[Substance.Adenosine];

        var reply = await agent.SendAsync("hello", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(40, reply.Tokens);
        Assert.Equal(ticksBefore + 40, agent.Brain.Chemistry.Tick);
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

        // The dopamine burst arrives over the next ticks.
        var daBefore = agent.Brain.Chemistry[Substance.Dopamine];
        agent.Brain.AdvanceTo(agent.Brain.Now.AddSeconds(1));
        Assert.True(agent.Brain.Chemistry[Substance.Dopamine] > 1.5, $"DA was {agent.Brain.Chemistry[Substance.Dopamine]} after {daBefore}");
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
            resumed.Restore((await AgentCheckpoint.LoadAsync(path, ct))!);

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
            Assert.Equal(original.History.Count, resumed.History.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
