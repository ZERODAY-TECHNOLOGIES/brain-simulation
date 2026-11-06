using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using BrainSim.Engine.Appraisal;
using BrainSim.Engine.Config;
using BrainSim.Engine.Dreams;
using BrainSim.Engine.Effectors;
using BrainSim.Engine.Llm;
using BrainSim.Engine.Memory;

namespace BrainSim.Engine;

/// <summary>One reply, with the appraisal of the message it answers and the facts memory brought back for it.</summary>
public sealed record AgentReply(
    string Text, AppraisalSignals Signals, SamplingParameters Sampling, int Tokens, IReadOnlyList<string> Recalled, double Familiarity,
    string? Mood = null);

/// <summary>Everything needed to pick a session back up: brain, appraisal, conversation and dreams.</summary>
public sealed record AgentCheckpoint(BrainCheckpoint Brain, AppraiserState Appraisal, List<ChatMessage> History, DreamState? Dreams = null)
{
    public async Task SaveAsync(string path, CancellationToken cancellationToken = default)
    {
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(this), cancellationToken);
        File.Move(temp, path, overwrite: true); // never leave a half-written checkpoint behind
    }

    public static async Task<AgentCheckpoint?> LoadAsync(string path, CancellationToken cancellationToken = default) =>
        File.Exists(path) ? JsonSerializer.Deserialize<AgentCheckpoint>(await File.ReadAllTextAsync(path, cancellationToken)) : null;
}

/// <summary>
/// The execution loop (spec: "Bucla de executie") in reduced mode: LM Studio is the cortex,
/// the brain modulates it only through sampling (D-03, D-04).
/// Not thread safe. The engine thread owns it.
/// </summary>
public sealed class Agent
{
    private readonly EngineConfig _config;
    private readonly LmStudioClient _llm;
    private readonly Func<DateTime> _clock;
    private const int MaxHistory = 400;
    private readonly List<ChatMessage> _history = [];

    // Facts extracted in the background, applied on the engine loop at the next tick (docs/memory.md "Encoding").
    private readonly ConcurrentQueue<(int EpisodeId, IReadOnlyList<string> Facts, float[][] Embeddings, MemoryModulation Encoding)> _learned = new();

    public Agent(EngineConfig config, LmStudioClient llm, IReadOnlyDictionary<string, LinearProbe> probes, Func<DateTime> clock)
    {
        _config = config;
        _llm = llm;
        _clock = clock;
        Brain = new Brain(config, clock());
        Appraiser = new Appraiser(config.Appraisal, probes);
        Memory = new DeclarativeMemory(config.Memory);
        Dreamer = new Dreamer(config, llm, Memory);
        Brain.Ticked += (brain, _) =>
        {
            ApplyLearnedFacts();
            Memory.OnTick(brain);
            Dreamer.OnTick(brain);
        };
        Brain.Transition += Dreamer.OnTransition;
    }

    public Brain Brain { get; }
    public Appraiser Appraiser { get; }
    public Dreamer Dreamer { get; }
    public DeclarativeMemory Memory { get; }

    /// <summary>The fact extraction started after the last reply. Tests await it.</summary>
    public Task? Extraction { get; private set; }

    /// <summary>Raised on the engine loop when extracted facts have been stored or have strengthened existing ones.</summary>
    public event Action<IReadOnlyList<Chunk>>? FactsLearned;

    /// <summary>Raised from the background when fact extraction fails. The episode stays in memory regardless.</summary>
    public event Action<string>? MemoryFailed;
    public IReadOnlyList<ChatMessage> History => _history;

