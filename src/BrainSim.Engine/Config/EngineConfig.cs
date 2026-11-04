using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace BrainSim.Engine.Config;

/// <summary>Everything in config.yaml. Keys mirror the spec tables.</summary>
public sealed record EngineConfig
{
    public TimeConfig Time { get; init; } = new();
    public ConcentrationConfig Concentration { get; init; } = new();
    public Dictionary<string, double> HalfLife { get; init; } = [];
    public List<InteractionConfig> Interactions { get; init; } = [];
    public ReceptorConfig Receptors { get; init; } = new();
    public ModulationConfig Modulation { get; init; } = new();

    public static EngineConfig LoadDefault() => Load(Path.Combine(AppContext.BaseDirectory, "config.yaml"));

    public static EngineConfig Load(string path)
    {
        var config = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build()
            .Deserialize<EngineConfig>(File.ReadAllText(path));
        config.Validate();
        return config;
    }

    /// <summary>Half-life per substance, in enum order.</summary>
    public double[] HalfLives() => Substances.All.Select(s => HalfLife[s.Abbreviation()]).ToArray();

    private void Validate()
    {
        foreach (var s in Substances.All)
        {
            if (!HalfLife.TryGetValue(s.Abbreviation(), out var t) || t <= 0)
            {
                throw new InvalidDataException($"config.yaml: half_life for {s.Abbreviation()} is missing or not positive.");
            }
        }

        // Parse every substance name once so a typo fails at load, not mid-run.
        foreach (var i in Interactions)
        {
            Substances.Parse(i.From);
            Substances.Parse(i.To);
        }

        foreach (var term in Modulation.Channels.Values.SelectMany(t => t))
        {
            Substances.Parse(term.Substance);
        }
    }
}

public sealed record TimeConfig
{
    public double TicksPerSecond { get; init; } = 10;
    public int ImpulseSpreadTicks { get; init; } = 10;
}

public sealed record ConcentrationConfig
{
    public double Baseline { get; init; } = 1.0;
    public double Min { get; init; } = 0.0;
    public double Max { get; init; } = 5.0;
}

public sealed record InteractionConfig
{
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    public double W { get; init; }
}

public sealed record ReceptorConfig
{
    public double TauAvg { get; init; } = 5000;
    public double TauRho { get; init; } = 20000;
    public double Gamma { get; init; } = 0.5;
    public double RhoMin { get; init; } = 0.3;
    public double RhoMax { get; init; } = 1.5;
}

public sealed record ModulationConfig
{
    public double GabaGain { get; init; } = 1.0;
    public Dictionary<string, List<ChannelTermConfig>> Channels { get; init; } = [];
}

public sealed record ChannelTermConfig
{
    public string Substance { get; init; } = "";
    public double Emax { get; init; }
    public double Ec50 { get; init; } = 1.0;
    public double N { get; init; } = 1.5;
    public string Shape { get; init; } = "hill";
    public double Peak { get; init; } = 1.0;
}
