using BrainSim.Engine.Config;

namespace BrainSim.Engine.Sleep;

public enum SleepPhase
{
    Wake,
    N1,
    N2,
    N3,
    Rem,
}

/// <summary>
/// Sleep cycles (spec: "Arhitectura somnului"). Each 90 minute cycle runs N1, N2, N3, REM.
/// N1 is 5% of the cycle. N3 starts at 40% and drops 9 points per cycle, REM starts at 10% and gains 8.
/// N2 is what is left. Late cycles clamp N3 at zero.
/// </summary>
public sealed class SleepArchitecture(SleepConfig config)
{
    public TimeSpan CycleLength => TimeSpan.FromMinutes(config.CycleMinutes);

    /// <summary>Fractions of cycle <paramref name="cycle"/> (0 based) spent in N1, N2, N3 and REM.</summary>
    public (double N1, double N2, double N3, double Rem) Fractions(int cycle)
    {
        var n1 = config.N1Fraction;
        var n3 = Math.Max(0, config.N3FractionStart + cycle * config.N3FractionStep);
        var rem = Math.Clamp(config.RemFractionStart + cycle * config.RemFractionStep, 0, 1 - n1 - n3);
        return (n1, 1 - n1 - n3 - rem, n3, rem);
    }

    public (SleepPhase Phase, int Cycle) PhaseAt(TimeSpan sinceOnset)
    {
        var cycle = (int)(sinceOnset / CycleLength);
        var position = (sinceOnset - cycle * CycleLength) / CycleLength;
        var (n1, n2, n3, _) = Fractions(cycle);

        var phase = position < n1 ? SleepPhase.N1
            : position < n1 + n2 ? SleepPhase.N2
            : position < n1 + n2 + n3 ? SleepPhase.N3
            : SleepPhase.Rem;
        return (phase, cycle);
    }
}
