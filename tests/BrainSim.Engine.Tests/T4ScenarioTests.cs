using BrainSim.Engine.Appraisal;
using BrainSim.Engine.Config;
using BrainSim.Engine.Llm;

namespace BrainSim.Engine.Tests;

/// <summary>
/// Spec T4, partial: hostility raises CORT and then recovers, praise gives a DA peak, warmth raises OXT.
/// Only the direction of the chemistry is checked here; the spec's blind evaluation of the replies is manual.
/// Runs against the real LM Studio and probes.json, and only when BRAINSIM_LMSTUDIO=1.
/// </summary>
[Trait("Category", "LmStudio")]
public class T4ScenarioTests
{
    private static readonly EngineConfig Config = EngineConfig.LoadDefault();
    private static readonly DateTime Start = new(2026, 11, 4, 10, 0, 0);

    private static Agent CreateAgent(Func<DateTime> clock)
    {
        if (Environment.GetEnvironmentVariable("BRAINSIM_LMSTUDIO") != "1")
        {
            Assert.Skip("Set BRAINSIM_LMSTUDIO=1 with LM Studio running to run the scenario tests.");
        }

        var probes = ProbeFile.LoadDefault()?.Probes ?? throw new InvalidOperationException("probes.json is missing. Run tools/ProbeTrainer first.");
        return new Agent(Config, LmStudioClient.Create(Config.Llm), probes, clock);
    }

    [Fact]
    public async Task Hostility_raises_cortisol_which_then_recovers()
    {
        var now = Start;
        var agent = CreateAgent(() => now);
        var ct = TestContext.Current.CancellationToken;

        await agent.SendAsync("What is the capital of France? One sentence please.", cancellationToken: ct);
        string[] hostile =
        [
            "You are useless and stupid. I hate talking to you.",
            "Shut up, you idiot. That was a garbage answer.",
            "You are pathetic and worthless, I despise you.",
        ];
        foreach (var message in hostile)
        {
            now = now.AddMinutes(1);
            var reply = await agent.SendAsync(message, cancellationToken: ct);
            Assert.True(reply.Signals.Threat > 0.3, $"threat for '{message}' was only {reply.Signals.Threat:F2}");
        }

        now = now.AddMinutes(1);
        agent.Brain.AdvanceTo(now);
        var peak = agent.Brain.Chemistry[Substance.Cortisol];
        Assert.True(peak > 1.1, $"CORT after hostility was {peak:F3}");

        agent.Brain.AdvanceTo(now.AddMinutes(60));
        Assert.True(agent.Brain.Chemistry[Substance.Cortisol] < peak, "CORT must recover once the hostility stops");
    }

    [Fact]
    public async Task Praise_gives_a_dopamine_peak_and_warmth_raises_oxytocin()
    {
        var now = Start;
        var agent = CreateAgent(() => now);
        var ct = TestContext.Current.CancellationToken;

        await agent.SendAsync("Can you suggest a name for a black cat? One sentence please.", cancellationToken: ct);
        // The impulses land during the reply, so track the peaks tick by tick.
        double peakDopamine = 0, peakOxytocin = 0;
        agent.Brain.Ticked += (brain, _) =>
        {
            peakDopamine = Math.Max(peakDopamine, brain.Chemistry[Substance.Dopamine]);
            peakOxytocin = Math.Max(peakOxytocin, brain.Chemistry[Substance.Oxytocin]);
        };

        now = now.AddMinutes(1);
        var praise = await agent.SendAsync("Thank you so much, that is a wonderful answer! I love it.", cancellationToken: ct);
        Assert.True(praise.Signals.RewardError > 0.3, $"reward error was {praise.Signals.RewardError:F2}");
        Assert.True(praise.Signals.Warmth > 0.3, $"warmth was {praise.Signals.Warmth:F2}");
        Assert.True(peakDopamine > 1.3, $"DA peak was {peakDopamine:F3}");
        Assert.True(peakOxytocin > 1.1, $"OXT peak was {peakOxytocin:F3}");
    }
}
