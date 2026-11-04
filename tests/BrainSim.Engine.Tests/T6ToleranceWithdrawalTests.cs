using BrainSim.Engine.Config;
using BrainSim.Engine.Receptors;

namespace BrainSim.Engine.Tests;

/// <summary>Spec T6: a sustained high level lowers rho; after withdrawal the effect stays below baseline for about tau_rho.</summary>
public class T6ToleranceWithdrawalTests
{
    private static readonly EngineConfig Config = EngineConfig.LoadDefault();
    private static readonly ChannelTerm DopamineTerm = new((int)Substance.Dopamine, Emax: 1.0, Ec50: 1.0, N: 1.5, InvertedU: false, Peak: 1.0);

    private static void Hold(ReceptorState receptors, double[] c, int ticks)
    {
        for (var t = 0; t < ticks; t++)
        {
            receptors.Step(c);
        }
    }

    [Fact]
    public void Sustained_high_level_builds_tolerance_then_withdrawal_lasts_about_tau_rho()
    {
        var receptors = new ReceptorState(Config);
        var c = Enumerable.Repeat(1.0, Substances.Count).ToArray();
        var naiveEffect = receptors.Delta(DopamineTerm, 2.0);

        c[(int)Substance.Dopamine] = 2.0;
        Hold(receptors, c, 100_000);

        Assert.InRange(receptors.Rho[(int)Substance.Dopamine], 0.45, 0.55);
        Assert.True(receptors.Delta(DopamineTerm, 2.0) < naiveEffect, "tolerance: the same concentration must have a smaller effect");

        c[(int)Substance.Dopamine] = 1.0;
        var withdrawal = receptors.Delta(DopamineTerm, 1.0);
        Assert.True(withdrawal < 0, "withdrawal: back at baseline the effect must be below zero");

        var tauRho = (int)Config.Receptors.TauRho;
        Hold(receptors, c, tauRho);
        Assert.InRange(receptors.Delta(DopamineTerm, 1.0) / withdrawal, 0.3, 0.7);

        Hold(receptors, c, 4 * tauRho);
        Assert.InRange(receptors.Delta(DopamineTerm, 1.0) / withdrawal, 0.0, 0.05);
    }

    [Fact]
    public void Sustained_low_level_sensitizes()
    {
        var receptors = new ReceptorState(Config);
        var c = Enumerable.Repeat(1.0, Substances.Count).ToArray();
        c[(int)Substance.Dopamine] = 0.5;
        Hold(receptors, c, 100_000);

        Assert.True(receptors.Rho[(int)Substance.Dopamine] > 1.2);
        Assert.True(receptors.Delta(DopamineTerm, 1.0) > 0, "sensitization: baseline concentration now has a positive effect");
    }
}
