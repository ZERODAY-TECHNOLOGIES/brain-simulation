using BrainSim.Engine.Chemistry;
using BrainSim.Engine.Config;
using BrainSim.Engine.Effectors;
using BrainSim.Engine.Journal;
using BrainSim.Engine.Receptors;

namespace BrainSim.Engine.Tests;

public class TickJournalTests
{
    [Fact]
    public async Task Every_tick_round_trips_through_parquet_across_row_groups()
    {
        var config = EngineConfig.LoadDefault();
        var chemistry = new ChemicalState(config);
        var receptors = new ReceptorState(config);
        var modulation = new Modulation(config);
        var path = Path.Combine(Path.GetTempPath(), $"brainsim-journal-{Guid.NewGuid():N}.parquet");
        var written = new List<TickRecord>();

        try
        {
            await using (var journal = await TickJournal.CreateAsync(path, modulation.Names, rowGroupSize: 1000))
            {
                chemistry.Release(Substance.Dopamine, 2.0);
                for (var t = 0; t < 2500; t++)
                {
                    chemistry.Step();
                    receptors.Step(chemistry.C);
                    modulation.Compute(chemistry.C, receptors);
                    var record = new TickRecord(
                        chemistry.Tick, "wake", [.. chemistry.C], [.. receptors.MeanC], [.. receptors.Rho], [.. modulation.M],
                        t % 3 == 0 ? $"tok{t}" : null);
                    written.Add(record);
                    await journal.AppendAsync(record);
                }
            }

            var read = await TickJournal.ReadAllAsync(path);

            Assert.Equal(written.Count, read.Count);
            for (var i = 0; i < written.Count; i++)
            {
                Assert.Equal(written[i].Tick, read[i].Tick);
                Assert.Equal(written[i].Phase, read[i].Phase);
                Assert.Equal(written[i].Token, read[i].Token);
                Assert.Equal(written[i].C, read[i].C);
                Assert.Equal(written[i].MeanC, read[i].MeanC);
                Assert.Equal(written[i].Rho, read[i].Rho);
                Assert.Equal(written[i].M, read[i].M);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
}
