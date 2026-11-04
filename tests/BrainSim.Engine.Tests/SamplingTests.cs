using BrainSim.Engine.Config;
using BrainSim.Engine.Effectors;

namespace BrainSim.Engine.Tests;

public class SamplingTests
{
    private static readonly SamplingConfig Config = EngineConfig.LoadDefault().Sampling;

    private static double[] Chemistry(params (Substance S, double C)[] changes)
    {
        var c = Enumerable.Repeat(1.0, Substances.Count).ToArray();
        foreach (var (s, value) in changes)
        {
            c[(int)s] = value;
        }

        return c;
    }

    [Fact]
    public void Baseline_gives_near_t0_full_length_and_full_top_p()
    {
        var p = Sampling.Compute(Chemistry(), Config);
        Assert.Equal(0.7 * (1 + 0.3 * 0.09), p.Temperature, 9);
        Assert.Equal(0.95, p.TopP, 9);
        Assert.Equal(512, p.MaxTokens);
    }

    [Fact]
    public void Rem_chemistry_raises_temperature_to_about_one()
    {
        // Spec, dreams: NE near 0 gives T close to 1.0 through the E2 formula.
        var p = Sampling.Compute(Chemistry((Substance.Norepinephrine, 0.05)), Config);
        Assert.InRange(p.Temperature, 0.98, 1.08);
    }

    [Fact]
    public void Temperature_is_lowest_at_moderate_norepinephrine_and_clamped()
    {
        double T(double ne) => Sampling.Compute(Chemistry((Substance.Norepinephrine, ne)), Config).Temperature;
        Assert.True(T(1.3) < T(1.0));
        Assert.True(T(1.3) < T(2.5));
        Assert.Equal(1.5, T(5.0), 9);
    }

    [Fact]
    public void Stress_and_fatigue_shorten_replies_and_tighten_top_p()
    {
        var stressed = Sampling.Compute(Chemistry((Substance.Cortisol, 2.0), (Substance.Norepinephrine, 2.0)), Config);
        Assert.Equal(358, stressed.MaxTokens);                     // 30% shorter
        Assert.Equal(0.95, stressed.TopP, 9);

        var onlyCortisol = Sampling.Compute(Chemistry((Substance.Cortisol, 2.0)), Config);
        Assert.Equal(512, onlyCortisol.MaxTokens);                 // needs both CORT and NE

        var tired = Sampling.Compute(Chemistry((Substance.Adenosine, 5.0)), Config);
        Assert.Equal(358, tired.MaxTokens);
        Assert.Equal(0.8, tired.TopP, 9);

        var inhibited = Sampling.Compute(Chemistry((Substance.Gaba, 1.5)), Config);
        Assert.Equal(0.875, inhibited.TopP, 9);
    }
}
