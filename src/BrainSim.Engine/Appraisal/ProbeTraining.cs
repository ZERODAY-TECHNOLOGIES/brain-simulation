using System.Text.Json.Serialization;
using BrainSim.Engine.Config;
using BrainSim.Engine.Llm;
using Parquet;
using Parquet.Serialization;

namespace BrainSim.Engine.Appraisal;

/// <summary>The probes that cleared the AUC gate, as a file for this embedding setup, and the names of those that did not.</summary>
public sealed record ProbeTrainingResult(ProbeFile File, IReadOnlyList<string> Rejected);

/// <summary>
/// Trains the four appraisal probes (D-05) for one embedding model and prefix: GoEmotions for warmth, positive and
/// negative, tweet_eval offensive for hostility. A probe ships only if it clears AUC > 0.85 on the held-out split
/// (spec: "Evaluarea"). Used by Settings for any embedding backend and by tools/ProbeTrainer for the shipped file (D-39).
/// Downloads are cached in <c>cacheDir</c>, embeddings per model and prefix below it, so a rerun is free.
/// </summary>
public sealed class ProbeTraining(HttpClient http, LmStudioClient llm, LlmConfig embedding, string cacheDir)
{
    public const double AucGate = 0.85;
    private const int Seed = 1;
    private const int Batch = 256;

    private static readonly int[] Warm = [0, 5, 15, 18];                                         // admiration, caring, gratitude, love
    private static readonly int[] Positive = [0, 1, 4, 5, 8, 13, 15, 17, 18, 20, 21, 23];         // admiration ... relief
    private static readonly int[] Negative = [2, 3, 9, 10, 11, 12, 14, 16, 19, 24, 25];           // anger ... sadness

    public int GoTrainSize { get; init; } = 10_000;
    public int OffensiveTrainSize { get; init; } = 8_000;