    public async Task<AgentReply> SendAsync(
        string message,
        Action<string>? onToken = null,
        CancellationToken cancellationToken = default,
        Action<AppraisalSignals, SamplingParameters, IReadOnlyList<string>, string?>? onAppraised = null)
    {
        // 1. Catch up idle time, sleeping through it if the conditions were met. The message is light and wakes the brain.
        var now = _clock();
        Brain.AdvanceTo(now);
        Brain.OnUserMessage(Brain.Now);

        // A message that woke the brain in REM: the dream it broke off is still in mind (spec: "Visele (REM)").
        if (Dreamer.TakeWakeMemory() is { Length: > 0 } dream)
        {
            _history.Add(ChatMessage.System($"You were dreaming when this message woke you, and you still remember the dream: {dream}"));
        }

        // 2 to 4. Appraise the message, recall what it cues, and schedule the impulses.
        var (signals, remembered, familiarity, embedding) = await AppraiseAsync(message, cancellationToken);

        // 5. Reduced mode: sampling and the mood note are fixed for the whole response, from the state once this
        // message's releases have arrived, so the reply to an insult is already shaped by it (D-04, D-38).
        var state = Brain.Chemistry.Projected();
        var sampling = Sampling.Compute(state, _config.Sampling);
        var mood = MoodNote.Describe(Brain.Modulation.Names, Brain.Modulation.Evaluate(state, Brain.Receptors), _config.Mood);
        onAppraised?.Invoke(signals, sampling, remembered, mood);
        _history.Add(ChatMessage.User(message));

        // 6 to 8. One tick per streamed token.
        var (reply, tokens) = await StreamReplyAsync(BuildRequest(remembered, mood), sampling, onToken, cancellationToken);

        // 10. Effort and expected reward. Step 11, the sleep decision, runs on every following idle tick.
        _history.Add(ChatMessage.Assistant(reply));

        // ponytail: the conversation kept in the checkpoint is capped; ACT-R memory (Phase 7) is the long-term store.
        if (_history.Count > MaxHistory)
        {
            _history.RemoveRange(0, _history.Count - MaxHistory);
        }

        Brain.OnResponseFinished(tokens);
        Appraiser.LearnExpectedReward();
        Encode(message, reply, embedding);
        return new AgentReply(reply, signals, sampling, tokens, remembered, familiarity, mood);
    }

    /// <summary>
    /// Steps 2 to 4. Familiar messages are less surprising, and memory recalls what the message cues. Without an
    /// embedding (the embedding model is gone) the reply still goes ahead, with a neutral appraisal and no memory.
    /// </summary>
    private async Task<(AppraisalSignals Signals, IReadOnlyList<string> Remembered, double Familiarity, float[]? Embedding)> AppraiseAsync(
        string message, CancellationToken cancellationToken)
    {
        float[] embedding;
        try
        {
            embedding = (await _llm.EmbedAsync([message], cancellationToken))[0];
        }
        catch (HttpRequestException)
        {
            var neutral = new AppraisalSignals(0, null, 0, 0, 0, Appraiser.ExpectedReward);
            Appraiser.Release(neutral, Brain.Chemistry, Brain.Endocannabinoids);
            return (neutral, [], 0, null);
        }

        var modulation = MemoryModulation.From(Brain.Modulation);
        var familiarity = Memory.Familiarity(embedding, modulation, Brain.Now);
        var signals = Appraiser.Appraise(embedding, hasPreviousResponse: _history.Any(m => m.Role == "assistant"));
        signals = signals with { Surprise = signals.Surprise * (1 - _config.Memory.FamiliarityDamping * familiarity) };
        IReadOnlyList<string> remembered = [.. Memory.RecallFacts(embedding, modulation, Brain.Now).Select(r => r.Chunk.Text)];
        Appraiser.Release(signals, Brain.Chemistry, Brain.Endocannabinoids);
        return (signals, remembered, familiarity, embedding);
    }

