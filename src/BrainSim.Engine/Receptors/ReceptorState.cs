using BrainSim.Engine.Config;

namespace BrainSim.Engine.Receptors;

/// <summary>
/// Hill occupancy and tolerance (spec: "Receptori").
/// rho is kept per substance, not per substance and channel, because gamma is the same everywhere.
/// </summary>
public sealed class ReceptorState
{
    private const int N = Substances.Count;
    private readonly ReceptorConfig _config;
    private readonly double _baseline;

    public ReceptorState(EngineConfig config)
    {
        _config = config.Receptors;
        _baseline = config.Concentration.Baseline;
        Array.Fill(MeanC, _baseline);
        Array.Fill(Rho, 1.0);
    }

    /// <summary>Long-term mean C-bar per substance.</summary>
    public double[] MeanC { get; } = new double[N];

    /// <summary>Receptor density rho per substance. Below 1 is tolerance, above 1 is sensitization.</summary>
    public double[] Rho { get; } = new double[N];

    public double Baseline => _baseline;

    /// <summary>Advances C-bar and rho by one tick. Call after the concentrations have stepped.</summary>
    public void Step(ReadOnlySpan<double> c)
    {
        for (var i = 0; i < N; i++)
        {
            MeanC[i] += (c[i] - MeanC[i]) / _config.TauAvg;
            var target = Math.Clamp(1 - _config.Gamma * (MeanC[i] - _baseline), _config.RhoMin, _config.RhoMax);
            Rho[i] += (target - Rho[i]) / _config.TauRho;
        }
    }

    public static double Occupancy(double c, double ec50, double n)
    {
        var cn = Math.Pow(c, n);
        return cn / (Math.Pow(ec50, n) + cn);
    }

    /// <summary>
    /// Delta_{k,i} for one channel term. Exactly zero at baseline with rho = 1.
    /// Hill: Emax (rho occ(C) - occ(b)).
    /// Inverted U: Emax (1 - ((x - x*) / (x_b - x*))^2), with x = rho occ(C), peak at x* = occ(peak).
    /// </summary>
    public double Delta(ChannelTerm term, double c)
    {
        var x = Rho[term.Index] * Occupancy(c, term.Ec50, term.N);
        var xb = Occupancy(_baseline, term.Ec50, term.N);
        if (!term.InvertedU)
        {
            return term.Emax * (x - xb);
        }

        var xPeak = Occupancy(term.Peak, term.Ec50, term.N);
        var u = (x - xPeak) / (xb - xPeak);
        return term.Emax * (1 - u * u);
    }
}

/// <summary>A parsed channel term from config.yaml.</summary>
public readonly record struct ChannelTerm(int Index, double Emax, double Ec50, double N, bool InvertedU, double Peak)
{
    public static ChannelTerm From(ChannelTermConfig c) =>
        new((int)Substances.Parse(c.Substance), c.Emax, c.Ec50, c.N, c.Shape == "inverted_u", c.Peak);
}
