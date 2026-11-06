using BrainSim.Engine.Config;
using BrainSim.Engine.Effectors;
using BrainSim.Engine.Receptors;

namespace BrainSim.Engine.Tests;

public class ModulationTests
{
    private static readonly EngineConfig Config = EngineConfig.LoadDefault();

    private static Modulation ComputeAt(Action<double[]> set)
    {
        var c = Enumerable.Repeat(1.0, Substances.Count).ToArray();
        set(c);
        var modulation = new Modulation(Config);
        modulation.Compute(c, new ReceptorState(Config));
        return modulation;
    }

    [Fact]
    public void Baseline_state_gives_exactly_zero_on_every_channel()
    {
        var modulation = ComputeAt(_ => { });
        Assert.All(modulation.M, m => Assert.Equal(0.0, m, 12));
    }

    [Fact]
    public void Evaluate_gives_what_compute_would_and_leaves_the_current_values_alone()
    {
        var stressed = Enumerable.Repeat(1.0, Substances.Count).ToArray();
        stressed[(int)Substance.Cortisol] = 2.5;
        var modulation = ComputeAt(_ => { });

        var projected = modulation.Evaluate(stressed, new ReceptorState(Config));

        Assert.Equal(ComputeAt(c => c[(int)Substance.Cortisol] = 2.5).M, projected);
        Assert.All(modulation.M, m => Assert.Equal(0.0, m, 12));
    }

    [Fact]
    public void An_unknown_channel_name_throws_and_names_it()
    {
        var e = Assert.Throws<KeyNotFoundException>(() => ComputeAt(_ => { })["no_such_channel"]);
        Assert.Contains("'no_such_channel'", e.Message);
    }

    [Fact]
    public void Cortisol_makes_the_agent_more_anxious_and_more_cautious()
    {
        var modulation = ComputeAt(c => c[(int)Substance.Cortisol] = 2.5);
        Assert.True(modulation["calm_anxious"] < 0);
        Assert.True(modulation["cautious_bold"] > 0);
    }

    [Fact]
    public void Focus_peaks_at_moderate_norepinephrine()
    {
        double Focus(double ne) => ComputeAt(c => c[(int)Substance.Norepinephrine] = ne)["focused_distracted"];

        Assert.True(Focus(1.3) > 0);
        Assert.True(Focus(1.3) > Focus(3.0));
        Assert.True(Focus(3.0) < 0);
    }

    [Fact]
    public void Gaba_damps_every_channel()
    {
        var plain = ComputeAt(c => c[(int)Substance.Dopamine] = 2.0)["curious_apathetic"];
        var damped = ComputeAt(c =>
        {
            c[(int)Substance.Dopamine] = 2.0;
            c[(int)Substance.Gaba] = 2.0;
        })["curious_apathetic"];

        Assert.True(damped < plain);
        Assert.True(damped > 0);
    }
}
