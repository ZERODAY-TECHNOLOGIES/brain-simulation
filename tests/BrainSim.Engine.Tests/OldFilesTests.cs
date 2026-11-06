using BrainSim.Engine.Chemistry;
using BrainSim.Engine.Config;
using BrainSim.Engine.Journal;
using Parquet;
using Parquet.Schema;

namespace BrainSim.Engine.Tests;

/// <summary>Files written before Phase 8 have ten substances. They still load, with AEA and 2-AG at baseline (D-36).</summary>
public class OldFilesTests
{
    private const int Old = 10;
    private static readonly int[] Ecb = [(int)Substance.Anandamide, (int)Substance.Arachidonoylglycerol];

    [Fact]
    public void Old_checkpoint_with_ten_substances_loads_with_ecb_at_baseline()
    {
        var start = new DateTime(2026, 11, 6, 12, 0, 0);
        var brain = new Brain(EngineConfig.LoadDefault(), start);
        brain.Chemistry.Release(Substance.Cortisol, 1.0);
        brain.AdvanceTo(start.AddSeconds(30));
        var now = brain.Save();
        var old = now with
        {
            Chemistry = now.Chemistry with { C = now.Chemistry.C[..Old], Pending = [.. now.Chemistry.Pending.Select(p => p[..Old])] },
            MeanC = now.MeanC[..Old],
            Rho = now.Rho[..Old],
        };

        var restored = new Brain(EngineConfig.LoadDefault(), start);
        restored.Restore(old);

        var saved = restored.Save();
        Assert.Equal(now.Chemistry.C[..Old], saved.Chemistry.C[..Old]);
        Assert.Equal(now.MeanC[..Old], saved.MeanC[..Old]);
        foreach (var i in Ecb)
        {
            Assert.Equal(1.0, saved.Chemistry.C[i]);
            Assert.Equal(1.0, saved.MeanC[i]);
            Assert.Equal(1.0, saved.Rho[i]);
            Assert.All(saved.Chemistry.Pending, p => Assert.Equal(0.0, p[i]));
        }
    }

    [Fact]
    public void Old_checkpoint_json_has_no_sleep_debt()
    {
        var brain = new Brain(EngineConfig.LoadDefault(), new DateTime(2026, 11, 6, 12, 0, 0));
        var json = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(brain.Save()))!.AsObject();
        json.Remove("LastSleepHours");

        var old = System.Text.Json.JsonSerializer.Deserialize<BrainCheckpoint>(json.ToJsonString())!;

        Assert.Equal(EngineConfig.LoadDefault().Endocannabinoid.FullNightHours, old.LastSleepHours);
    }

    [Fact]
    public async Task Journal_without_ecb_columns_reads_with_baseline_values()
    {
        var path = Path.Combine(Path.GetTempPath(), $"brainsim-old-journal-{Guid.NewGuid():N}.parquet");
        try
        {
            await WriteOldJournalAsync(path, rows: 3);

            var read = await TickJournal.ReadAllAsync(path);

            Assert.Equal(3, read.Count);
            foreach (var r in read)
            {
                Assert.Equal(Substances.Count, r.C.Length);
                Assert.Equal(1.5, r.C[(int)Substance.Cortisol]);
                Assert.Equal(0.0, r.Cb1);
                foreach (var i in Ecb)
                {
                    Assert.Equal(1.0, r.C[i]);
                    Assert.Equal(1.0, r.Baseline[i]);
                    Assert.Equal(1.0, r.MeanC[i]);
                    Assert.Equal(1.0, r.Rho[i]);
                }
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>The Phase 7 layout: ten substances per vector, one channel, CORT at 1.5 and everything else at 1.0.</summary>
    private static async Task WriteOldJournalAsync(string path, int rows)
    {
        var abbreviations = Substances.Abbreviations.Take(Old).ToArray();
        DataField<double>[] PerSubstance(string prefix) => [.. abbreviations.Select(a => new DataField<double>(prefix + a))];
        var tick = new DataField<long>("tick");
        var time = new DataField<long>("time");
        var phase = new DataField<string>("phase");
        var cycle = new DataField<int>("cycle");
        var phi = new DataField<double>("phi");
        var pressure = new DataField<double>("sleep_pressure");
        var token = new DataField<string>("token", true);
        var vectors = new[] { PerSubstance("c_"), PerSubstance("baseline_"), PerSubstance("mean_c_"), PerSubstance("rho_") };
        var channel = new DataField<double>("m_calm_anxious");
        var schema = new ParquetSchema([tick, time, phase, cycle, phi, pressure, .. vectors.SelectMany(v => v), channel, token]);

        await using var stream = File.Create(path);
        await using var writer = await ParquetWriter.CreateAsync(schema, stream);
        using var group = writer.CreateRowGroup();
        double[] Fill(double value) => Enumerable.Repeat(value, rows).ToArray();
        await group.WriteAsync(tick, (ReadOnlyMemory<long>)Enumerable.Range(0, rows).Select(i => (long)i).ToArray());
        await group.WriteAsync(time, (ReadOnlyMemory<long>)Enumerable.Range(0, rows).Select(i => new DateTime(2026, 11, 4).AddSeconds(i).Ticks).ToArray());
        await group.WriteAsync(phase, Enumerable.Repeat("Wake", rows).ToArray());
        await group.WriteAsync(cycle, (ReadOnlyMemory<int>)new int[rows]);
        await group.WriteAsync(phi, (ReadOnlyMemory<double>)Fill(12));
        await group.WriteAsync(pressure, (ReadOnlyMemory<double>)Fill(0));
        foreach (var field in vectors.SelectMany(v => v))
        {
            await group.WriteAsync(field, (ReadOnlyMemory<double>)Fill(field.Name == "c_CORT" ? 1.5 : 1.0));
        }

        await group.WriteAsync(channel, (ReadOnlyMemory<double>)Fill(0));
        await group.WriteAsync(token, new string?[rows]);
    }
}
