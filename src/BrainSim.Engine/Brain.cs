using BrainSim.Engine.Chemistry;
using BrainSim.Engine.Circadian;
using BrainSim.Engine.Config;
using BrainSim.Engine.Effectors;
using BrainSim.Engine.Receptors;
using BrainSim.Engine.Sleep;

namespace BrainSim.Engine;

public enum TransitionKind
{
    FellAsleep,
    ForcedSleep,
    WokeNaturally,
    ForcedWake,
}

public readonly record struct SleepTransition(DateTime Time, TransitionKind Kind, SleepPhase From);

/// <summary>Everything needed to resume a brain exactly where it stopped (spec loop step 12).</summary>
public sealed record BrainCheckpoint(
    DateTime Now,
    SleepPhase Phase,
    int Cycle,
    DateTime? SleepOnset,
    DateTime LastUserMessage,
    DateTime AwakeningUntil,
    ChemistrySnapshot Chemistry,
    double[] MeanC,
    double[] Rho,
    ClockSnapshot Clock);

/// <summary>
/// Everything that runs on ticks, without the LLM: chemistry, receptors, modulation, the clock and sleep.
/// The caller supplies the time of each tick, so real and simulated clocks work the same way.
/// </summary>
public sealed class Brain
{
    private const int Ado = (int)Substance.Adenosine;
    private const int Cort = (int)Substance.Cortisol;
    private const int Mel = (int)Substance.Melatonin;

    private readonly EngineConfig _config;
    private readonly (int Index, double Value)[][] _phaseTargets;
    private readonly TimeSpan _tickLength;
    private readonly double _awakeningSource;
    private DateTime _lastUserMessage;
    private DateTime _awakeningUntil = DateTime.MinValue;

    public Brain(EngineConfig config, DateTime start)
    {
        _config = config;
        Chemistry = new ChemicalState(config);
        Receptors = new ReceptorState(config);
        Modulation = new Modulation(config);
        Clock = new CircadianClock(config.Circadian);
        Architecture = new SleepArchitecture(config.Sleep);
        _tickLength = TimeSpan.FromTicks((long)(TimeSpan.TicksPerSecond / config.Time.TicksPerSecond));

        // The awakening response is real secretion, not a moved target: a constant CORT source for
        // cortisol_awakening_minutes, sized so CORT reaches cortisol_awakening at the end of it.
        var k = Math.Log(2) / config.HalfLife[Substance.Cortisol.Abbreviation()];
        var ticks = config.Circadian.CortisolAwakeningMinutes * 60 * config.Time.TicksPerSecond;
        _awakeningSource = (config.Circadian.CortisolAwakening - config.Concentration.Baseline) * k / (1 - Math.Exp(-k * ticks));
        Now = start;
        _lastUserMessage = start;

        _phaseTargets = new (int, double)[Enum.GetValues<SleepPhase>().Length][];
        foreach (var phase in Enum.GetValues<SleepPhase>())
        {
            var key = phase == SleepPhase.Rem ? "REM" : phase.ToString();
            _phaseTargets[(int)phase] = config.Sleep.PhaseTargets.TryGetValue(key, out var targets)
                ? [.. targets.Select(t => ((int)Substances.Parse(t.Key), t.Value))]
                : [];
        }
    }

    public ChemicalState Chemistry { get; }
    public ReceptorState Receptors { get; }
    public Modulation Modulation { get; }
    public CircadianClock Clock { get; }
    public SleepArchitecture Architecture { get; }

    public DateTime Now { get; private set; }
    public SleepPhase Phase { get; private set; } = SleepPhase.Wake;
    public int Cycle { get; private set; }
    public DateTime? SleepOnset { get; private set; }
    public bool Asleep => Phase != SleepPhase.Wake;

    /// <summary>Set while a response is streaming. Sleep is never entered mid-response.</summary>
    public bool IsGenerating { get; set; }

    public event Action<SleepTransition>? Transition;

    /// <summary>Raised after every tick, with the generated token when the tick is one.</summary>
    public event Action<Brain, string?>? Ticked;

    /// <summary>Net sleep pressure P = (ADO - b) - C(phi). Measured from baseline, see D-21.</summary>
    public double SleepPressure =>
        Chemistry.C[Ado] - Chemistry.RestingBaseline - CircadianClock.Alertness(Clock.Phase(Now));

