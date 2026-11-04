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
}
