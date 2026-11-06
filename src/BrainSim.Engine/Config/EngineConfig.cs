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
    public LlmConfig Llm { get; init; } = new();
    public SamplingConfig Sampling { get; init; } = new();
    public DreamConfig Dreams { get; init; } = new();
    public MemoryConfig Memory { get; init; } = new();
    public EndocannabinoidConfig Endocannabinoid { get; init; } = new();
    public MoodConfig Mood { get; init; } = new();

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

    private static readonly string[] Signals = ["surprise", "reward_error", "threat", "warmth", "expected_reward"];
    private static readonly string[] SleepPhases = ["N1", "N2", "N3", "REM"];

    /// <summary>Every name in the file is parsed once, here, so a typo fails at load and not mid-run.</summary>
    private void Validate()
    {
        foreach (var s in Substances.All)
        {
            if (!HalfLife.TryGetValue(s.Abbreviation(), out var t) || t <= 0)
            {
                throw new InvalidDataException($"config.yaml: half_life for {s.Abbreviation()} is missing or not positive.");
            }
        }

        ParseSubstances(Interactions.SelectMany(i => new[] { i.From, i.To }));
        ParseSubstances(Modulation.Channels.Values.SelectMany(t => t).Select(t => t.Substance));
        ParseSubstances(Endocannabinoid.Cb1.Select(t => t.Substance));
        ParseSubstances(Endocannabinoid.ReleaseGain.Keys);
        ValidateTable("appraisal signal", "gains", Appraisal.Gains, Signals);
        ValidateTable("sleep phase", "phase_targets", Sleep.PhaseTargets, SleepPhases);
        ValidateMood();
    }

    private void ValidateMood()
    {
        if (Mood.Channels.Keys.FirstOrDefault(c => !Modulation.Channels.ContainsKey(c)) is { } unknown)
        {
            throw new InvalidDataException($"config.yaml: mood channel '{unknown}' is not a modulation channel.");
        }
    }

    /// <summary>A table keyed by one of <paramref name="allowed"/>, whose rows map substances to values.</summary>
    private static void ValidateTable(string what, string key, Dictionary<string, Dictionary<string, double>> table, string[] allowed)
    {
        foreach (var (name, row) in table)
        {
            if (!allowed.Contains(name))
            {
                throw new InvalidDataException($"config.yaml: unknown {what} '{name}' in {key}.");
            }

            ParseSubstances(row.Keys);
        }
    }

    private static void ParseSubstances(IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            Substances.Parse(name);
        }
    }
}

/// <summary>The mood note that stands in for E1 steering in reduced mode (D-38).</summary>
public sealed record MoodConfig
{
    public double Threshold { get; init; } = 0.1;
    public double Strong { get; init; } = 0.3;
    public int MaxChannels { get; init; } = 3;
    public Dictionary<string, MoodChannel> Channels { get; init; } = [];
}

/// <summary>Positive is the first word of the channel name (calm in calm_anxious), negative the second.</summary>
public sealed record MoodChannel
{
    public MoodPole Positive { get; init; } = new();
    public MoodPole Negative { get; init; } = new();
}

/// <summary>How a pole feels, and how that shows in a reply.</summary>
public sealed record MoodPole
{
    public string Feeling { get; init; } = "";
    public string Shows { get; init; } = "";
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
    public double RewardLearningRate { get; init; } = 0.1;
    public int SurpriseWindow { get; init; } = 50;
    public int SurpriseMinSamples { get; init; } = 5;
    public double SurpriseMax { get; init; } = 3.0;
    public double ProbeFloor { get; init; } = 0.5;
    public Dictionary<string, Dictionary<string, double>> Gains { get; init; } = [];
}

public sealed record MemoryConfig
{
    public double Decay { get; init; } = 0.5;
    public double Spreading { get; init; } = 2.0;
    public double CosineFloor { get; init; } = 0.7;
    public double Noise { get; init; } = 0.25;
    public double Threshold { get; init; } = -3.0;
    public double Gain { get; init; } = 4;
    public int RecallCount { get; init; } = 4;
    public double DuplicateCosine { get; init; } = 0.97;
    public double FamiliarityDamping { get; init; } = 0.5;
    public double ReplayMinutes { get; init; } = 5;
    public double ReplayWeight { get; init; } = 0.5;
    public double N2ReplayRate { get; init; } = 0.3;
    public double SalienceDecay { get; init; } = 0.7;
    public int ArchiveLimit { get; init; } = 5000;
    public double ExtractionTemperature { get; init; } = 0.2;
    public int ExtractionTokens { get; init; } = 150;
    public int ExtractionReplyChars { get; init; } = 200;
    public int FactsPerEpisode { get; init; } = 5;
    public int FactMinChars { get; init; } = 3;
    public int FactMaxChars { get; init; } = 200;
}