    /// <summary>Advances one tick that happens at <paramref name="now"/>. <paramref name="token"/> is set for generated tokens.</summary>
    public void Tick(DateTime now, string? token = null)
    {
        var elapsed = (now - Now).TotalSeconds;
        Now = now;
        Clock.Advance(now, elapsed);
        if (SleepOnset is { } onset)
        {
            (Phase, Cycle) = Architecture.PhaseAt(now - onset);
        }

        ApplyTargets();
        Chemistry.Step();
        Receptors.Step(Chemistry.C);
        Modulation.Compute(Chemistry.C, Receptors);
        CheckSleepDecision();
        Ticked?.Invoke(this, token);
    }

    public BrainCheckpoint Save() => new(
        Now, Phase, Cycle, SleepOnset, _lastUserMessage, _awakeningUntil,
        Chemistry.Save(), [.. Receptors.MeanC], [.. Receptors.Rho], Clock.Save());

    public void Restore(BrainCheckpoint checkpoint)
    {
        Now = checkpoint.Now;
        Phase = checkpoint.Phase;
        Cycle = checkpoint.Cycle;
        SleepOnset = checkpoint.SleepOnset;
        _lastUserMessage = checkpoint.LastUserMessage;
        _awakeningUntil = checkpoint.AwakeningUntil;
        Chemistry.Restore(checkpoint.Chemistry);
        Chemistry.Asleep = Asleep;
        Array.Copy(checkpoint.MeanC, Receptors.MeanC, Substances.Count);
        Array.Copy(checkpoint.Rho, Receptors.Rho, Substances.Count);
        Clock.Restore(checkpoint.Clock);
        Modulation.Compute(Chemistry.C, Receptors);
    }

    /// <summary>Idle time: ticks at ticks_per_second of clock time until <paramref name="until"/>.</summary>
    public void AdvanceTo(DateTime until)
    {
        while (Now + _tickLength <= until)
        {
            Tick(Now + _tickLength);
        }
    }

    public void OnUserMessage(DateTime now)
    {
        _lastUserMessage = now;
        Clock.OnLight(now);
        if (Asleep)
        {
            Wake(TransitionKind.ForcedWake);
        }
    }

    /// <summary>Spec loop step 10: ADO += effort_gain * tokens generated.</summary>
    public void OnResponseFinished(int tokens) =>
        Chemistry.Release(Substance.Adenosine, _config.Appraisal.EffortGain * tokens);

    private void ApplyTargets()
    {
        var b = Chemistry.Baseline;
        Array.Fill(b, Chemistry.RestingBaseline);
        foreach (var (index, value) in _phaseTargets[(int)Phase])
        {
            b[index] = value;
        }

        // CORT and MEL follow the clock, not the sleep phase.
        b[Cort] = Clock.CortisolTarget(Clock.Phase(Now));
        Chemistry.Source[Cort] = Now < _awakeningUntil ? _awakeningSource : 0;
        Chemistry.Source[Mel] = Clock.MelatoninProduction(Now);

        // Process S: besides effort, ADO creeps up with every awake tick (D-23). Sleep drains it.
        Chemistry.Source[Ado] = Asleep ? 0 : _config.Sleep.WakeAdenosineRise;
        Chemistry.Asleep = Asleep;
    }

    private void CheckSleepDecision()
    {
        if (IsGenerating)
        {
            return;
        }

        var sleep = _config.Sleep;
        var pressure = SleepPressure;
        if (!Asleep)
        {
            if (Chemistry.C[Ado] >= sleep.ForcedAdenosine)
            {
                FallAsleep(TransitionKind.ForcedSleep);
            }
            else if (pressure > sleep.OnsetPressure
                && Chemistry.C[Mel] > sleep.OnsetMelatonin
                && (Now - _lastUserMessage).TotalMinutes > sleep.OnsetIdleMinutes)
            {
                FallAsleep(TransitionKind.FellAsleep);
            }
        }
        else if (pressure < sleep.WakePressure && !Clock.InDarkWindow(Clock.Phase(Now)))
        {
            Wake(TransitionKind.WokeNaturally);
        }
    }

    private void FallAsleep(TransitionKind kind)
    {
        SleepOnset = Now;
        (Phase, Cycle) = Architecture.PhaseAt(TimeSpan.Zero);
        Chemistry.Asleep = true;
        Transition?.Invoke(new SleepTransition(Now, kind, SleepPhase.Wake));
    }

    private void Wake(TransitionKind kind)
    {
        var from = Phase;
        Phase = SleepPhase.Wake;
        Cycle = 0;
        SleepOnset = null;
        Chemistry.Asleep = false;
        if (kind == TransitionKind.WokeNaturally)
        {
            _awakeningUntil = Now.AddMinutes(_config.Circadian.CortisolAwakeningMinutes);
        }

        Transition?.Invoke(new SleepTransition(Now, kind, from));
    }
}
