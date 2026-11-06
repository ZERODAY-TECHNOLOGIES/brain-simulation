using BrainSim.Engine.Config;
using BrainSim.Engine.Journal;

namespace BrainSim.Engine.Tests;

public class TickJournalTests
{
    [Fact]
    public async Task Every_tick_round_trips_through_parquet_across_row_groups()
    {
        var brain = new Brain(EngineConfig.LoadDefault(), new DateTime(2026, 11, 4, 23, 0, 0));
        var path = Path.Combine(Path.GetTempPath(), $"brainsim-journal-{Guid.NewGuid():N}.parquet");
        var written = new List<TickRecord>();

        try
        {
            await using (var journal = await TickJournal.CreateAsync(path, brain.Modulation.Names, rowGroupSize: 1000))
            {
                brain.Chemistry.Release(Substance.Dopamine, 2.0);
                brain.Chemistry.Release(Substance.Arachidonoylglycerol, 1.0); // so CB1 tone moves
                brain.Ticked += (b, token) =>
                {
                    var record = TickRecord.From(b, token);
                    written.Add(record);
                    journal.Append(record);
                };

                for (var t = 0; t < 2500; t++)
                {
                    brain.Tick(brain.Now.AddMilliseconds(100), t % 3 == 0 ? $"tok{t}" : null);
                }
            }

            var read = await TickJournal.ReadAllAsync(path);

            Assert.Equal(written.Count, read.Count);
            for (var i = 0; i < written.Count; i++)
            {
                var (w, r) = (written[i], read[i]);
                Assert.Equal((w.Tick, w.Time, w.Phase, w.Cycle, w.Phi, w.SleepPressure, w.Token), (r.Tick, r.Time, r.Phase, r.Cycle, r.Phi, r.SleepPressure, r.Token));
                Assert.Equal(w.C, r.C);
                Assert.Equal(w.Baseline, r.Baseline);
                Assert.Equal(w.MeanC, r.MeanC);
                Assert.Equal(w.Rho, r.Rho);
                Assert.Equal(w.M, r.M);
                Assert.Equal(w.Cb1, r.Cb1);
            }

            Assert.Contains(read, r => r.Cb1 > 0.01);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void The_history_stays_bounded_when_every_tick_carries_a_token()
    {
        var history = new TickHistory(capacity: 100);
        double[] c = [1, 1, 1, 1, 1, 1, 1, 1, 1, 1];

        for (var t = 0; t < 2_000; t++)
        {
            history.Append(new TickRecord(t, new DateTime(2026, 11, 4).AddSeconds(t / 10.0), "Wake", 0, 0, 0, c, c, c, c, [], $"t{t}"));
        }

        Assert.InRange(history.Count, 1, 100);
        Assert.Equal(1_999, history.Latest!.Tick);
    }

    // Capacity 8, nine ticks 0..8: the older half (0..3) keeps every second tick plus the token ticks,
    // the newer half (4..8) stays whole. If the token ticks would still overflow, the kept ones are halved again.
    [Theory]
    [InlineData(new long[0], new long[] { 0, 2, 4, 5, 6, 7, 8 })]
    [InlineData(new long[] { 1 }, new long[] { 0, 1, 2, 4, 5, 6, 7, 8 })]
    [InlineData(new long[] { 0, 1, 2, 3, 4, 5, 6, 7, 8 }, new long[] { 0, 2, 4, 5, 6, 7, 8 })]
    public void A_full_history_thins_its_older_half_and_keeps_token_ticks(long[] tokenTicks, long[] expected)
    {
        var history = new TickHistory(capacity: 8);

        for (var t = 0L; t <= 8; t++)
        {
            history.Append(Record(t, tokenTicks.Contains(t) ? $"t{t}" : null));
        }

        Assert.Equal(expected, history.Window(0, long.MaxValue, int.MaxValue).Select(r => r.Tick));
    }

    [Fact]
    public void An_empty_history_and_a_window_before_the_first_tick_give_nothing()
    {
        var history = new TickHistory();
        Assert.Null(history.First);
        Assert.Null(history.At(5));
        Assert.Empty(history.Window(0, 10, 100));

        history.Append(Record(10, null));
        history.Append(Record(20, null));

        Assert.Equal(10, history.First!.Tick);
        Assert.Empty(history.Window(0, 5, 100));
    }

    private static TickRecord Record(long tick, string? token)
    {
        double[] c = [1, 1, 1, 1, 1, 1, 1, 1, 1, 1];
        return new TickRecord(tick, new DateTime(2026, 11, 4).AddSeconds(tick / 10.0), "Wake", 0, 0, 0, c, c, c, c, [], token);
    }
}
