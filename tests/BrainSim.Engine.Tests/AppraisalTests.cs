using BrainSim.Engine.Appraisal;
using BrainSim.Engine.Chemistry;
using BrainSim.Engine.Config;

using static BrainSim.Engine.Tests.Vectors;

namespace BrainSim.Engine.Tests;

public class AppraisalTests
{
    private static readonly EngineConfig Config = EngineConfig.LoadDefault() with { Interactions = [] };

    // Toy probes on a 4-dimensional "embedding": each probe reads one axis.
    private static readonly Dictionary<string, LinearProbe> Probes = new()
    {
        ["hostility"] = Axis(0),
        ["warmth"] = Axis(1),
        ["positive"] = Axis(2),
        ["negative"] = Axis(3),
    };

    private static LinearProbe Axis(int i)
    {
        var w = new float[4];
        w[i] = 20;
        return new LinearProbe(w, -10, 1, "test");
    }

    [Fact]
    public void Dopamine_follows_the_reward_prediction_error_not_the_raw_reward()
    {
        var appraiser = new Appraiser(Config.Appraisal, Probes);
        var praise = Unit(0, 0, 1, 0);

        var first = appraiser.Appraise(praise, hasPreviousResponse: true);
        Assert.Equal(1.0, first.Reward!.Value, 3);
        Assert.Equal(1.0, first.RewardError, 3);
        appraiser.LearnExpectedReward();

        // The same praise again is now partly expected, so the error shrinks.
        var second = appraiser.Appraise(praise, hasPreviousResponse: true);
        Assert.Equal(0.9, second.RewardError, 3);
        appraiser.LearnExpectedReward();
        Assert.Equal(0.19, appraiser.ExpectedReward, 3);

        // Less reward than expected pushes DA below baseline.
        var neutral = appraiser.Appraise(Unit(1, 1, 0.5f, 0.5f), hasPreviousResponse: true);
        Assert.True(neutral.RewardError < 0);
        var chemistry = new ChemicalState(Config);
        appraiser.Release(neutral, chemistry);
        for (var t = 0; t < Config.Time.ImpulseSpreadTicks; t++)
        {
            chemistry.Step();
        }

        Assert.True(chemistry[Substance.Dopamine] < 1.0);
    }

    [Fact]
    public void First_message_has_no_reward()
    {
        var signals = new Appraiser(Config.Appraisal, Probes).Appraise(Unit(0, 0, 1, 0), hasPreviousResponse: false);
        Assert.Null(signals.Reward);
        Assert.Equal(0, signals.RewardError);
    }

    [Fact]
    public void Hostility_releases_cortisol_and_norepinephrine_warmth_releases_oxytocin()
    {
        var appraiser = new Appraiser(Config.Appraisal, Probes);
        var hostile = appraiser.Appraise(Unit(1, 0, 0, 0), hasPreviousResponse: false);
        var warm = appraiser.Appraise(Unit(0, 1, 0, 0), hasPreviousResponse: false);
        Assert.InRange(hostile.Threat, 0.99, 1.0);
        Assert.Equal(0, hostile.Warmth, 3);
        Assert.InRange(warm.Warmth, 0.99, 1.0);

        var chemistry = new ChemicalState(Config);
        appraiser.Release(hostile with { Surprise = 0 }, chemistry);
        appraiser.Release(warm with { Surprise = 0 }, chemistry);
        for (var t = 0; t < Config.Time.ImpulseSpreadTicks; t++)
        {
            chemistry.Step();
        }

        Assert.True(chemistry[Substance.Cortisol] > 1.25);
        Assert.True(chemistry[Substance.Norepinephrine] > 1.5);
        Assert.True(chemistry[Substance.Oxytocin] > 1.7);
    }

    [Fact]
    public void Repeating_a_message_is_not_surprising_but_a_new_topic_is()
    {
        var appraiser = new Appraiser(Config.Appraisal, new Dictionary<string, LinearProbe>());
        var random = new Random(3);
        var topic = Unit(1, 0, 0, 0);

        // Small variations around one topic build up the novelty statistics.
        for (var i = 0; i < 10; i++)
        {
            var near = Unit(1, (float)random.NextDouble() * 0.3f, (float)random.NextDouble() * 0.3f, 0);
            appraiser.Appraise(near, hasPreviousResponse: false);
        }

        appraiser.Appraise(topic, hasPreviousResponse: false);
        Assert.Equal(0, appraiser.Appraise(topic, hasPreviousResponse: false).Surprise);
        Assert.True(appraiser.Appraise(Unit(0, 0, 0, 1), hasPreviousResponse: false).Surprise > 2);
    }

    [Fact]
    public void Missing_probes_read_as_zero()
    {
        var signals = new Appraiser(Config.Appraisal, new Dictionary<string, LinearProbe>()).Appraise(Unit(1, 1, 1, 1), hasPreviousResponse: true);
        Assert.Equal((0.0, 0.0, 0.0), (signals.Threat, signals.Warmth, signals.Reward!.Value));
    }

