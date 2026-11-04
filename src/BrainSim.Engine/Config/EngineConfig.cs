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
    public AppraisalConfig Appraisal { get; init; } = new();
    public CircadianConfig Circadian { get; init; } = new();
    public SleepConfig Sleep { get; init; } = new();

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

        foreach (var (phase, targets) in Sleep.PhaseTargets)
        {
            if (phase is not ("N1" or "N2" or "N3" or "REM"))
            {
                throw new InvalidDataException($"config.yaml: unknown sleep phase '{phase}' in phase_targets.");
            }

            foreach (var substance in targets.Keys)
            {
                Substances.Parse(substance);
            }
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

public sealed record AppraisalConfig
{
    public double EffortGain { get; init; } = 0.001;
}

public sealed record CircadianConfig
{
    public double DarkStart { get; init; } = 21;
    public double DarkEnd { get; init; } = 7;
    public double RampHours { get; init; } = 1;
    public double LightMinutes { get; init; } = 30;
    public double JetLagShiftMinutes { get; init; } = 15;
    public double JetLagLightMinutes { get; init; } = 60;
    public double MelatoninProduction { get; init; } = 3.85e-5;
    public double CortisolNight { get; init; } = 0.6;
    public double CortisolRiseStart { get; init; } = 3;
    public double CortisolAwakening { get; init; } = 1.5;
    public double CortisolAwakeningMinutes { get; init; } = 30;
}

public sealed record SleepConfig
{
    public double OnsetPressure { get; init; } = 2.0;
    public double OnsetMelatonin { get; init; } = 1.5;
    public double OnsetIdleMinutes { get; init; } = 10;
    public double ForcedAdenosine { get; init; } = 5.0;
    public double WakePressure { get; init; } = 0.5;
    public double CycleMinutes { get; init; } = 90;
    public double N1Fraction { get; init; } = 0.05;
    public double N3FractionStart { get; init; } = 0.40;
    public double N3FractionStep { get; init; } = -0.09;
    public double RemFractionStart { get; init; } = 0.10;
    public double RemFractionStep { get; init; } = 0.08;
    public Dictionary<string, Dictionary<string, double>> PhaseTargets { get; init; } = [];
}
