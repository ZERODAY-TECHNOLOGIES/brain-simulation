using BrainSim.Engine.Config;
using BrainSim.Engine.Effectors;

namespace BrainSim.Engine.Tests;

/// <summary>The six steering channels, turned into the mood note the model gets in reduced mode (D-38).</summary>
public class MoodNoteTests
{
    private static readonly string[] Names = ["calm_anxious", "warm_cold", "energetic_tired", "plasticity"];

    private static readonly MoodConfig Mood = new()
    {
        Threshold = 0.1,
        Strong = 0.3,
        MaxChannels = 2,
        Channels = new()
        {
            ["calm_anxious"] = new() { Positive = new() { Feeling = "calm", Shows = "stay steady" }, Negative = new() { Feeling = "tense", Shows = "keep it short" } },
            ["warm_cold"] = new() { Positive = new() { Feeling = "warm", Shows = "be friendly" }, Negative = new() { Feeling = "cold", Shows = "keep your distance" } },
            ["energetic_tired"] = new() { Positive = new() { Feeling = "lively", Shows = "say a bit more" }, Negative = new() { Feeling = "tired", Shows = "say little" } },
        },
    };

    private static string? Describe(params double[] m) => MoodNote.Describe(Names, m, Mood);

    [Fact]
    public void Near_rest_there_is_no_note()
    {
        Assert.Null(Describe(0.05, -0.09, 0, 0));
    }

    [Fact]
    public void A_negative_channel_gives_its_negative_feeling_and_how_it_shows()
    {
        Assert.Equal("Right now you feel tense. Let it show: keep it short.", Describe(-0.2, 0, 0, 0));
    }

    [Fact]
    public void A_positive_channel_gives_its_positive_pole()
    {
        Assert.Equal("Right now you feel warm. Let it show: be friendly.", Describe(0, 0.15, 0, 0));
    }

    [Fact]
    public void A_strong_deviation_is_very()
    {
        Assert.Equal("Right now you feel very tense. Let it show: keep it short.", Describe(-0.3, 0, 0, 0));
    }

    [Fact]
    public void Only_the_largest_channels_are_named_largest_first()
    {
        Assert.Equal(
            "Right now you feel very cold, and tense. Let it show: keep your distance; keep it short.",
            Describe(-0.2, -0.4, 0.15, 0));
    }

    [Fact]
    public void A_channel_without_mood_phrases_is_left_out()
    {
        Assert.Null(Describe(0, 0, 0, 0.9)); // plasticity is computed but has no mood
    }

    [Fact]
    public void The_shipped_config_names_all_six_steering_channels()
    {
        var config = EngineConfig.LoadDefault();
        Assert.Equal(
            ["calm_anxious", "curious_apathetic", "focused_distracted", "warm_cold", "cautious_bold", "energetic_tired"],
            config.Mood.Channels.Keys);
        Assert.All(config.Mood.Channels.Values, c => Assert.False(string.IsNullOrWhiteSpace(c.Negative.Shows)));
    }
}