    [Fact]
    public void Criticism_after_a_response_is_a_negative_reward()
    {
        var signals = new Appraiser(Config.Appraisal, Probes).Appraise(Unit(0, 0, 0, 1), hasPreviousResponse: true);
        Assert.Equal(-1.0, signals.Reward!.Value, 3);
    }

    [Fact]
    public void Surprise_is_the_z_score_of_novelty_once_enough_samples_exist()
    {
        // Novelty per message: -, 0, 0, 1, 1. The fifth message is the first with 3 novelty values behind it
        // (0, 0, 1: mean 1/3, std sqrt(2)/3), so its surprise is (1 - 1/3) / (sqrt(2)/3) = sqrt(2).
        var surprises = Surprises(Config.Appraisal with { SurpriseMinSamples = 3 }, 0, 0, 0, 1, 2);
        Assert.Equal([0, 0, 0, 0, Math.Sqrt(2)], surprises, (a, b) => Math.Abs(a - b) < 1e-9);
    }

    [Fact]
    public void Messages_older_than_the_window_are_forgotten()
    {
        // Window 3. Axis 0 leaves the window after message 5, so returning to it is novel again:
        // novelty 1 against the recent values 1, 0, 1 (mean 2/3, std sqrt(2)/3) gives 1/sqrt(2).
        var surprises = Surprises(Config.Appraisal with { SurpriseWindow = 3, SurpriseMinSamples = 3 }, 0, 0, 1, 1, 2, 0);
        Assert.Equal([0, 0, 0, 0, Math.Sqrt(2), 1 / Math.Sqrt(2)], surprises, (a, b) => Math.Abs(a - b) < 1e-9);
    }

    [Fact]
    public void A_restored_appraiser_continues_where_the_saved_one_was()
    {
        var saved = new Appraiser(Config.Appraisal, Probes);
        foreach (var axis in new[] { 0, 0, 0, 1, 1, 0 })
        {
            saved.Appraise(Basis(axis), hasPreviousResponse: false);
        }

        // The target has its own, different history, which the restore must replace.
        var restored = new Appraiser(Config.Appraisal, Probes);
        foreach (var axis in new[] { 3, 2, 1 })
        {
            restored.Appraise(Basis(axis), hasPreviousResponse: false);
        }

        restored.Restore(saved.Save());

        // A new topic is surprising, and a topic from the saved history is not.
        double[] expected = [.. new[] { 3, 1 }.Select(a => saved.Appraise(Basis(a), hasPreviousResponse: false).Surprise)];
        double[] actual = [.. new[] { 3, 1 }.Select(a => restored.Appraise(Basis(a), hasPreviousResponse: false).Surprise)];
        Assert.Equal([2, 0], expected, (a, b) => Math.Abs(a - b) < 1e-9);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void SetGain_replaces_an_existing_pair_and_adds_a_new_one()
    {
        var appraiser = new Appraiser(Config.Appraisal, Probes);
        var original = appraiser.Gains.ToList();
        var (signal, substance, _) = original[0];

        appraiser.SetGain(signal, substance, 5);
        appraiser.SetGain("threat", Substance.Dopamine, 0.7);

        Assert.Equal([(signal, substance, 5.0), .. original.Skip(1), ("threat", Substance.Dopamine, 0.7)], appraiser.Gains);
    }

    [Fact]
    public void Release_scales_each_gain_by_its_signal()
    {
        var appraiser = new Appraiser(Config.Appraisal, Probes);
        var none = new AppraisalSignals(Surprise: 0, Reward: null, RewardError: 0, Threat: 0, Warmth: 0, ExpectedReward: 0);

        double Rise(AppraisalSignals signals, Substance substance)
        {
            var released = new ChemicalState(Config);
            var control = new ChemicalState(Config);
            appraiser.Release(signals, released);
            for (var t = 0; t < Config.Time.ImpulseSpreadTicks; t++)
            {
                released.Step();
                control.Step();
            }

            return released[substance] - control[substance];
        }

        var once = Rise(none with { Surprise = 1 }, Substance.Acetylcholine);
        Assert.True(once > 0);
        Assert.Equal(2 * once, Rise(none with { Surprise = 2 }, Substance.Acetylcholine), 9);

        // V adds serotonin only while it is positive.
        Assert.True(Rise(none with { ExpectedReward = 1 }, Substance.Serotonin) > 0);
        Assert.Equal(0, Rise(none with { ExpectedReward = -1 }, Substance.Serotonin), 9);
    }

    private static double[] Surprises(AppraisalConfig config, params int[] axes)
    {
        var appraiser = new Appraiser(config, new Dictionary<string, LinearProbe>());
        return [.. axes.Select(a => appraiser.Appraise(Basis(a), hasPreviousResponse: false).Surprise)];
    }

    private static float[] Basis(int axis)
    {
        var v = new float[4];
        v[axis] = 1;
        return v;
    }
}
