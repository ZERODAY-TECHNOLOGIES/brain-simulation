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
            }
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
}
