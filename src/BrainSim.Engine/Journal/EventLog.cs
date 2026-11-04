using System.Text.Json;

namespace BrainSim.Engine.Journal;

/// <summary>Kinds of journal events. Messages, Lab actions, sleep transitions, clock changes and errors.</summary>
public static class EventKinds
{
    public const string User = "user";
    public const string Agent = "agent";
    public const string Lab = "lab";
    public const string Sleep = "sleep";
    public const string Clock = "clock";
    public const string Dream = "dream";
    public const string Memory = "memory";
    public const string Error = "error";
}

/// <summary>One event. <paramref name="Values"/> holds the numbers worth keeping (signals, sampling, Lab amounts).</summary>
public sealed record EventRecord(long Tick, DateTime Time, string Kind, string Text, Dictionary<string, double>? Values = null);

/// <summary>
/// Events journal as JSON lines next to the tick journal: one line per event, flushed as it is written,
/// so it survives a crash and can be read while the session is still running.
/// </summary>
public sealed class EventLog : IDisposable
{
    private readonly Lock _gate = new();
    private readonly List<EventRecord> _events = [];
    private readonly StreamWriter? _writer;

    /// <summary>In-memory only, for replays and tests.</summary>
    public EventLog(IEnumerable<EventRecord>? events = null)
    {
        _events.AddRange(events ?? []);
    }

    public EventLog(string path)
    {
        _writer = new StreamWriter(path, append: true) { AutoFlush = true };
    }

    public event Action<EventRecord>? Added;

    public IReadOnlyList<EventRecord> Snapshot()
    {
        lock (_gate)
        {
            return [.. _events];
        }
    }

    public void Add(EventRecord record)
    {
        lock (_gate)
        {
            _events.Add(record);
            _writer?.WriteLine(JsonSerializer.Serialize(record));
        }

        Added?.Invoke(record);
    }

    public static EventLog Read(string path) =>
        new(File.Exists(path)
            ? File.ReadLines(path).Where(l => l.Length > 0).Select(l => JsonSerializer.Deserialize<EventRecord>(l)!)
            : []);

    public void Dispose() => _writer?.Dispose();
}
