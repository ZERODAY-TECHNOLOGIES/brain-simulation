using BrainSim.Engine.Config;
using BrainSim.Engine.Receptors;

namespace BrainSim.Engine.Endocannabinoids;

/// <summary>
/// CB1 tone and the brake it puts on release (docs/endocannabinoids.md, D-36). Not in the spec.
/// cb1 = sum of Delta over the AEA and 2-AG terms, zero at rest. gain_i = clamp(1 + r_i cb1).
/// </summary>
public sealed class EndocannabinoidSystem
{
    private readonly ChannelTerm[] _cb1;
    private readonly double[] _release = new double[Substances.Count];
    private readonly double _gainMin;
    private readonly double _gainMax;
    private readonly EndocannabinoidConfig _config;

    public EndocannabinoidSystem(EngineConfig config)
    {
        var ecb = config.Endocannabinoid;
        _config = ecb;
        _cb1 = [.. ecb.Cb1.Select(ChannelTerm.From)];
        foreach (var (substance, r) in ecb.ReleaseGain)
        {
            _release[(int)Substances.Parse(substance)] = r;
        }

        _gainMin = ecb.GainMin;
        _gainMax = ecb.GainMax;
    }

    /// <summary>CB1 tone: above 0 brakes stress release, below 0 buffers less.</summary>
    public double Cb1 { get; private set; }

    /// <summary>Call after the receptors have stepped. Not divided by the GABA damping: it is a receptor tone, not an output.</summary>
    public void Compute(ReadOnlySpan<double> c, ReceptorState receptors)
    {
        var sum = 0.0;
        foreach (var term in _cb1)
        {
            sum += receptors.Delta(term, c[term.Index]);
        }

        Cb1 = sum;
    }

    /// <summary>The factor on endogenous release of <paramref name="substance"/> at the current tone.</summary>
    public double Gain(Substance substance) => Gain(substance, Cb1);

    /// <summary>The factor at a given tone, for a frame read from the journal.</summary>
    public double Gain(Substance substance, double cb1) =>
        Math.Clamp(1 + _release[(int)substance] * cb1, _gainMin, _gainMax);

    /// <summary>The share of an emotional tag a dream seed loses: CB1 drives extinction (Marsicano et al. 2002).</summary>
    public double Detension(double detension) =>
        Math.Min(_config.DetensionMax, detension * (1 + _config.ExtinctionGain * Math.Max(0, Cb1)));

    /// <summary>P term: AEA promotes sleep. Measured from its target, so the daily rhythm alone does not move P.</summary>
    public double SleepPressure(double aea, double aeaTarget) => _config.SleepPressure * (aea - aeaTarget);

    /// <summary>Where the 2-AG target sits above or below rest at clock phase <paramref name="phi"/>.</summary>
    public double TwoAgRhythm(double phi, double lastSleepHours)
    {
        var debt = Math.Clamp(1 - lastSleepHours / _config.FullNightHours, 0, 1);
        return Rhythm(_config.TwoAgRhythm, phi, debt);
    }

    /// <summary>Where the AEA target sits above or below rest at clock phase <paramref name="phi"/>.</summary>
    public double AeaRhythm(double phi) => Rhythm(_config.AeaRhythm, phi, 0);

    private static double Rhythm(RhythmConfig r, double phi, double debt) =>
        r.Amplitude * (1 + r.DebtAmplitude * debt) * Math.Cos(2 * Math.PI * (phi - r.PeakHour - r.DebtDelayHours * debt) / 24);
}
