using BrainSim.Engine.Appraisal;
using BrainSim.Engine.Config;
using BrainSim.Engine.Endocannabinoids;
using BrainSim.Engine.Receptors;

namespace BrainSim.Engine.Tests;

/// <summary>Phase 8 acceptance tests from docs/endocannabinoids.md (D-36). Not in the spec, so the criteria are ours.</summary>
public class EndocannabinoidTests
{
    private static readonly EngineConfig Config = EngineConfig.LoadDefault();
    private static readonly DateTime Noon = new(2026, 11, 6, 12, 0, 0);
    private static readonly AppraisalSignals Threat = new(0, null, 0, 1, 0, 0);
    private const int Aea = (int)Substance.Anandamide;
    private const int TwoAg = (int)Substance.Arachidonoylglycerol;
    private const int Ne = (int)Substance.Norepinephrine;
    private const int Cort = (int)Substance.Cortisol;

    [Fact]
    public void Neutral_at_rest()
    {
        var ecb = new EndocannabinoidSystem(Config);
        var receptors = new ReceptorState(Config);

        ecb.Compute(Enumerable.Repeat(1.0, Substances.Count).ToArray(), receptors);

        Assert.Equal(0.0, ecb.Cb1);
        Assert.All(Substances.All, s => Assert.Equal(1.0, ecb.Gain(s)));
    }

    [Fact]
    public void Stress_ends_sooner_with_ecb_than_with_it_held_at_baseline()
    {
        var withEcb = TicksUntilCortisolHalves(blocked: false);
        var blocked = TicksUntilCortisolHalves(blocked: true);

        // Hill et al. 2011: blocking CB1 prolongs the cortisol response after stress ends.
        Assert.True(withEcb <= 0.8 * blocked, $"CORT halved after {withEcb} ticks with eCB and {blocked} with CB1 blocked");
    }

    [Fact]
    public void Buffering_follows_the_ecb_level()
    {
        var rest = PeaksAfterThreat(hold: null);
        var high2Ag = PeaksAfterThreat(hold: (TwoAg, 2.0));
        var lowAea = PeaksAfterThreat(hold: (Aea, 0.5));

        Assert.True(high2Ag.Ne < rest.Ne && high2Ag.Cort < rest.Cort, $"2-AG high: NE {high2Ag.Ne} CORT {high2Ag.Cort}, rest: NE {rest.Ne} CORT {rest.Cort}");
        Assert.True(lowAea.Ne > rest.Ne && lowAea.Cort > rest.Cort, $"AEA low: NE {lowAea.Ne} CORT {lowAea.Cort}, rest: NE {rest.Ne} CORT {rest.Cort}");
    }

    [Fact]
    public void Aea_dips_and_2ag_peaks_after_ne()
    {
        var (brain, appraiser) = Start();
        appraiser.Release(Threat, brain.Chemistry, brain.Endocannabinoids);

        var (minAea, neAt, twoAgAt, nePeak, twoAgPeak) = (double.MaxValue, 0, 0, 0.0, 0.0);
        for (var t = 1; t <= 3000; t++)
        {
            brain.Tick(Noon.AddSeconds(t / 10.0));
            var c = brain.Chemistry.C;
            minAea = Math.Min(minAea, c[Aea]);
            if (c[Ne] > nePeak) (nePeak, neAt) = (c[Ne], t);
            if (c[TwoAg] > twoAgPeak) (twoAgPeak, twoAgAt) = (c[TwoAg], t);
        }

        Assert.True(minAea < 0.9, $"AEA only fell to {minAea}");
        Assert.True(twoAgPeak > 1.05, $"2-AG only rose to {twoAgPeak}");
        Assert.True(twoAgAt > neAt, $"2-AG peaked at tick {twoAgAt}, NE at {neAt}");
    }

    private static (Brain Brain, Appraiser Appraiser) Start() =>
        (new Brain(Config, Noon), new Appraiser(Config.Appraisal, new Dictionary<string, LinearProbe>()));

    private static int TicksUntilCortisolHalves(bool blocked)
    {
        var (brain, appraiser) = Start();
        if (blocked)
        {
            brain.Chemistry.Hold[Aea] = 1.0;
            brain.Chemistry.Hold[TwoAg] = 1.0;
        }

        appraiser.Release(Threat, brain.Chemistry, brain.Endocannabinoids);
        var peak = 0.0;
        for (var t = 1; t <= 100_000; t++)
        {
            brain.Tick(Noon.AddSeconds(t / 10.0));
            var excess = brain.Chemistry.C[Cort] - brain.Chemistry.Baseline[Cort];
            peak = Math.Max(peak, excess);
            if (t > 10 && excess <= peak / 2)
            {
                return t;
            }
        }

        return int.MaxValue;
    }

    private static (double Ne, double Cort) PeaksAfterThreat((int Index, double Level)? hold)
    {
        var (brain, appraiser) = Start();
        if (hold is { } h)
        {
            brain.Chemistry.Hold[h.Index] = h.Level;
        }

        brain.Tick(Noon.AddSeconds(0.1)); // the held level is in place and CB1 has seen it
        appraiser.Release(Threat, brain.Chemistry, brain.Endocannabinoids);
        var (ne, cort) = (0.0, 0.0);
        for (var t = 2; t <= 100; t++)
        {
            brain.Tick(Noon.AddSeconds(t / 10.0));
            (ne, cort) = (Math.Max(ne, brain.Chemistry.C[Ne]), Math.Max(cort, brain.Chemistry.C[Cort]));
        }

        return (ne, cort);
    }
}
