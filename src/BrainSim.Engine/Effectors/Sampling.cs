using BrainSim.Engine.Config;
using BrainSim.Engine.Llm;

namespace BrainSim.Engine.Effectors;

/// <summary>
/// E2 sampling (spec: "E2. Sampling"). Computed once per response from the chemistry at its start (D-04).
/// </summary>
public static class Sampling
{
    public static SamplingParameters Compute(IReadOnlyList<double> c, SamplingConfig config)
    {
        double C(Substance s) => c[(int)s];

        // Adaptive gain (Aston-Jones and Cohen): focus is best at moderate NE, worse in both directions.
        var ne = C(Substance.Norepinephrine) - config.NeOptimum;
        var temperature = Math.Clamp(config.T0 * (1 + config.BetaT * ne * ne), config.TMin, config.TMax);

        // Up to 30% shorter when CORT and NE are both elevated, or when ADO is high.
        var stress = Elevation(Math.Min(C(Substance.Cortisol), C(Substance.Norepinephrine)), config.ElevatedRange);
        var fatigue = Elevation(C(Substance.Adenosine), config.AdenosineRange);
        var cut = config.MaxLengthCut * Math.Clamp(stress + fatigue, 0, 1);
        var maxTokens = (int)Math.Round(config.MaxTokens * (1 - cut));

        // More conservative generation when GABA or ADO is high.
        var inhibition = Math.Max(Elevation(C(Substance.Gaba), config.ElevatedRange), fatigue);
        var topP = config.TopPMax - (config.TopPMax - config.TopPMin) * inhibition;

        return new SamplingParameters(temperature, topP, maxTokens);
    }

    /// <summary>0 at baseline, 1 at baseline + range, linear in between.</summary>
    private static double Elevation(double concentration, double range) => Math.Clamp((concentration - 1) / range, 0, 1);
}
