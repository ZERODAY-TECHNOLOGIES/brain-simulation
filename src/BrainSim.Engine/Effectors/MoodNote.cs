using BrainSim.Engine.Config;

namespace BrainSim.Engine.Effectors;

/// <summary>
/// Reduced mode has no hook for E1 steering, so the steering channels reach the model as a note in its system prompt:
/// how it feels and how that shows (D-38). Channels near rest say nothing; with all of them near rest there is no note.
/// </summary>
public static class MoodNote
{
    public static string? Describe(IReadOnlyList<string> names, IReadOnlyList<double> m, MoodConfig config)
    {
        var felt = Felt(names, m, config);
        if (felt.Count == 0)
        {
            return null;
        }

        var feelings = felt.Select(f => (f.Strong ? "very " : "") + f.Pole.Feeling).ToList();
        var feeling = feelings.Count == 1 ? feelings[0] : string.Join(", ", feelings[..^1]) + ", and " + feelings[^1];
        return $"Right now you feel {feeling}. Let it show: {string.Join("; ", felt.Select(f => f.Pole.Shows))}.";
    }

    /// <summary>The channels with mood phrases that are far enough from rest, largest first, and the pole each one is at.</summary>
    private static List<(MoodPole Pole, bool Strong)> Felt(IReadOnlyList<string> names, IReadOnlyList<double> m, MoodConfig config) =>
        names
            .Select((name, k) => (Channel: config.Channels.GetValueOrDefault(name), M: m[k]))
            .Where(c => c.Channel is not null && Math.Abs(c.M) >= config.Threshold)
            .OrderByDescending(c => Math.Abs(c.M))
            .Take(config.MaxChannels)
            .Select(c => (c.M > 0 ? c.Channel!.Positive : c.Channel!.Negative, Math.Abs(c.M) >= config.Strong))
            .ToList();
}
