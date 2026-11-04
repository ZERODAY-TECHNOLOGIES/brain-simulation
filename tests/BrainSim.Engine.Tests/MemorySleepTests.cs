using BrainSim.Engine.Config;
using BrainSim.Engine.Memory;
using BrainSim.Engine.Sleep;

namespace BrainSim.Engine.Tests;

public class MemorySleepTests
{
    private static readonly EngineConfig Config = EngineConfig.LoadDefault();
    private static readonly DateTime Night = new(2026, 11, 5, 1, 0, 0);

    private static double[] Chemistry(double dopamine)
    {
        var c = Enumerable.Repeat(1.0, Substances.Count).ToArray();
        c[(int)Substance.Dopamine] = dopamine;
        return c;
    }

    /// <summary>Three episodes from the evening before, the second the most salient (high DA at encoding).</summary>
    private static DeclarativeMemory Remember()
    {
        var memory = new DeclarativeMemory(Config.Memory);
        memory.AddEpisode("a calm walk", [1, 0, 0], Chemistry(1.0), default, Night.AddHours(-4));
        memory.AddEpisode("winning the prize", [0, 1, 0], Chemistry(2.0), default, Night.AddHours(-3));
        memory.AddEpisode("tea with a friend", [0, 0, 1], Chemistry(1.2), default, Night.AddHours(-2));
        return memory;
    }

    private static Brain Hooked(DeclarativeMemory memory, bool asleep)
    {
        var brain = new Brain(Config, Night);
        brain.Ticked += (b, _) => memory.OnTick(b);
        if (asleep)
        {
            brain.Chemistry[Substance.Adenosine] = Config.Sleep.ForcedAdenosine;
        }

        return brain;
    }

    [Fact]
    public void A_night_of_sleep_strengthens_salient_episodes()
    {
        var slept = Remember();
        var awake = Remember();
        var sleeper = Hooked(slept, asleep: true);
        var wakeful = Hooked(awake, asleep: false);

        sleeper.AdvanceTo(Night.AddHours(6));
        wakeful.AdvanceTo(Night.AddHours(6));

        Assert.False(wakeful.Asleep);
        var prize = (DeclarativeMemory m) => m.Snapshot().Single(c => c.Text == "winning the prize");
        Assert.True(slept.BaseLevel(prize(slept), Night.AddHours(6)) > awake.BaseLevel(prize(awake), Night.AddHours(6)));
    }

    [Fact]
    public void Replay_follows_salience_and_lowers_it()
    {
        var memory = Remember();
        var before = memory.Snapshot().Single(c => c.Text == "winning the prize").Salience;
        var replayed = new List<Chunk>();
        memory.Replayed += replayed.Add;

        Hooked(memory, asleep: true).AdvanceTo(Night.AddMinutes(7)); // N1 for 4.5 minutes, then one replay in N2

        Assert.Equal("winning the prize", Assert.Single(replayed).Text);
        Assert.Equal(before * Config.Memory.SalienceDecay, memory.Snapshot().Single(c => c.Text == "winning the prize").Salience, 9);
    }

    [Fact]
    public void N2_replays_at_a_third_of_the_weight()
    {
        var memory = new DeclarativeMemory(Config.Memory);

        Assert.Equal(0.3 * memory.ReplayWeight(SleepPhase.N3, default), memory.ReplayWeight(SleepPhase.N2, default), 9);
        Assert.Equal(0, memory.ReplayWeight(SleepPhase.Rem, default));
        Assert.Equal(0, memory.ReplayWeight(SleepPhase.Wake, default));
    }

    [Fact]
    public void Dopamine_strengthens_consolidation()
    {
        var memory = new DeclarativeMemory(Config.Memory);

        Assert.True(memory.ReplayWeight(SleepPhase.N3, new MemoryModulation(0, 0, 0, 0.12)) > memory.ReplayWeight(SleepPhase.N3, default));
    }
}
