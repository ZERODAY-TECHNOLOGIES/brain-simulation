using BrainSim.Engine.Appraisal;
using BrainSim.Engine.Config;

namespace BrainSim.Engine.Tests;

public class LabTests
{
    private static readonly EngineConfig Config = EngineConfig.LoadDefault();
    private static readonly DateTime Noon = new(2026, 11, 4, 12, 0, 0);

    [Fact]
    public void A_moved_baseline_becomes_the_new_resting_level_and_reset_undoes_it()
    {
        // Compared against an untouched brain, since hours awake move 5-HT a little on their own (ADO, NE, CORT).
        var control = new Brain(Config, Noon);
        var brain = new Brain(Config, Noon);
        brain.Chemistry.SetResting(Substance.Serotonin, 1.5);

        // Soon after the change, receptors still measure from 1.0, so the higher set point reads as calmer.
        control.AdvanceTo(Noon.AddMinutes(10));
        brain.AdvanceTo(Noon.AddMinutes(10));
        Assert.True(brain.Modulation["calm_anxious"] > control.Modulation["calm_anxious"] + 0.02);

        // Two hours later 5-HT sits 0.5 higher, but tolerance has pulled rho down and taken most of the effect back.
        control.AdvanceTo(Noon.AddHours(2));
        brain.AdvanceTo(Noon.AddHours(2));
        var lift = brain.Chemistry[Substance.Serotonin] - control.Chemistry[Substance.Serotonin];
        Assert.InRange(lift, 0.45, 0.55);
        Assert.True(brain.Receptors.Rho[(int)Substance.Serotonin] < 0.85);

        // Reset also undoes everything else the Lab changes: half-lives, W (an entry config.yaml leaves at 0 too) and holds.
        brain.Chemistry.SetHalfLife(Substance.Serotonin, 1e9);
        brain.Chemistry.SetInteraction(Substance.Adenosine, Substance.Serotonin, 0.001);
        brain.Chemistry.Hold[(int)Substance.Serotonin] = 1.5;
        brain.Chemistry.ResetParameters();
        control.AdvanceTo(Noon.AddHours(4));
        brain.AdvanceTo(Noon.AddHours(4));
        Assert.InRange(brain.Chemistry[Substance.Serotonin] - control.Chemistry[Substance.Serotonin], -0.02, 0.02);
    }

    [Fact]
    public void Changed_gains_change_the_release_and_reset_restores_config()
    {
        var appraiser = new Appraiser(Config.Appraisal, new Dictionary<string, LinearProbe>());
        var signals = new AppraisalSignals(Surprise: 0, Reward: null, RewardError: 0, Threat: 1, Warmth: 0, ExpectedReward: 0);

        double CortisolAfterRelease()
        {
            var brain = new Brain(Config with { Interactions = [] }, Noon);
            appraiser.Release(signals, brain.Chemistry);
            brain.AdvanceTo(Noon.AddSeconds(1));
            return brain.Chemistry[Substance.Cortisol];
        }

        var normal = CortisolAfterRelease();
        appraiser.SetGain("threat", Substance.Cortisol, 0.9);
        var tripled = CortisolAfterRelease();
        appraiser.ResetGains();

        Assert.Equal(3 * (normal - 1), tripled - 1, 3);
        Assert.Equal(normal, CortisolAfterRelease(), 9);
    }
}
