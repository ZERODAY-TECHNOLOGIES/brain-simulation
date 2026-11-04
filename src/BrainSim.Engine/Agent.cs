using System.Text;
using System.Text.Json;
using BrainSim.Engine.Appraisal;
using BrainSim.Engine.Config;
using BrainSim.Engine.Effectors;
using BrainSim.Engine.Llm;

namespace BrainSim.Engine;

public sealed record AgentReply(string Text, AppraisalSignals Signals, SamplingParameters Sampling, int Tokens);

/// <summary>Everything needed to pick a session back up: brain, appraisal and conversation.</summary>
public sealed record AgentCheckpoint(BrainCheckpoint Brain, AppraiserState Appraisal, List<ChatMessage> History)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public async Task SaveAsync(string path, CancellationToken cancellationToken = default)
    {
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(this, Json), cancellationToken);
        File.Move(temp, path, overwrite: true); // never leave a half-written checkpoint behind
    }

    public static async Task<AgentCheckpoint?> LoadAsync(string path, CancellationToken cancellationToken = default) =>
        File.Exists(path) ? JsonSerializer.Deserialize<AgentCheckpoint>(await File.ReadAllTextAsync(path, cancellationToken), Json) : null;
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
    private readonly List<ChatMessage> _history = [];

    public Agent(EngineConfig config, LmStudioClient llm, IReadOnlyDictionary<string, LinearProbe> probes, Func<DateTime> clock)
    {
        _config = config;
        _llm = llm;
        _clock = clock;
        Brain = new Brain(config, clock());
        Appraiser = new Appraiser(config.Appraisal, probes);
    }

    public Brain Brain { get; }
    public Appraiser Appraiser { get; }
    public IReadOnlyList<ChatMessage> History => _history;

    public async Task<AgentReply> SendAsync(string message, Action<string>? onToken = null, CancellationToken cancellationToken = default)
    {
        // 1. Catch up idle time, sleeping through it if the conditions were met. The message is light and wakes the brain.
        var now = _clock();
        Brain.AdvanceTo(now);
        Brain.OnUserMessage(Brain.Now);

        // 2 to 4. Appraise the message and schedule the impulses.
        var embedding = (await _llm.EmbedAsync([message], cancellationToken))[0];
        var signals = Appraiser.Appraise(embedding, hasPreviousResponse: _history.Any(m => m.Role == "assistant"));
        Appraiser.Release(signals, Brain.Chemistry);

        // 5. Reduced mode: sampling is fixed from the chemistry at the start of the response.
        var sampling = Sampling.Compute(Brain.Chemistry.C, _config.Sampling);
        _history.Add(ChatMessage.User(message));
        List<ChatMessage> request = [ChatMessage.System(_config.Llm.SystemPrompt), .. _history.TakeLast(_config.Llm.HistoryMessages)];

        // 6 to 8. One tick per streamed token.
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

        // 10. Effort and expected reward. Step 11, the sleep decision, runs on every following idle tick.
        _history.Add(ChatMessage.Assistant(text.ToString()));
        Brain.OnResponseFinished(tokens);
        Appraiser.LearnExpectedReward();
        return new AgentReply(text.ToString(), signals, sampling, tokens);
    }

    /// <summary>12. Snapshot for the next session.</summary>
    public AgentCheckpoint Checkpoint() => new(Brain.Save(), Appraiser.Save(), [.. _history]);

    public void Restore(AgentCheckpoint checkpoint)
    {
        Brain.Restore(checkpoint.Brain);
        Appraiser.Restore(checkpoint.Appraisal);
        _history.Clear();
        _history.AddRange(checkpoint.History);
    }
}