/// <summary>The endocannabinoid system (docs/endocannabinoids.md, D-36). Not in the spec.</summary>
public sealed record EndocannabinoidConfig
{
    /// <summary>The terms of CB1 tone, summed like a channel but without GABA damping.</summary>
    public List<ChannelTermConfig> Cb1 { get; init; } = [];

    /// <summary>r_i in gain_i = 1 + r_i * cb1, per substance. Substances not listed are not braked.</summary>
    public Dictionary<string, double> ReleaseGain { get; init; } = [];
    public double GainMin { get; init; } = 0.25;
    public double GainMax { get; init; } = 2.0;

    /// <summary>P gains sleep_pressure * (AEA - its target): AEA promotes sleep, and stress (AEA below target) delays it.</summary>
    public double SleepPressure { get; init; } = 0.5;

    /// <summary>Dream detension is multiplied by 1 + extinction_gain * max(0, cb1), up to detension_max.</summary>
    public double ExtinctionGain { get; init; } = 1.0;
    public double DetensionMax { get; init; } = 0.5;

    /// <summary>A night this long or longer leaves no sleep debt for the 2-AG rhythm.</summary>
    public double FullNightHours { get; init; } = 8;
    public RhythmConfig TwoAgRhythm { get; init; } = new();
    public RhythmConfig AeaRhythm { get; init; } = new();
}

/// <summary>
/// A daily rhythm of a target: amplitude * cos(2 pi (phi - peak) / 24). Sleep debt d in [0, 1] (the share of a full
/// night missed) multiplies the amplitude by 1 + debt_amplitude * d and moves the peak debt_delay_hours * d later.
/// </summary>
public sealed record RhythmConfig
{
    public double Amplitude { get; init; }
    public double PeakHour { get; init; } = 12;
    public double DebtAmplitude { get; init; }
    public double DebtDelayHours { get; init; }
}

public sealed record DreamConfig
{
    public int SeedMin { get; init; } = 3;
    public int SeedMax { get; init; } = 5;
    public double Detension { get; init; } = 0.2;
    public double SecondsPerToken { get; init; } = 2;
    public double SettleSeconds { get; init; } = 30;
    public int MaxTokens { get; init; } = 1200;
    public int JournalKept { get; init; } = 50;
    public string Joiner { get; init; } = ", and then";
    public int SegmentTokens { get; init; } = 120;
}

public sealed record SamplingConfig
{
    public double T0 { get; init; } = 0.7;
    public double NeOptimum { get; init; } = 1.3;
    public double BetaT { get; init; } = 0.3;
    public double TMin { get; init; } = 0.2;
    public double TMax { get; init; } = 1.5;
    public int MaxTokens { get; init; } = 512;
    public double MaxLengthCut { get; init; } = 0.3;
    public double TopPMax { get; init; } = 0.95;
    public double TopPMin { get; init; } = 0.8;
    public double ElevatedRange { get; init; } = 1.0;
    public double AdenosineRange { get; init; } = 4.0;
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
    public double WakeAdenosineRise { get; init; } = 3.8e-6;
    public double WakePressure { get; init; } = 0.5;
    public double CycleMinutes { get; init; } = 90;
    public double N1Fraction { get; init; } = 0.05;
    public double N3FractionStart { get; init; } = 0.40;
    public double N3FractionStep { get; init; } = -0.09;
    public double RemFractionStart { get; init; } = 0.10;
    public double RemFractionStep { get; init; } = 0.08;
    public Dictionary<string, Dictionary<string, double>> PhaseTargets { get; init; } = [];
}

public sealed record LlmConfig
{
    public string BaseUrl { get; init; } = "http://localhost:1234/v1/";

    /// <summary>Where embeddings are requested. Empty means the chat server at <see cref="BaseUrl"/>.</summary>
    public string EmbeddingBaseUrl { get; init; } = "";

    /// <summary>Sent as a bearer token when set. Comes from the Settings dialog, never from config.yaml in the repo.</summary>
    public string ApiKey { get; init; } = "";
    public string ChatModel { get; init; } = "";
    public string EmbeddingModel { get; init; } = "";
    public string EmbeddingPrefix { get; init; } = "";
    public string SystemPrompt { get; init; } = "";
    public int HistoryMessages { get; init; } = 20;
}
