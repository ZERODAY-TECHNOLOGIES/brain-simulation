using System.Text;
using BrainSim.Engine.Config;
using BrainSim.Engine.Effectors;
using BrainSim.Engine.Llm;
using BrainSim.Engine.Sleep;

namespace BrainSim.Engine.Dreams;

/// <summary>
/// One conversation turn kept as recent memory until ACT-R exists (architecture.md "Dreams in reduced mode").
/// <paramref name="Tag"/> is the emotional tag: how stressed and alert the brain was when it was encoded.
/// </summary>
public sealed record Fragment(DateTime Time, string Text, double Tag);

/// <summary>One dream from the dream journal: when, from which seeds, in what chemistry, and what was dreamt.</summary>
public sealed record Dream(DateTime Start, DateTime End, IReadOnlyList<string> Seeds, double[] Chemistry, string Text, bool Interrupted);

/// <summary>
/// Dreams in REM (spec: "Visele (REM)"). Once REM chemistry has settled, 3 to 5 recent fragments are picked in
/// proportion to their emotional tag, shuffled into a prompt with no instructions, and the LLM continues it.
/// Tokens are revealed at one per 2 seconds of REM, so a 9 minute REM gives about 270 tokens. Each seed loses
/// 20% of its tag (emotional detensioning). A forced wake from REM leaves the dream in the wake-up context.
/// </summary>
public sealed class Dreamer
{
    private readonly DreamConfig _config;
    private readonly SamplingConfig _sampling;
    private readonly LmStudioClient _llm;
    private readonly Random _random = new(7);
    private readonly List<Fragment> _fragments = [];
    private IReadOnlyList<Dream> _journal = []; // replaced, never mutated, so the UI thread can read it any time
    private readonly Lock _gate = new();
    private readonly List<string> _generated = [];
    private DateTime? _remStart;
    private bool _dreamedThisRem;
    private Draft? _current;
    private int _revealed;
    private CancellationTokenSource? _cancel;
    private string? _wakeMemory;

    public Dreamer(EngineConfig config, LmStudioClient llm)
    {
        _config = config.Dreams;
        _sampling = config.Sampling;
        _llm = llm;
    }

    public IReadOnlyList<Fragment> Fragments => _fragments;
    public IReadOnlyList<Dream> Journal => _journal;

    /// <summary>The dream being generated. A class, so the UI thread always reads a whole one.</summary>
    private sealed record Draft(DateTime Start, string[] Seeds, double[] Chemistry);

    /// <summary>The generation running for the current dream, if any. Tests await it.</summary>
    public Task? Generation { get; private set; }

    /// <summary>The dream in progress, with only the tokens revealed so far. Null outside a dream.</summary>
    public Dream? Current
    {
        get
        {
            var c = _current;
            return c is null ? null : new Dream(c.Start, DateTime.MinValue, c.Seeds, c.Chemistry, RevealedText(), Interrupted: false);
        }
    }

    public event Action<Dream>? Started;
    public event Action<Dream>? Finished;
    public event Action<string>? Failed;

    /// <summary>Stores a turn as a fragment. Tag = floor + (NE - 1)+ + (CORT - 1)+ at encoding (spec: CORT and NE).</summary>
    public void Remember(string text, IReadOnlyList<double> c, DateTime now)
    {
        var tag = _config.TagFloor
            + Math.Max(0, c[(int)Substance.Norepinephrine] - 1)
            + Math.Max(0, c[(int)Substance.Cortisol] - 1);
        _fragments.Add(new Fragment(now, text, tag));
        if (_fragments.Count > _config.FragmentsKept)
        {
            _fragments.RemoveAt(0);
        }
    }

    /// <summary>The interrupted dream to bring into the wake-up context, once.</summary>
    public string? TakeWakeMemory()
    {
        var memory = _wakeMemory;
        _wakeMemory = null;
        return memory;
    }

    /// <summary>Called after every brain tick.</summary>
    public void OnTick(Brain brain)
    {
        if (brain.Phase == SleepPhase.Rem)
        {
            _remStart ??= brain.Now;
            if (!_dreamedThisRem && (brain.Now - _remStart.Value).TotalSeconds >= _config.SettleSeconds)
            {
                _dreamedThisRem = true;
                Begin(brain);
            }
        }
        else if (_remStart is not null)
        {
            // REM ended without a forced wake: into the next cycle, or a natural wake.
            End(brain.Now, interrupted: false);
        }

        if (_current is { } dream)
        {
            // Reveal at one token per seconds_per_token of REM, never ahead of what the model has produced.
            var due = (int)((brain.Now - dream.Start).TotalSeconds / _config.SecondsPerToken);
            lock (_gate)
            {
                _revealed = Math.Min(due, _generated.Count);
            }
        }
    }

