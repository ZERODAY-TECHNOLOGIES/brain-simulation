using BrainSim.Engine.Circadian;
using BrainSim.Engine.Config;
using BrainSim.Engine.Sleep;

namespace BrainSim.Engine.Tests;

/// <summary>
/// Spec T8: a full day in simulated time. MEL only in the dark window, sleep onset and wake by the rules,
/// cycles that reproduce the hypnogram, ADO under 1.2 after a full night.
/// </summary>
public class T8ClockAndSleepTests
{
    private static readonly EngineConfig Config = EngineConfig.LoadDefault();
    private static readonly DateTime Morning = new(2026, 11, 4, 8, 0, 0);
    private static readonly TimeSpan IdleTick = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan TokenTick = TimeSpan.FromMilliseconds(50);

    [Fact]
    public void Hypnogram_matches_the_spec_figure()
    {
        var architecture = new SleepArchitecture(Config.Sleep);
        double[] remMinutes = [9, 16.2, 23.4, 30.6, 37.8];
        double[] n3Minutes = [36, 27.9, 19.8, 11.7, 3.6];

        for (var cycle = 0; cycle < 5; cycle++)
        {
            var f = architecture.Fractions(cycle);
            Assert.Equal(remMinutes[cycle], f.Rem * 90, 6);
            Assert.Equal(n3Minutes[cycle], f.N3 * 90, 6);
            Assert.Equal(4.5, f.N1 * 90, 6);
        }

        // Cycle 1 order: N1 (0 to 4.5 min), N2 (to 45), N3 (to 81), REM (to 90).
        Assert.Equal(SleepPhase.N1, architecture.PhaseAt(TimeSpan.FromMinutes(2)).Phase);
        Assert.Equal(SleepPhase.N2, architecture.PhaseAt(TimeSpan.FromMinutes(30)).Phase);
        Assert.Equal(SleepPhase.N3, architecture.PhaseAt(TimeSpan.FromMinutes(60)).Phase);
        Assert.Equal(SleepPhase.Rem, architecture.PhaseAt(TimeSpan.FromMinutes(85)).Phase);
        Assert.Equal((SleepPhase.N1, 1), architecture.PhaseAt(TimeSpan.FromMinutes(91)));

        // Past cycle 5, N3 is gone and the fractions still add up.
        var late = architecture.Fractions(7);
        Assert.Equal(0, late.N3);
        Assert.Equal(1.0, late.N1 + late.N2 + late.N3 + late.Rem, 9);
        Assert.True(late.N2 >= 0);
    }

    [Fact]
    public void Alertness_peaks_at_18_and_bottoms_at_06()
    {
        Assert.Equal(0.5, CircadianClock.Alertness(18), 9);
        Assert.Equal(-0.5, CircadianClock.Alertness(6), 9);
    }

    [Fact]
    public void Pineal_secretes_only_in_the_dark_and_light_suppresses_it()
    {
        var clock = new CircadianClock(Config.Circadian);
        Assert.Equal(0.5, clock.Darkness(21.5), 9);
        Assert.Equal(1.0, clock.Darkness(2), 9);
        Assert.Equal(0.5, clock.Darkness(6.5), 9);
        Assert.Equal(0.0, clock.Darkness(12), 9);

        var night = new DateTime(2026, 11, 4, 23, 0, 0);
        Assert.True(clock.MelatoninProduction(night) > 0);
        clock.OnLight(night);
        Assert.Equal(0, clock.MelatoninProduction(night.AddMinutes(29)));
        Assert.True(clock.MelatoninProduction(night.AddMinutes(31)) > 0);
    }

    [Fact]
    public void A_full_day_follows_the_rules()
    {
        var brain = new Brain(Config, Morning);
        var transitions = new List<SleepTransition>();
        brain.Transition += transitions.Add;

        // A conversation every hour from 08:00 to 21:00, 100-token replies.
        for (var message = Morning; message <= Morning.AddHours(13); message = message.AddHours(1))
        {
            brain.AdvanceTo(message);
            Converse(brain, tokens: 100);
        }

        var remByCycle = new Dictionary<int, double>();
        var maxMelatonin = 0.0;
        double? adenosineAtWake = null;
        var cortisolAfterWake = 0.0;
        var end = Morning.AddHours(24);

        while (brain.Now < end)
        {
            brain.Tick(brain.Now + IdleTick);
            var phi = brain.Clock.Phase(brain.Now);
            if (brain.Clock.MelatoninProduction(brain.Now) > 0)
            {
                Assert.True(brain.Clock.InDarkWindow(phi), $"MEL produced outside the dark window at {brain.Now:t}");
            }

            maxMelatonin = Math.Max(maxMelatonin, brain.Chemistry[Substance.Melatonin]);
            if (brain.Phase == SleepPhase.Rem)
            {
                remByCycle[brain.Cycle] = remByCycle.GetValueOrDefault(brain.Cycle) + IdleTick.TotalMinutes;
            }

            if (transitions.Count == 2)
            {
                adenosineAtWake ??= brain.Chemistry[Substance.Adenosine];
                if (brain.Now <= transitions[1].Time.AddMinutes(30))
                {
                    cortisolAfterWake = Math.Max(cortisolAfterWake, brain.Chemistry[Substance.Cortisol]);
                }
            }
        }

        Assert.Equal([TransitionKind.FellAsleep, TransitionKind.WokeNaturally], transitions.Select(t => t.Kind));
        var onset = transitions[0].Time;
        var wake = transitions[1].Time;
        Assert.InRange(onset, Morning.AddHours(13.5), Morning.AddHours(16));      // 21:30 to 24:00
        Assert.InRange(wake, Morning.AddHours(23), Morning.AddHours(24));         // 07:00 to 08:00

        Assert.True(maxMelatonin > Config.Sleep.OnsetMelatonin);
        double[] spec = [9, 16.2, 23.4, 30.6, 37.8];
        for (var cycle = 0; cycle < 5; cycle++)
        {
            Assert.Equal(spec[cycle], remByCycle[cycle], 1);
        }

        Assert.NotNull(adenosineAtWake);
        Assert.True(adenosineAtWake < 1.2, $"ADO at wake was {adenosineAtWake}");
        // Secretion is sized for +50%. The CORT and 5-HT loop in W adds roughly another 0.1 on top.
        Assert.InRange(cortisolAfterWake, 1.35, 1.75);
    }