    public async Task<ProbeTrainingResult> RunAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(cacheDir);
        var go = await SplitAsync("go_emotions", s => ReadGoEmotionsAsync(s, progress, cancellationToken), r => r.Text, GoTrainSize, progress, cancellationToken);
        var off = await SplitAsync("offensive", s => ReadOffensiveAsync(s, progress, cancellationToken), r => r.Offensive.Text, OffensiveTrainSize, progress, cancellationToken);
        return FitAll(Jobs(go, off), progress, cancellationToken);
    }

    private static Job[] Jobs(Split<GoEmotionRow> go, Split<OffensiveRow> off) =>
    [
        new("hostility", "cardiffnlp/tweet_eval offensive", off.TrainX, [.. off.Train.Select(r => r.IsOffensive)], off.TestX, [.. off.Test.Select(r => r.IsOffensive)]),
        new("warmth", "go_emotions: admiration, caring, gratitude, love", go.TrainX, Has(go.Train, Warm), go.TestX, Has(go.Test, Warm)),
        new("positive", "go_emotions positive group", go.TrainX, Has(go.Train, Positive), go.TestX, Has(go.Test, Positive)),
        new("negative", "go_emotions negative group", go.TrainX, Has(go.Train, Negative), go.TestX, Has(go.Test, Negative)),
    ];

    private ProbeTrainingResult FitAll(Job[] jobs, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var probes = new Dictionary<string, LinearProbe>();
        foreach (var job in jobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Fit(job, progress) is { } probe)
            {
                probes[job.Name] = probe;
            }
        }

        var rejected = jobs.Select(j => j.Name).Where(n => !probes.ContainsKey(n)).ToList();
        return new ProbeTrainingResult(new ProbeFile(embedding.EmbeddingModel, embedding.EmbeddingPrefix, DateTime.UtcNow, probes), rejected);
    }

    /// <summary>One probe's training and held-out data.</summary>
    private sealed record Job(string Name, string Source, IReadOnlyList<float[]> TrainX, List<bool> TrainY, IReadOnlyList<float[]> TestX, List<bool> TestY);

    /// <summary>A dataset's sampled train rows and its test rows, with their embeddings.</summary>
    private sealed record Split<T>(List<T> Train, IReadOnlyList<float[]> TrainX, List<T> Test, IReadOnlyList<float[]> TestX);

    private async Task<Split<T>> SplitAsync<T>(
        string name, Func<string, Task<List<T>>> read, Func<T, string> text, int trainSize, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var train = Sample(await read("train"), trainSize);
        var test = await read("test");
        var trainX = await EmbedCachedAsync($"{name}_train", [.. train.Select(text)], progress, cancellationToken);
        var testX = await EmbedCachedAsync($"{name}_test", [.. test.Select(text)], progress, cancellationToken);
        return new Split<T>(train, trainX, test, testX);
    }

    /// <summary>The trained probe if it clears the gate on the held-out split, else null.</summary>
    private static LinearProbe? Fit(Job job, IProgress<string>? progress)
    {
        var (weights, bias) = LinearProbe.Train(job.TrainX, job.TrainY);
        var candidate = new LinearProbe(weights, bias, 0, job.Source);
        var auc = LinearProbe.RocAuc([.. job.TestX.Select(x => candidate.Predict(x))], job.TestY);
        var ships = auc > AucGate;
        progress?.Report($"{job.Name}: AUC {auc:F3} on {job.TestY.Count} held-out, {(ships ? "ships" : "rejected")}");
        return ships ? candidate with { Auc = Math.Round(auc, 4) } : null;
    }

    private static List<bool> Has(IEnumerable<GoEmotionRow> rows, int[] labels) => [.. rows.Select(r => r.Labels.Any(l => labels.Contains((int)l)))];

    private static List<T> Sample<T>(List<T> rows, int count)
    {
        var copy = rows.ToArray();
        new Random(Seed).Shuffle(copy);
        return [.. copy.Take(count)];
    }

    private async Task<List<GoEmotionRow>> ReadGoEmotionsAsync(string split, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        await using var file = File.OpenRead(await DownloadAsync("google-research-datasets/go_emotions", "simplified", split, progress, cancellationToken));
        await using var reader = await ParquetReader.CreateAsync(file, cancellationToken: cancellationToken);
        var textField = reader.Schema.DataFields.First(f => f.Name == "text");
        var labelField = reader.Schema.DataFields.First(f => f.Path.ToString().StartsWith("labels", StringComparison.Ordinal));
        var rows = new List<GoEmotionRow>();
        for (var g = 0; g < reader.RowGroupCount; g++)
        {
            using var group = reader.OpenRowGroupReader(g);
            var texts = new string?[group.RowCount];
            await group.ReadAsync(textField, texts.AsMemory());
            var count = (int)group.GetMetadata(labelField)!.MetaData!.NumValues;
            // ponytail: buffers one slot larger than numValues, which this Parquet.Net version insists on.
            var values = new long[count + 1];
            var definition = new int[count + 1];
            var repetition = new int[count + 1];
            await group.ReadRawAsync<long>(labelField, values.AsMemory(), definition.AsMemory(), repetition.AsMemory(), cancellationToken);

            var first = rows.Count;
            rows.AddRange(texts.Select(t => new GoEmotionRow(t ?? "")));
            AddLabels(rows, first, count, values, definition, repetition, labelField.MaxDefinitionLevel);
        }

        return rows;
    }

    /// <summary>A repetition level of 0 starts the next row; a full definition level is a label, anything less an empty list.</summary>
    private static void AddLabels(List<GoEmotionRow> rows, int first, int count, long[] values, int[] definition, int[] repetition, int maxDefinition)
    {
        var row = first - 1;
        var next = 0;
        for (var i = 0; i < count; i++)
        {
            row += repetition[i] == 0 ? 1 : 0;
            if (definition[i] == maxDefinition)
            {
                rows[row].Labels.Add(values[next++]);
            }
        }
    }

    private async Task<List<OffensiveRow>> ReadOffensiveAsync(string split, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var path = await DownloadAsync("cardiffnlp/tweet_eval", "offensive", split, progress, cancellationToken);
        return [.. (await ParquetSerializer.DeserializeAsync<TweetEvalRow>(path)).Data.Select(r => new OffensiveRow(r, r.Label == 1))];
    }

    private async Task<string> DownloadAsync(string dataset, string subset, string split, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var path = Path.Combine(cacheDir, $"{dataset.Replace('/', '_')}_{subset}_{split}.parquet");
        if (!File.Exists(path))
        {
            var url = $"https://huggingface.co/api/datasets/{dataset}/parquet/{subset}/{split}/0.parquet";
            progress?.Report($"Downloading {dataset} ({subset}, {split})");
            await File.WriteAllBytesAsync(path, await http.GetByteArrayAsync(url, cancellationToken), cancellationToken);
        }

        return path;
    }

    /// <summary>Embeddings are cached per model and prefix: another model's vectors would train nonsense.</summary>
    private async Task<IReadOnlyList<float[]>> EmbedCachedAsync(string name, List<string> texts, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var dir = Path.Combine(cacheDir, ProbeFile.Slug(embedding));
        var path = Path.Combine(dir, $"{name}_{texts.Count}.emb");
        if (File.Exists(path))
        {
            return ReadEmbeddings(path);
        }

        var vectors = new List<float[]>(texts.Count);
        for (var i = 0; i < texts.Count; i += Batch)
        {
            vectors.AddRange(await llm.EmbedAsync(texts.GetRange(i, Math.Min(Batch, texts.Count - i)), cancellationToken));
            progress?.Report($"Embedding {name}: {vectors.Count:N0} of {texts.Count:N0}");
        }

        Directory.CreateDirectory(dir);
        WriteEmbeddings(path, vectors);
        return vectors;
    }

    private static void WriteEmbeddings(string path, List<float[]> vectors)
    {
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(vectors.Count);
        writer.Write(vectors.Count > 0 ? vectors[0].Length : 0);
        foreach (var f in vectors.SelectMany(v => v))
        {
            writer.Write(f);
        }
    }

    private static List<float[]> ReadEmbeddings(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        var count = reader.ReadInt32();
        var dim = reader.ReadInt32();
        var vectors = new List<float[]>(count);
        for (var i = 0; i < count; i++)
        {
            var v = new float[dim];
            for (var j = 0; j < dim; j++)
            {
                v[j] = reader.ReadSingle();
            }

            vectors.Add(v);
        }

        return vectors;
    }

    private sealed record GoEmotionRow(string Text)
    {
        public List<long> Labels { get; } = [];
    }

    private sealed record OffensiveRow(TweetEvalRow Offensive, bool IsOffensive);

    private sealed class TweetEvalRow
    {
        [JsonPropertyName("text")]
        public string Text { get; set; } = "";

        [JsonPropertyName("label")]
        public long? Label { get; set; }
    }
}