    /// <summary>A message woke the brain in REM: the dream stops where it was and stays in mind.</summary>
    public void OnTransition(SleepTransition transition)
    {
        if (transition is { Kind: TransitionKind.ForcedWake, From: SleepPhase.Rem } && _current is not null)
        {
            _wakeMemory = End(transition.Time, interrupted: true)?.Text;
        }
    }

    public DreamState Save() => new([.. _fragments], [.. _journal]);

    public void Restore(DreamState? state)
    {
        _fragments.Clear();
        _fragments.AddRange(state?.Fragments ?? []);
        _journal = [.. state?.Journal ?? []];
    }

    private void Begin(Brain brain)
    {
        var seeds = PickSeeds();
        if (seeds.Count == 0)
        {
            return; // nothing to dream about yet
        }

        // The rest of this REM period: REM is the last phase of every cycle.
        var remEnd = brain.SleepOnset!.Value + brain.Architecture.CycleLength * (brain.Cycle + 1);
        var budget = (int)Math.Clamp((remEnd - brain.Now).TotalSeconds / _config.SecondsPerToken, 1, _config.MaxTokens);
        var sampling = Sampling.Compute(brain.Chemistry.C, _sampling) with { MaxTokens = budget };

        // Recombination: the seeds go in shuffled, without order or instructions.
        var shuffled = seeds.ToArray();
        _random.Shuffle(shuffled);
        var prompt = string.Join("\n\n", shuffled) + "\n\n";

        lock (_gate)
        {
            _generated.Clear();
            _revealed = 0;
        }

        _current = new Draft(brain.Now, shuffled, [.. brain.Chemistry.C]);
        _cancel = new CancellationTokenSource();
        var token = _cancel.Token;
        Generation = Task.Run(async () =>
        {
            try
            {
                await foreach (var piece in _llm.StreamCompletionAsync(prompt, sampling, token))
                {
                    lock (_gate)
                    {
                        _generated.Add(piece);
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // The dream ended before the model did.
            }
            catch (HttpRequestException e)
            {
                Failed?.Invoke($"The dream could not be generated: {e.Message}");
            }
        });

        Started?.Invoke(Current!);
    }

    private Dream? End(DateTime now, bool interrupted)
    {
        _remStart = null;
        _dreamedThisRem = false;
        if (_current is not { } c)
        {
            return null;
        }

        _cancel?.Cancel();
        _cancel?.Dispose();
        var dream = new Dream(c.Start, now, c.Seeds, c.Chemistry, RevealedText(), interrupted);
        _current = null;
        _journal = [.. _journal.Skip(Math.Max(0, _journal.Count + 1 - _config.JournalKept)), dream];

        Finished?.Invoke(dream);
        return dream;
    }

    /// <summary>Weighted draw without replacement; each pick loses <c>detension</c> of its tag.</summary>
    private List<string> PickSeeds()
    {
        var count = Math.Min(_fragments.Count, _random.Next(_config.SeedMin, _config.SeedMax + 1));
        var pool = Enumerable.Range(0, _fragments.Count).ToList();
        var seeds = new List<string>(count);
        for (var n = 0; n < count; n++)
        {
            var total = pool.Sum(i => _fragments[i].Tag);
            var roll = _random.NextDouble() * total;
            var pick = pool[^1];
            foreach (var i in pool)
            {
                roll -= _fragments[i].Tag;
                if (roll <= 0)
                {
                    pick = i;
                    break;
                }
            }

            pool.Remove(pick);
            seeds.Add(_fragments[pick].Text);
            _fragments[pick] = _fragments[pick] with { Tag = _fragments[pick].Tag * (1 - _config.Detension) };
        }

        return seeds;
    }

    private string RevealedText()
    {
        lock (_gate)
        {
            var text = new StringBuilder();
            for (var i = 0; i < _revealed; i++)
            {
                text.Append(_generated[i]);
            }

            return text.ToString();
        }
    }
}

/// <summary>The dream part of a checkpoint.</summary>
public sealed record DreamState(List<Fragment> Fragments, List<Dream> Journal);
