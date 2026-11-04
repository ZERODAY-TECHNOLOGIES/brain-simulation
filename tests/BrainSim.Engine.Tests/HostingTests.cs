using BrainSim.Engine.Appraisal;
using BrainSim.Engine.Config;
using BrainSim.Engine.Hosting;
using BrainSim.Engine.Journal;
using BrainSim.Engine.Llm;
using BrainSim.Engine.Memory;

namespace BrainSim.Engine.Tests;

public class HostingTests
{
    private static readonly EngineConfig Config = EngineConfig.LoadDefault();

    private static TickRecord Record(long tick, string? token = null) =>
        new(tick, DateTime.MinValue, "Wake", 0, 0, 0, [], [], [], [], [], token);

    [Fact]
    public void History_stays_bounded_keeps_recent_ticks_whole_and_keeps_tokens()
    {
        var history = new TickHistory(capacity: 1000);
        for (var t = 0; t < 10_000; t++)
        {
            history.Append(Record(t, t == 50 ? "kept" : null));
        }

        Assert.InRange(history.Count, 500, 1000);
        Assert.Equal(9_999, history.Latest!.Tick);
        Assert.Equal("kept", history.At(50)!.Token);

        // The newest 400 ticks are all still there.
        var recent = history.Window(9_600, 9_999, maxPoints: 10_000);
        Assert.Equal(400, recent.Count);

        var coarse = history.Window(0, 9_999, maxPoints: 100);
        Assert.InRange(coarse.Count, 50, 101);
        Assert.Equal(9_999, coarse[^1].Tick);
    }

    [Fact]
    public void Simulated_clock_runs_faster_changes_speed_without_jumps_and_pauses()
    {
        var wall = new DateTime(2026, 11, 4, 12, 0, 0);
        var clock = new SimulationClock(() => wall);
        var start = new DateTime(2026, 11, 4, 20, 0, 0);

        clock.SetSimulated(60, start);
        wall = wall.AddMinutes(1);
        Assert.Equal(start.AddHours(1), clock.Now);

        clock.SetFactor(0);
        wall = wall.AddMinutes(10);
        Assert.Equal(start.AddHours(1), clock.Now);

        clock.SetReal();
        Assert.Equal(wall, clock.Now);
    }

    [Fact]
    public async Task Host_answers_logs_journals_applies_the_lab_and_replays()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"brainsim-host-{Guid.NewGuid():N}");
        var llm = new LmStudioClient(new HttpClient(new FakeLmStudio(20)) { BaseAddress = new Uri(Config.Llm.BaseUrl) }, Config.Llm);
        string journalPath;

        try
        {
            await using (var host = new EngineHost(Config, llm, new Dictionary<string, LinearProbe>(), directory))
            {
                var replied = new TaskCompletionSource<AgentReply>();
                var tokens = 0;
                host.TokenReceived += _ => tokens++;
                host.Replied += r => replied.TrySetResult(r);
                host.Failed += e => replied.TrySetException(new InvalidOperationException(e));
                await host.StartAsync();
                journalPath = host.JournalPath!;

                host.Send("hello");
                var reply = await replied.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
                Assert.Equal(20, reply.Tokens);
                Assert.Equal(20, tokens);
                await WaitUntilAsync(() => host.Events.Snapshot().Any(e => e.Kind == EventKinds.Memory), ct); // a fact learned

                host.Inject(Substance.Dopamine, 2.0);
                await WaitUntilAsync(() => host.Events.Snapshot().Any(e => e.Kind == EventKinds.Lab), ct);
                await WaitUntilAsync(() => host.History.Latest!.C[(int)Substance.Dopamine] > 1.5, ct);
            }

            Assert.True(File.Exists(Path.Combine(directory, "checkpoint.json")));
            Assert.Contains(MemoryFile.Load(Path.Combine(directory, "memory.bin")), c => c.Kind == ChunkKind.Episode && c.Text.StartsWith("hello"));
            var replay = await ReplaySession.LoadAsync(journalPath);
            Assert.True(replay.History.Count > 20);
            Assert.Equal(20, replay.History.Window(0, long.MaxValue, int.MaxValue).Count(r => r.Token is not null));
            Assert.Equal([EventKinds.User, EventKinds.Agent, EventKinds.Memory, EventKinds.Lab], replay.Events.Snapshot().Select(e => e.Kind));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Closing_right_after_a_reply_keeps_its_facts()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"brainsim-host-{Guid.NewGuid():N}");
        var fake = new FakeLmStudio(20) { ExtractionDelay = TimeSpan.FromMilliseconds(500) };
        var llm = new LmStudioClient(new HttpClient(fake) { BaseAddress = new Uri(Config.Llm.BaseUrl) }, Config.Llm);
        try
        {
            await using (var host = new EngineHost(Config, llm, new Dictionary<string, LinearProbe>(), directory))
            {
                var replied = new TaskCompletionSource();
                host.Replied += _ => replied.TrySetResult();
                await host.StartAsync();
                host.Send("hello");
                await replied.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            } // closed while the extraction is still running

            Assert.Contains(MemoryFile.Load(Path.Combine(directory, "memory.bin")), c => c.Kind == ChunkKind.Fact);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task A_phase_6_checkpoint_brings_its_dream_fragments_into_memory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"brainsim-host-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var llm = new LmStudioClient(new HttpClient(new FakeLmStudio(20)) { BaseAddress = new Uri(Config.Llm.BaseUrl) }, Config.Llm);
        try
        {
            var old = new Agent(Config, llm, new Dictionary<string, LinearProbe>(), () => DateTime.Now).Checkpoint() with
            {
                Dreams = new Dreams.DreamState([new(DateTime.Now.AddHours(-2), "a red kite", 0.4), new(DateTime.Now.AddHours(-1), "the lake", 0.1)], []),
            };
            await old.SaveAsync(Path.Combine(directory, "checkpoint.json"), TestContext.Current.CancellationToken);

            await using var host = new EngineHost(Config, llm, new Dictionary<string, LinearProbe>(), directory);
            await host.StartAsync();

            Assert.Equal(["a red kite", "the lake"], host.Agent.Memory.Snapshot().Select(c => c.Text));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting for the engine loop");
            await Task.Delay(20, ct);
        }
    }
}
