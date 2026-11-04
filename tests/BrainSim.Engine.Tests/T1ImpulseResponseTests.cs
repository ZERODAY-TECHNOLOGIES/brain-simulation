using BrainSim.Engine.Chemistry;
using BrainSim.Engine.Config;

namespace BrainSim.Engine.Tests;

/// <summary>Spec T1: every substance returns toward baseline with its configured half-life, error under 2%.</summary>
public class T1ImpulseResponseTests
{
    public static TheoryData<Substance> AllSubstances => [.. Substances.All];

    [Theory]
    [MemberData(nameof(AllSubstances))]
    public void Deviation_halves_after_one_half_life_within_2_percent(Substance substance)
    {
        // Interactions off so each substance is measured alone. Asleep so ADO decays too.
        var config = EngineConfig.LoadDefault() with { Interactions = [] };
        var chemistry = new ChemicalState(config) { Asleep = true };
        chemistry[substance] = 2.0;

        var halfLife = config.HalfLives()[(int)substance];
        for (var t = 0; t < halfLife; t++)
        {
            chemistry.Step();
        }

        // Closed form b + (C0 - b) e^(-k T_half) gives exactly half the deviation left.
        var remaining = chemistry[substance] - 1.0;
        Assert.InRange(remaining, 0.5 * 0.98, 0.5 * 1.02);
    }

    [Fact]
    public void A_half_life_set_in_the_lab_is_the_one_the_decay_follows()
    {
        var chemistry = new ChemicalState(EngineConfig.LoadDefault() with { Interactions = [] });
        chemistry.SetHalfLife(Substance.Dopamine, 40); // config.yaml says 100
        chemistry[Substance.Dopamine] = 2.0;

        for (var t = 0; t < 40; t++)
        {
            chemistry.Step();
        }

        Assert.InRange(chemistry[Substance.Dopamine] - 1.0, 0.5 * 0.98, 0.5 * 1.02);
    }

    [Fact]
    public void Adenosine_does_not_decay_while_awake()
    {
        var chemistry = new ChemicalState(EngineConfig.LoadDefault() with { Interactions = [] });
        chemistry[Substance.Adenosine] = 3.0;

        for (var t = 0; t < 10_000; t++)
        {
            chemistry.Step();
        }

        Assert.Equal(3.0, chemistry[Substance.Adenosine], 12);
    }

    [Fact]
    public void Release_is_spread_evenly_over_ten_ticks()
    {
        var config = EngineConfig.LoadDefault() with { Interactions = [] };
        var chemistry = new ChemicalState(config);
        chemistry.Release(Substance.Cortisol, 1.0);

        chemistry.Step();
        Assert.Equal(1.1, chemistry[Substance.Cortisol], 6);

        for (var t = 1; t < config.Time.ImpulseSpreadTicks; t++)
        {
            chemistry.Step();
        }

        // All of the release has arrived, minus a little decay (CORT half-life is 8000).
        Assert.InRange(chemistry[Substance.Cortisol], 1.99, 2.0);
        chemistry.Step();
        Assert.True(chemistry[Substance.Cortisol] < 2.0);
    }

    [Fact]
    public void Concentrations_stay_within_bounds()
    {
        var chemistry = new ChemicalState(EngineConfig.LoadDefault());
        chemistry.Release(Substance.Norepinephrine, 100);
        chemistry.Release(Substance.Gaba, -100);

        for (var t = 0; t < 50; t++)
        {
            chemistry.Step();
            Assert.All(chemistry.C, c => Assert.InRange(c, 0.0, 5.0));
        }
    }
}