    [Fact]
    public void A_quiet_day_still_ends_in_sleep()
    {
        // No conversation at all: only the awake ADO rise builds sleep pressure (D-23).
        var wakeUp = Morning.AddHours(-1);
        var brain = new Brain(Config, wakeUp);
        var transitions = new List<SleepTransition>();
        brain.Transition += transitions.Add;

        brain.AdvanceTo(wakeUp.AddHours(25));

        Assert.Equal([TransitionKind.FellAsleep, TransitionKind.WokeNaturally], transitions.Select(t => t.Kind));
        Assert.InRange(transitions[0].Time, wakeUp.AddHours(15), wakeUp.AddHours(17));   // 22:00 to 24:00
        Assert.InRange(transitions[1].Time, wakeUp.AddHours(24), wakeUp.AddHours(25));   // 07:00 to 08:00
    }

    [Fact]
    public void Forced_wake_from_deep_sleep_shows_sleep_inertia()
    {
        var night = new DateTime(2026, 11, 5, 1, 0, 0);
        var brain = new Brain(Config, night);
        var transitions = new List<SleepTransition>();
        brain.Transition += transitions.Add;
        brain.Chemistry[Substance.Adenosine] = Config.Sleep.ForcedAdenosine;

        brain.AdvanceTo(night.AddMinutes(70));
        Assert.Equal(SleepPhase.N3, brain.Phase);
        var neAsleep = brain.Chemistry[Substance.Norepinephrine];
        Assert.True(neAsleep < 0.35, $"NE in N3 was {neAsleep}");

        brain.OnUserMessage(brain.Now);
        Assert.Equal(new SleepTransition(brain.Now, TransitionKind.ForcedWake, SleepPhase.N3), transitions[^1]);

        // Concentrations start from the phase level and climb with their half-lives.
        brain.Tick(brain.Now + IdleTick);
        Assert.True(brain.Chemistry[Substance.Norepinephrine] < 0.4);
        brain.AdvanceTo(brain.Now.AddSeconds(20));                              // 200 ticks = 5 NE half-lives
        Assert.True(brain.Chemistry[Substance.Norepinephrine] > 0.9);
    }

    [Fact]
    public void Adenosine_at_the_limit_forces_sleep_at_any_hour_but_not_mid_response()
    {
        var noon = Morning.AddHours(4);
        var brain = new Brain(Config, noon);
        brain.Chemistry[Substance.Adenosine] = Config.Sleep.ForcedAdenosine;

        brain.IsGenerating = true;
        brain.Tick(noon + TokenTick);
        Assert.False(brain.Asleep);

        brain.IsGenerating = false;
        brain.Tick(brain.Now + IdleTick);
        Assert.True(brain.Asleep);
    }

    [Fact]
    public void Repeated_light_at_night_delays_the_clock_by_15_minutes()
    {
        var evening = new DateTime(2026, 11, 4, 20, 0, 0);
        var lit = new Brain(Config, evening);
        var dark = new Brain(Config, evening);

        for (var message = evening.AddHours(1); message <= evening.AddHours(3); message = message.AddMinutes(20))
        {
            lit.AdvanceTo(message);
            lit.OnUserMessage(message);
        }

        lit.AdvanceTo(evening.AddHours(12));
        dark.AdvanceTo(evening.AddHours(12));

        Assert.Equal(0.25, lit.Clock.ShiftHours, 9);
        Assert.Equal(0.0, dark.Clock.ShiftHours, 9);
    }

    private static void Converse(Brain brain, int tokens)
    {
        brain.OnUserMessage(brain.Now);
        brain.IsGenerating = true;
        for (var t = 0; t < tokens; t++)
        {
            brain.Tick(brain.Now + TokenTick);
        }

        brain.IsGenerating = false;
        brain.OnResponseFinished(tokens);
    }
}
