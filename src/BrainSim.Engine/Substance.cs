namespace BrainSim.Engine;

/// <summary>The ten substances, in the order of the spec table.</summary>
public enum Substance
{
    Norepinephrine,
    Acetylcholine,
    Dopamine,
    Gaba,
    Oxytocin,
    Serotonin,
    Cortisol,
    GrowthHormone,
    Melatonin,
    Adenosine,
}

public static class Substances
{
    public const int Count = 10;

    /// <summary>Spec abbreviations, used by config.yaml and the UI.</summary>
    public static readonly IReadOnlyList<string> Abbreviations =
        ["NE", "ACh", "DA", "GABA", "OXT", "5-HT", "CORT", "GH", "MEL", "ADO"];

    public static readonly IReadOnlyList<Substance> All = Enum.GetValues<Substance>();

    public static string Abbreviation(this Substance substance) => Abbreviations[(int)substance];

    public static Substance Parse(string abbreviation)
    {
        for (var i = 0; i < Count; i++)
        {
            if (Abbreviations[i] == abbreviation)
            {
                return (Substance)i;
            }
        }

        throw new FormatException($"Unknown substance '{abbreviation}'. Expected one of: {string.Join(", ", Abbreviations)}.");
    }
}
