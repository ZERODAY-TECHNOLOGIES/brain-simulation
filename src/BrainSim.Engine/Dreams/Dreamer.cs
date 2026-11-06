using System.Text;
using BrainSim.Engine.Config;
using BrainSim.Engine.Effectors;
using BrainSim.Engine.Llm;
using BrainSim.Engine.Memory;
using BrainSim.Engine.Sleep;

namespace BrainSim.Engine.Dreams;

/// <summary>
/// A conversation turn as checkpoints before Phase 7 kept it for dreaming. Read only to migrate them into memory.
/// <paramref name="Tag"/> is the emotional tag: how stressed and alert the brain was when it was encoded.
/// </summary>
public sealed record Fragment(DateTime Time, string Text, double Tag);

/// <summary>One dream from the dream journal: when, from which seeds, in what chemistry, and what was dreamt.</summary>
public sealed record Dream(DateTime Start, DateTime End, IReadOnlyList<string> Seeds, double[] Chemistry, string Text, bool Interrupted);

/// <summary>
/// Dreams in REM (spec: "Visele (REM)"). Once REM chemistry has settled, 3 to 5 memory episodes are picked in
/// proportion to their emotional tag, shuffled into a prompt with no instructions, and the LLM continues it.
/// Tokens are revealed at one per 2 seconds of REM, so a 9 minute REM gives about 270 tokens. Each seed loses
/// 20% of its tag (emotional detensioning). A forced wake from REM leaves the dream in the wake-up context.
/// </summary>
public sealed class Dreamer
{
    private readonly DreamConfig _config;
    private readonly SamplingConfig _sampling;
    private readonly LmStudioClient _llm;
    private readonly DeclarativeMemory _memory;
    private readonly Random _random = new(7);
    private IReadOnlyList<Dream> _journal = []; // replaced, never mutated, so the UI thread can read it any time
    private readonly Lock _gate = new();
    private readonly List<string> _generated = [];
    private DateTime? _remStart;
    private bool _dreamedThisRem;
    private Draft? _current;
    private int _revealed;
    private CancellationTokenSource? _cancel;
    private string? _wakeMemory;

    public Dreamer(EngineConfig config, LmStudioClient llm, DeclarativeMemory memory)
    {
        _config = config.Dreams;
        _sampling = config.Sampling;
        _llm = llm;
        _memory = memory;
    }

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

    public DreamState Save() => new(null, [.. _journal]);

    public void Restore(DreamState? state)
    {
        if (state?.Fragments is { Count: > 0 } old)
        {
            _memory.Import(old); // a checkpoint from before Phase 7: its dream fragments become episodes
        }

        _journal = [.. state?.Journal ?? []];
    }

    private void Begin(Brain brain)
    {
        var seeds = _memory.DrawByTag(_random.Next(_config.SeedMin, _config.SeedMax + 1), _random, brain.Endocannabinoids.Detension(_config.Detension));
        if (seeds.Count == 0)
        {
            return; // nothing to dream about yet
        }

        // The rest of this REM period: REM is the last phase of every cycle.
        var remEnd = brain.SleepOnset!.Value + brain.Architecture.CycleLength * (brain.Cycle + 1);
        var budget = (int)Math.Clamp((remEnd - brain.Now).TotalSeconds / _config.SecondsPerToken, 1, _config.MaxTokens);
        var sampling = Sampling.Compute(brain.Chemistry.C, _sampling) with { MaxTokens = budget };

        // Recombination, as structure rather than word lists, so it holds in any language (D-31):
        // the seeds lose their dialogue shape (clauses only, questions dropped) and are shuffled together.
        var pool = seeds.SelectMany(Clauses).ToList();
        var shuffled = seeds.ToArray();
        _random.Shuffle(shuffled);

        lock (_gate)
        {
            _generated.Clear();
            _revealed = 0;
        }

        _current = new Draft(brain.Now, shuffled, [.. brain.Chemistry.C]);
        _cancel = new CancellationTokenSource();
        var token = _cancel.Token;
        var random = new Random(_random.Next());
        Generation = Task.Run(async () =>
        {
            try
            {
                await DreamAsync(pool, sampling, budget, random, token);
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

    /// <summary>
    /// The dream runs in segments. Each one continues a freshly shuffled pool of clauses plus the tail of the dream
    /// so far, and stops at the first new line, question or thinking tag: the places where an instruction-tuned
    /// model leaves the stream of images and starts answering someone.
    /// </summary>
    private async Task DreamAsync(List<string> pool, SamplingParameters sampling, int budget, Random random, CancellationToken token)
    {
        var dream = new StringBuilder();
        var produced = 0;
        var empty = 0;
        while (produced < budget && empty < 3 && !token.IsCancellationRequested)
        {
            random.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(pool));
            var tail = dream.Length > 200 ? dream.ToString(dream.Length - 200, 200) : dream.ToString();
            var prompt = string.Join(", ", pool) + ", " + tail + _config.Joiner;
            var segment = new List<string>();
            var limit = sampling with { MaxTokens = Math.Min(_config.SegmentTokens, budget - produced) };
            await foreach (var piece in _llm.StreamCompletionAsync(prompt, limit, Stops, token))
            {
                segment.Add(piece);
            }

            var text = string.Concat(segment).Trim();
            if (text.Trim('.', ',', ' ').Length == 0)
            {
                empty++;
                continue;
            }

            empty = 0;
            segment.Add(_config.Joiner + " ");
            dream.Append(text).Append(_config.Joiner).Append(' ');
            lock (_gate)
            {
                _generated.AddRange(segment);
            }

            produced += segment.Count;
        }
    }

    private static readonly string[] Stops = ["\n", "?", "<think>", "</think>"];

    /// <summary>
    /// Splits a fragment into clauses on punctuation and line breaks, drops markdown marks and questions.
    /// No word lists, so it works the same for any language the conversation is in.
    /// </summary>
    public static IEnumerable<string> Clauses(string text)
    {
        var plain = new string([.. text.Select(c => c is '*' or '_' or '`' or '#' or '>' or '|' ? ' ' : c)]);
        foreach (var line in plain.Split('\n'))
        {
            foreach (var part in System.Text.RegularExpressions.Regex.Split(line.Trim(), @"(?<=[.!?;:])\s+|,\s+|^\s*-\s+|\s+-\s+"))
            {
                var clause = part.Trim(' ', '-', '.', '!', ';', ':');
                if (clause.Length > 0 && !clause.EndsWith('?'))
                {
                    yield return char.ToLowerInvariant(clause[0]) + clause[1..];
                }
            }
        }
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
/// <remarks><paramref name="Fragments"/> is only read, to migrate checkpoints from before Phase 7; it is saved as null.</remarks>
public sealed record DreamState(List<Fragment>? Fragments, List<Dream> Journal);
