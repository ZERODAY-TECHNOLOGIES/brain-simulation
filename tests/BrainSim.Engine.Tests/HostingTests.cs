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
    public async Task A_signed_amount_that_rounds_to_zero_is_logged_without_a_double_sign()
    {
        // .NET formats -0.001 with "+0.00;-0.00" as "-+0.00": a negative that rounds to zero falls back to the
        // positive section and keeps its minus. Found as "-+0.000" in the Effectors panel.
        var ct = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"brainsim-host-{Guid.NewGuid():N}");
        var llm = new LmStudioClient(new HttpClient(new FakeLmStudio(20)) { BaseAddress = new Uri(Config.Llm.BaseUrl) }, Config.Llm);
        try
        {
            await using var host = new EngineHost(Config, llm, new Dictionary<string, LinearProbe>(), directory);
            await host.StartAsync();

            host.Inject(Substance.Dopamine, -0.001);
            host.Inject(Substance.Dopamine, -0.5);
            await WaitUntilAsync(() => host.Events.Snapshot().Count(e => e.Kind == EventKinds.Lab) == 2, ct);

            Assert.Equal(["Inject DA 0.00", "Inject DA -0.50"], host.Events.Snapshot().Where(e => e.Kind == EventKinds.Lab).Select(e => e.Text));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task A_later_message_logs_its_reward_and_what_it_recalled()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"brainsim-host-{Guid.NewGuid():N}");
        var llm = new LmStudioClient(new HttpClient(new FakeLmStudio(20)) { BaseAddress = new Uri(Config.Llm.BaseUrl) }, Config.Llm);
        try
        {
            await using var host = new EngineHost(Config, llm, new Dictionary<string, LinearProbe>(), directory);
            var replies = 0;
            host.Replied += _ => Interlocked.Increment(ref replies);
            await host.StartAsync();

            host.Send("hello");
            await WaitUntilAsync(() => host.Events.Snapshot().Any(e => e.Text.StartsWith("Learned: ")), ct);
            host.Send("hello again"); // the fake embeds every message alike, so this cues the fact
            await WaitUntilAsync(() => replies == 2, ct);

            var events = host.Events.Snapshot();
            Assert.Contains("r", events.Last(e => e.Kind == EventKinds.User).Values!.Keys);
            Assert.Equal(1, events.Last(e => e.Kind == EventKinds.User).Values!["M"]);
            Assert.Contains(events, e => e.Kind == EventKinds.Memory && e.Text.StartsWith("Recalled 1 fact: t0 t1"));
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

    [Fact]
    public async Task Lab_and_clock_commands_reach_the_brain_and_the_event_log()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"brainsim-host-{Guid.NewGuid():N}");
        var llm = new LmStudioClient(new HttpClient(new FakeLmStudio(20)) { BaseAddress = new Uri(Config.Llm.BaseUrl) }, Config.Llm);
        const int Da = (int)Substance.Dopamine, Ht = (int)Substance.Serotonin, Ado = (int)Substance.Adenosine, Gaba = (int)Substance.Gaba;
        static (double Hold, double[,] W, double Resting, double Gain) Lab(Agent a) => (
            a.Brain.Chemistry.Hold[Da],
            a.Brain.Chemistry.LinearizedMatrix(asleep: true),
            a.Brain.Chemistry.Resting[Gaba],
            a.Appraiser.Gains.Single(g => g is { Signal: "threat", Substance: Substance.Cortisol }).Gain);
        try
        {
            await using var host = new EngineHost(Config, llm, new Dictionary<string, LinearProbe>(), directory);
            await host.StartAsync();

            host.Hold(Substance.Dopamine, 1.5);
            host.SetHalfLife(Substance.Serotonin, 500);
            host.SetInteraction(Substance.Adenosine, Substance.Serotonin, 0.001);
            host.SetBaseline(Substance.Gaba, 1.2);
            host.SetGain("threat", Substance.Cortisol, 0.9);
            var changed = await OnLoopAsync(host, Lab, ct);
            Assert.Equal(1.5, changed.Hold);
            Assert.Equal(-Math.Log(2) / 500, changed.W[Ht, Ht], 12);
            Assert.Equal(0.001, changed.W[Ht, Ado]);
            Assert.Equal(1.2, changed.Resting);
            Assert.Equal(0.9, changed.Gain);

            host.Hold(Substance.Dopamine, null);
            host.ResetLab();
            var reset = await OnLoopAsync(host, Lab, ct);
            Assert.True(double.IsNaN(reset.Hold));
            Assert.Equal(new Chemistry.ChemicalState(Config).LinearizedMatrix(asleep: true), reset.W);
            Assert.Equal(1.0, reset.Resting);
            Assert.Equal(0.3, reset.Gain);

            host.SetSimulated(60);
            Assert.Equal((true, 60.0), await OnLoopAsync(host, _ => (host.Clock.Simulated, host.Clock.Factor), ct));
            host.SetSimulated(0);
            host.SetReal();
            Assert.False(await OnLoopAsync(host, _ => host.Clock.Simulated, ct));

            Assert.Equal(
                [
                    $"Hold DA at {1.5:0.00}", "Half-life of 5-HT set to 500 ticks", $"W ADO to 5-HT set to {0.001:0.#####}",
                    $"Baseline of GABA set to {1.2:0.00}", $"Gain of threat on CORT set to {0.9:0.###}", "Release hold on DA",
                    "Lab reset to config.yaml", "Simulated time at x60", "Simulated time paused", "Real time",
                ],
                host.Events.Snapshot().Where(e => e.Kind is EventKinds.Lab or EventKinds.Clock).Select(e => e.Text));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Failures_are_reported_and_the_engine_loop_keeps_going()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"brainsim-host-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "memory.bin"), "not a memory file");
        var fake = new FakeLmStudio(20);
        var llm = new LmStudioClient(new HttpClient(fake) { BaseAddress = new Uri(Config.Llm.BaseUrl) }, Config.Llm);
        try
        {
            await using var host = new EngineHost(Config, llm, new Dictionary<string, LinearProbe>(), directory);
            var failures = new System.Collections.Concurrent.ConcurrentQueue<string>();
            host.Failed += failures.Enqueue;
            await host.StartAsync();

            host.Post(_ => throw new InvalidOperationException("a command broke"));
            fake.Down = true;
            host.Send("hello"); // answered after the broken command, so the loop survived it
            await WaitUntilAsync(() => failures.Count == 3, ct);

            Assert.Equal(
                [
                    "memory.bin could not be read. It was set aside as memory.bin.bad and memory starts empty.",
                    "a command broke",
                    "LM Studio did not answer: LM Studio is not running",
                ],
                failures);
            Assert.Equal(failures, host.Events.Snapshot().Where(e => e.Kind == EventKinds.Error).Select(e => e.Text));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task A_brain_saved_ahead_of_real_time_keeps_running_on_simulated_time()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"brainsim-host-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var llm = new LmStudioClient(new HttpClient(new FakeLmStudio(20)) { BaseAddress = new Uri(Config.Llm.BaseUrl) }, Config.Llm);
        try
        {
            var tomorrow = DateTime.Now.AddDays(1);
            await new Agent(Config, llm, new Dictionary<string, LinearProbe>(), () => tomorrow).Checkpoint()
                .SaveAsync(Path.Combine(directory, "checkpoint.json"), ct);

            await using var host = new EngineHost(Config, llm, new Dictionary<string, LinearProbe>(), directory);
            await host.StartAsync();

            Assert.True(host.Clock.Simulated);
            Assert.Contains(host.Events.Snapshot(), e => e is
            {
                Kind: EventKinds.Clock, Text: "The brain is ahead of real time, so it keeps running on simulated time at x1.",
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task A_night_logs_sleep_replay_and_dreams_that_end_both_ways()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"brainsim-host-{Guid.NewGuid():N}");
        var llm = new LmStudioClient(
            new HttpClient(new FakeLmStudio(20) { Completion = "a red kite over the lake" }) { BaseAddress = new Uri(Config.Llm.BaseUrl) }, Config.Llm);
        var wall = new DateTime(2026, 11, 5, 1, 0, 0).Ticks; // the host runs on real time, read from this clock
        try
        {
            await using var host = new EngineHost(
                Config, llm, new Dictionary<string, LinearProbe>(), directory, new SimulationClock(() => new DateTime(Interlocked.Read(ref wall))));
            var replies = 0;
            host.Replied += _ => Interlocked.Increment(ref replies);
            await host.StartAsync();
            List<string> Texts(string kind) => [.. host.Events.Snapshot().Where(e => e.Kind == kind).Select(e => e.Text)];

            // ADO at its limit forces sleep. Cycle 1 REM runs from minute 81 to 90 after onset.
            var calm = Enumerable.Repeat(1.0, Substances.Count).ToArray();
            host.Post(a => a.Memory.AddEpisode("a walk by the lake", null, calm, default, a.Brain.Now));
            host.Post(a => a.Brain.Chemistry[Substance.Adenosine] = Config.Sleep.ForcedAdenosine);
            await OnLoopAsync(host, _ => 0, ct);
            Interlocked.Add(ref wall, TimeSpan.FromMinutes(85).Ticks);
            await WaitUntilAsync(() => Texts(EventKinds.Sleep).Contains("Dream started from 1 memories"), ct, seconds: 60); // about 50,000 ticks
            host.Send("good morning"); // wakes it mid-dream
            await WaitUntilAsync(() => replies == 1, ct);

            // Asleep again; this time REM ends on its own.
            host.Post(a => a.Brain.Chemistry[Substance.Adenosine] = Config.Sleep.ForcedAdenosine);
            await OnLoopAsync(host, _ => 0, ct);
            Interlocked.Add(ref wall, TimeSpan.FromMinutes(95).Ticks);
            await WaitUntilAsync(() => Texts(EventKinds.Dream).Count == 2, ct, seconds: 60);

            Assert.Contains("ForcedSleep from Wake", Texts(EventKinds.Sleep));
            Assert.Contains("ForcedWake from Rem", Texts(EventKinds.Sleep));
            Assert.Contains(Texts(EventKinds.Memory), t => t.StartsWith("Replayed episode "));
            var dreams = host.Events.Snapshot().Where(e => e.Kind == EventKinds.Dream).ToList();
            Assert.All(dreams, d => Assert.Equal(["seeds", "minutes", "interrupted", "NE", "5-HT", "ACh"], d.Values!.Keys));
            Assert.Equal([1.0, 0.0], dreams.Select(d => d.Values!["interrupted"]));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Runs <paramref name="read"/> on the engine loop, after every command posted before it.</summary>
    private static async Task<T> OnLoopAsync<T>(EngineHost host, Func<Agent, T> read, CancellationToken ct)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Post(a => result.SetResult(read(a)));
        return await result.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct, int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting for the engine loop");
            await Task.Delay(20, ct);
        }
    }
}
