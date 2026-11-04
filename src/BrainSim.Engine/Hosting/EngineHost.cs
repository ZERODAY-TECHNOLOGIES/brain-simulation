using System.Collections.Concurrent;
using BrainSim.Engine.Appraisal;
using BrainSim.Engine.Config;
using BrainSim.Engine.Journal;
using BrainSim.Engine.Llm;

namespace BrainSim.Engine.Hosting;

/// <summary>
/// Runs the agent for the app (architecture: "System overview"). One sequential loop owns all engine state:
/// it applies queued commands at tick boundaries, answers queued messages, and otherwise advances idle time.
/// Every tick goes to the in-memory history and the Parquet journal; every event goes to the events journal.
/// Events are raised on the engine loop, so UI handlers must marshal to their own thread.
/// </summary>
public sealed class EngineHost : IAsyncDisposable
{
    private static readonly TimeSpan LoopInterval = TimeSpan.FromMilliseconds(33);

    private readonly ConcurrentQueue<Action<Agent>> _commands = new();
    private readonly ConcurrentQueue<string> _messages = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly string _dataDirectory;
    private Task? _loop;
    private TickJournal? _journal;

    public EngineHost(EngineConfig config, LmStudioClient llm, IReadOnlyDictionary<string, LinearProbe> probes, string dataDirectory, SimulationClock? clock = null)
    {
        _dataDirectory = dataDirectory;
        Clock = clock ?? new SimulationClock();
        Agent = new Agent(config, llm, probes, () => Clock.Now);
    }

    public Agent Agent { get; }
    public SimulationClock Clock { get; }
    public TickHistory History { get; } = new();
    public EventLog Events { get; private set; } = new();
    public string? JournalPath { get; private set; }
    public string CheckpointPath => Path.Combine(_dataDirectory, "checkpoint.json");

    /// <summary>The conversation restored from the checkpoint, from sessions before this one.</summary>
    public IReadOnlyList<ChatMessage> EarlierMessages { get; private set; } = [];

    /// <summary>A reply is streaming. Read from the UI thread, so it is only a hint.</summary>
    public bool IsGenerating => Agent.Brain.IsGenerating;

    public event Action<string, AppraisalSignals, SamplingParameters>? Appraised;
    public event Action<string>? TokenReceived;
    public event Action<AgentReply>? Replied;
    public event Action<string>? Failed;

    public async Task StartAsync()
    {
        var journalDirectory = Path.Combine(_dataDirectory, "journal");
        Directory.CreateDirectory(journalDirectory);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        JournalPath = Path.Combine(journalDirectory, $"session-{stamp}.parquet");
        _journal = await TickJournal.CreateAsync(JournalPath, Agent.Brain.Modulation.Names);
        Events = new EventLog(EventsPath(JournalPath));

        if (await AgentCheckpoint.LoadAsync(CheckpointPath) is { } checkpoint)
        {
            Agent.Restore(checkpoint);
            EarlierMessages = [.. Agent.History];

            // A brain that ran ahead in simulated time would freeze in real mode, so keep it on its own clock.
            if (Agent.Brain.Now > DateTime.Now)
            {
                Clock.SetSimulated(1, Agent.Brain.Now);
                Log(EventKinds.Clock, "The brain is ahead of real time, so it keeps running on simulated time at x1.");
            }
        }

        Agent.Brain.Ticked += OnTicked;
        Agent.Brain.Transition += t => Log(EventKinds.Sleep, $"{t.Kind} from {t.From}");
        _loop = Task.Run(() => LoopAsync(_stop.Token));
    }

    public static string EventsPath(string journalPath) => Path.ChangeExtension(journalPath, ".events.jsonl");

    /// <summary>Queues a user message. Messages are answered one at a time, in order.</summary>
    public void Send(string message) => _messages.Enqueue(message);

    /// <summary>Runs <paramref name="command"/> on the engine loop at the next tick boundary.</summary>
    public void Post(Action<Agent> command) => _commands.Enqueue(command);

    public void Inject(Substance s, double amount) => Post(a =>
    {
        a.Brain.Chemistry.Release(s, amount);
        Log(EventKinds.Lab, $"Inject {s.Abbreviation()} {amount:+0.00;-0.00}", new() { [s.Abbreviation()] = amount });
    });

    public void Hold(Substance s, double? level) => Post(a =>
    {
        a.Brain.Chemistry.Hold[(int)s] = level ?? double.NaN;
        Log(EventKinds.Lab, level is { } l ? $"Hold {s.Abbreviation()} at {l:0.00}" : $"Release hold on {s.Abbreviation()}");
    });

