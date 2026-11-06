using BrainSim.Engine.Chemistry;
using BrainSim.Engine.Config;

namespace BrainSim.Engine.Tests;

public class ChemicalStateTests
{
    private static readonly EngineConfig Config = EngineConfig.LoadDefault();

    [Fact]
    public void The_projection_adds_the_pending_releases_keeps_holds_and_stays_in_bounds()
    {
        var chemistry = new ChemicalState(Config);
        chemistry.Release(Substance.Norepinephrine, 1.2);
        chemistry.Release(Substance.Dopamine, -3);
        chemistry.Release(Substance.Cortisol, 9);
        chemistry.Release(Substance.Oxytocin, 0.8);
        chemistry.Hold[(int)Substance.Oxytocin] = 0.4;
        chemistry.Step(); // a tenth of each release has arrived, the rest is still pending

        var projected = chemistry.Projected();

        Assert.Equal(2.2, projected[(int)Substance.Norepinephrine], 1); // decay over the spread is ignored
        Assert.Equal(Config.Concentration.Min, projected[(int)Substance.Dopamine]);
        Assert.Equal(Config.Concentration.Max, projected[(int)Substance.Cortisol]);
        Assert.Equal(0.4, projected[(int)Substance.Oxytocin]);
        Assert.Equal(1.0, projected[(int)Substance.Serotonin], 3);
        Assert.True(chemistry[Substance.Norepinephrine] < 1.2, "projecting does not change the chemistry");
    }
}
