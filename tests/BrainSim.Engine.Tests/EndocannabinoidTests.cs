using BrainSim.Engine.Appraisal;
using BrainSim.Engine.Config;
using BrainSim.Engine.Endocannabinoids;
using BrainSim.Engine.Llm;
using BrainSim.Engine.Memory;
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
        Assert.Equal(Config.Dreams.Detension, ecb.Detension(Config.Dreams.Detension));
    }

    [Theory]
    [InlineData(Substance.Norepinephrine, 0.1, 0.8)]   // 1 - 2.0 * 0.1
    [InlineData(Substance.Cortisol, 0.1, 0.8)]
    [InlineData(Substance.Dopamine, 0.1, 1.1)]         // 1 + 1.0 * 0.1
    [InlineData(Substance.Oxytocin, 0.1, 1.0)]         // not braked
    [InlineData(Substance.Norepinephrine, 1.0, 0.25)]  // clamped at gain_min
    [InlineData(Substance.Norepinephrine, -1.0, 2.0)]  // clamped at gain_max
    public void Gain_from_tone_matches_config(Substance substance, double cb1, double expected) =>
        Assert.Equal(expected, new EndocannabinoidSystem(Config).Gain(substance, cb1), 12);

    [Fact]
    public void Low_aea_makes_sleep_harder()
    {
        var rest = new Brain(Config, Noon);
        var stressed = new Brain(Config, Noon);
        stressed.Chemistry.Hold[Aea] = 0.5;

        rest.Tick(Noon.AddSeconds(0.1));
        stressed.Tick(Noon.AddSeconds(0.1));

        // P gains sleep_pressure * (AEA - its target): the daily rhythm alone does not move P, a dip below it does.
        var expected = Config.Endocannabinoid.SleepPressure * (0.5 - rest.Chemistry.C[Aea]);
        Assert.Equal(expected, stressed.SleepPressure - rest.SleepPressure, 3);
        Assert.True(expected < 0);
    }

    [Fact]
    public void Short_night_makes_the_2ag_peak_higher_and_later()
    {
        var full = DayAfter(awakeUntilHour: null);
        var shortNight = DayAfter(awakeUntilHour: 3.0);

        // Hanlon et al. 2016: after short nights the 2-AG peak is higher and comes later.
        Assert.True(shortNight.Peak > full.Peak + 0.05, $"peak {shortNight.Peak} after a short night, {full.Peak} after a full one");
        Assert.True(shortNight.At - full.At >= TimeSpan.FromHours(1), $"peak at {shortNight.At} after a short night, {full.At} after a full one");
    }

    [Fact]
    public void Held_2ag_builds_cb1_tolerance_and_recovers()
    {
        var receptors = new ReceptorState(Config);
        var ecb = new EndocannabinoidSystem(Config);
        var c = Enumerable.Repeat(1.0, Substances.Count).ToArray();
        c[TwoAg] = 1.4;
        ecb.Compute(c, receptors);
        var first = ecb.Cb1;

        for (var t = 0; t < 200_000; t++)
        {
            receptors.Step(c);
        }

        ecb.Compute(c, receptors);

        // Hirvonen et al. 2012: about 20% fewer CB1 receptors with chronic use. The brake fades with them.
        Assert.InRange(receptors.Rho[TwoAg], 0.75, 0.85);
        Assert.True(ecb.Cb1 < first / 2, $"CB1 tone {ecb.Cb1} after the hold, {first} before");

        c[TwoAg] = 1.0;
        for (var t = 0; t < 200_000; t++)
        {
            receptors.Step(c);
        }

        Assert.True(receptors.Rho[TwoAg] > 0.95, $"rho only back to {receptors.Rho[TwoAg]}");
    }

    [Fact]
    public void Ecb_speeds_up_dream_detension()
    {
        var night = new DateTime(2026, 11, 5, 1, 0, 0);
        var fake = new FakeLmStudio(replyTokens: 40);
        var client = new LmStudioClient(new HttpClient(fake) { BaseAddress = new Uri(Config.Llm.BaseUrl) }, Config.Llm);
        var agent = new Agent(Config, client, new Dictionary<string, LinearProbe>(), () => night);
        var stressed = Enumerable.Repeat(1.0, Substances.Count).ToArray();
        stressed[Ne] = 2.0;
        stressed[Cort] = 1.5;
        string[] texts = ["the argument about the broken window", "someone shouting in the hallway", "a door slammed at night"];
        foreach (var (text, i) in texts.Select((t, i) => (t, i)))
        {
            agent.Memory.AddEpisode(text, null, stressed, default, night.AddHours(-3 + i));
        }

        double Tag(string text) => agent.Memory.Snapshot().Single(c => c.Text == text).Tag;
        var before = texts.ToDictionary(t => t, Tag);
        agent.Brain.Chemistry[Substance.Adenosine] = Config.Sleep.ForcedAdenosine; // sleep on the next tick

        // 2-AG high from just before the first REM (minutes 81 to 90) to its end. Held for hours, CB1 tolerance
        // would cancel the tone (rho falls toward 0.5), so the hold is short.
        agent.Brain.AdvanceTo(night.AddMinutes(75));
        agent.Brain.Chemistry.Hold[TwoAg] = 2.0;
        agent.Brain.AdvanceTo(night.AddMinutes(91));

        // Marsicano et al. 2002: CB1 drives the extinction of aversive memories. Each seed loses more than the 20% of T9.
        var dream = Assert.Single(agent.Dreamer.Journal);
        Assert.Equal(texts.Order(), dream.Seeds.Order());
        Assert.All(texts, t => Assert.True(Tag(t) < before[t] * (1 - Config.Dreams.Detension) - 1e-9, $"'{t}': tag {Tag(t)} from {before[t]}"));
    }

    /// <summary>
    /// From 07:00 on day 0 to the evening of day 1. With <paramref name="awakeUntilHour"/> the user keeps the agent up
    /// with a message every 10 minutes until that hour of the night. Both wake by 07:30 on day 1.
    /// </summary>
    private static (double Peak, TimeSpan At) DayAfter(double? awakeUntilHour)
    {
        var start = new DateTime(2026, 11, 5, 7, 0, 0);
        var brain = new Brain(Config, start);
        if (awakeUntilHour is { } hour)
        {
            brain.AdvanceTo(start.AddHours(14));
            for (var t = start.AddHours(14); t < start.Date.AddDays(1).AddHours(hour); t = t.AddMinutes(10))
            {
                brain.AdvanceTo(t);
                brain.OnUserMessage(t);
            }
        }

        var wake = start.Date.AddDays(1).AddHours(7.5);
        brain.AdvanceTo(wake);
        brain.OnUserMessage(wake);
        var (peak, at) = (0.0, wake);
        for (var t = wake; t < wake.AddHours(13); t = t.AddMinutes(1))
        {
            brain.AdvanceTo(t);
            if (brain.Chemistry.C[TwoAg] > peak)
            {
                (peak, at) = (brain.Chemistry.C[TwoAg], t);
            }
        }

        return (peak, at - wake.Date);
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