    public void SetHalfLife(Substance s, double halfLife) => Post(a =>
    {
        a.Brain.Chemistry.SetHalfLife(s, halfLife);
        Log(EventKinds.Lab, $"Half-life of {s.Abbreviation()} set to {halfLife:0} ticks");
    });

    public void SetInteraction(Substance from, Substance to, double w) => Post(a =>
    {
        a.Brain.Chemistry.SetInteraction(from, to, w);
        Log(EventKinds.Lab, $"W {from.Abbreviation()} to {to.Abbreviation()} set to {w:0.#####}");
    });

    public void SetBaseline(Substance s, double level) => Post(a =>
    {
        a.Brain.Chemistry.SetResting(s, level);
        Log(EventKinds.Lab, $"Baseline of {s.Abbreviation()} set to {level:0.00}");
    });

    public void SetGain(string signal, Substance s, double gain) => Post(a =>
    {
        a.Appraiser.SetGain(signal, s, gain);
        Log(EventKinds.Lab, $"Gain of {signal} on {s.Abbreviation()} set to {gain:0.###}");
    });

    public void ResetLab() => Post(a =>
    {
        a.Brain.Chemistry.ResetParameters();
        a.Appraiser.ResetGains();
        Log(EventKinds.Lab, "Lab reset to config.yaml");
    });

    public void SetSimulated(double factor) => Post(a =>
    {
        Clock.SetSimulated(factor, a.Brain.Now);
        Log(EventKinds.Clock, factor == 0 ? "Simulated time paused" : $"Simulated time at x{factor:0.##}");
    });

    public void SetReal() => Post(_ =>
    {
        Clock.SetReal();
        Log(EventKinds.Clock, "Real time");
    });

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_loop is not null)
        {
            await _loop;
        }

        if (_journal is not null)
        {
            await Agent.Checkpoint().SaveAsync(CheckpointPath);
            await _journal.DisposeAsync();
        }

        Events.Dispose();
        _stop.Dispose();
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                DrainCommands();
                if (_messages.TryDequeue(out var message))
                {
                    await ReplyAsync(message, cancellationToken);
                }
                else
                {
                    Agent.Brain.AdvanceTo(Clock.Now);
                }

                await Task.Delay(LoopInterval, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                Report(e.Message);
            }
        }
    }

    private async Task ReplyAsync(string message, CancellationToken cancellationToken)
    {
        try
        {
            var reply = await Agent.SendAsync(
                message,
                token => TokenReceived?.Invoke(token),
                cancellationToken,
                (signals, sampling) =>
                {
                    var values = new Dictionary<string, double>
                    {
                        ["S"] = signals.Surprise,
                        ["A"] = signals.Threat,
                        ["W"] = signals.Warmth,
                        ["delta"] = signals.RewardError,
                    };
                    if (signals.Reward is { } r)
                    {
                        values["r"] = r; // the first message of a conversation has no reward
                    }

                    Log(EventKinds.User, message, values);
                    Appraised?.Invoke(message, signals, sampling);
                });

            Log(EventKinds.Agent, reply.Text, new()
            {
                ["T"] = reply.Sampling.Temperature,
                ["top_p"] = reply.Sampling.TopP,
                ["max_tokens"] = reply.Sampling.MaxTokens,
                ["tokens"] = reply.Tokens,
            });
            Replied?.Invoke(reply);
            await Agent.Checkpoint().SaveAsync(CheckpointPath, cancellationToken);
        }
        catch (HttpRequestException e)
        {
            Report($"LM Studio did not answer: {e.Message}");
        }
    }

    private void OnTicked(Brain brain, string? token)
    {
        var record = TickRecord.From(brain, token);
        History.Append(record);
        _journal?.Append(record);
        DrainCommands();
    }

    private void DrainCommands()
    {
        while (_commands.TryDequeue(out var command))
        {
            command(Agent);
        }
    }

    private void Log(string kind, string text, Dictionary<string, double>? values = null) =>
        Events.Add(new EventRecord(Agent.Brain.Chemistry.Tick, Agent.Brain.Now, kind, text, values));

    private void Report(string message)
    {
        Log(EventKinds.Error, message);
        Failed?.Invoke(message);
    }
}

/// <summary>A finished session loaded back from its journals.</summary>
public sealed record ReplaySession(string Path, TickHistory History, EventLog Events)
{
    public static async Task<ReplaySession> LoadAsync(string journalPath)
    {
        var history = new TickHistory();
        await TickJournal.ReadAsync(journalPath, history.Append);
        return new ReplaySession(journalPath, history, EventLog.Read(EngineHost.EventsPath(journalPath)));
    }
}