    /// <summary>
    /// One system message, first: chat templates like Qwen's reject a system message anywhere else (D-34).
    /// The mood note and recalled facts ride along with this request only; they never enter the history, so they do not pile up.
    /// System notes in the history (a dream broken off by this message) are folded in after them.
    /// </summary>
    private List<ChatMessage> BuildRequest(IReadOnlyList<string> remembered, string? mood)
    {
        var window = _history.TakeLast(_config.Llm.HistoryMessages).ToList();
        var system = new StringBuilder(_config.Llm.SystemPrompt);
        if (mood is not null)
        {
            system.Append("\n\n").Append(mood);
        }

        if (remembered.Count > 0)
        {
            system.Append("\n\nThings you remember:\n").AppendJoin("\n", remembered.Select(f => "- " + f));
        }

        foreach (var note in window.Where(m => m.Role == "system"))
        {
            system.Append("\n\n").Append(note.Content);
        }

        return [ChatMessage.System(system.ToString()), .. window.Where(m => m.Role != "system")];
    }

    /// <summary>Steps 6 to 8: one brain tick per streamed token.</summary>
    private async Task<(string Text, int Tokens)> StreamReplyAsync(
        List<ChatMessage> request, SamplingParameters sampling, Action<string>? onToken, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        var tokens = 0;
        Brain.IsGenerating = true;
        try
        {
            await foreach (var token in _llm.StreamChatAsync(request, sampling, cancellationToken))
            {
                var at = _clock();
                Brain.Tick(at > Brain.Now ? at : Brain.Now.AddTicks(1), token);
                text.Append(token);
                tokens++;
                onToken?.Invoke(token);
            }
        }
        finally
        {
            Brain.IsGenerating = false;
        }

        return (text.ToString(), tokens);
    }

    /// <summary>
    /// The turn becomes an episode, tagged with the stress and alertness it was lived in (dreams draw on the tag),
    /// and its facts are extracted in the background.
    /// </summary>
    private void Encode(string message, string reply, float[]? embedding)
    {
        var encoding = MemoryModulation.From(Brain.Modulation);
        var episode = Memory.AddEpisode(message + "\n" + reply, embedding, Brain.Chemistry.C, encoding, Brain.Now);
        var exchange = FactExtractor.Exchange(message, reply, _config.Memory);
        Extraction = Task.Run(() => ExtractAsync(episode, exchange, encoding));
    }

    private async Task ExtractAsync(Chunk episode, string exchange, MemoryModulation encoding)
    {
        try
        {
            var facts = await FactExtractor.ExtractAsync(_llm, exchange, _config.Memory, CancellationToken.None);
            if (facts.Count > 0)
            {
                var embeddings = await _llm.EmbedAsync(facts);
                _learned.Enqueue((episode.Id, facts, embeddings, encoding));
            }
        }
        catch (HttpRequestException e)
        {
            MemoryFailed?.Invoke($"Facts could not be extracted from a turn: {e.Message}");
        }
    }

    /// <summary>
    /// On close: waits up to <paramref name="timeout"/> for the last extraction and stores its facts, which would
    /// otherwise wait for a tick that never comes. Call once the engine loop has stopped.
    /// </summary>
    public async Task FinishLearningAsync(TimeSpan timeout)
    {
        if (Extraction is { } extraction)
        {
            await Task.WhenAny(extraction, Task.Delay(timeout));
        }

        ApplyLearnedFacts();
    }

    /// <summary>On the engine loop: store the facts extraction produced, with the chemistry of their episode.</summary>
    private void ApplyLearnedFacts()
    {
        while (_learned.TryDequeue(out var batch))
        {
            var learned = new List<Chunk>();
            for (var i = 0; i < batch.Facts.Count; i++)
            {
                learned.Add(Memory.AddOrStrengthenFact(batch.Facts[i], batch.Embeddings[i], batch.EpisodeId, batch.Encoding, Brain.Now));
            }

            FactsLearned?.Invoke(learned);
        }
    }

    /// <summary>12. Snapshot for the next session.</summary>
    public AgentCheckpoint Checkpoint() => new(Brain.Save(), Appraiser.Save(), [.. _history], Dreamer.Save());

    public void Restore(AgentCheckpoint checkpoint)
    {
        Brain.Restore(checkpoint.Brain);
        Appraiser.Restore(checkpoint.Appraisal);
        Dreamer.Restore(checkpoint.Dreams);
        _history.Clear();
        _history.AddRange(checkpoint.History);
    }
}
