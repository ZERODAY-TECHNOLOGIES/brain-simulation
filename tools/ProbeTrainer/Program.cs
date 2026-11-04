// Trains the appraisal probes (D-05) on embeddings from LM Studio and writes probes.json.
// A probe ships only if it clears AUC > 0.85 on the held-out split (spec: "Evaluarea").
//
// Usage, from the repo root, with LM Studio running and the embedding model loaded:
//   dotnet run --project tools/ProbeTrainer
//
// Downloads and embedding caches go to data/probes (ignored by git).

using System.Text.Json;
using System.Text.Json.Serialization;
using BrainSim.Engine.Appraisal;
using BrainSim.Engine.Config;
using BrainSim.Engine.Llm;
using Parquet.Serialization;

const double AucGate = 0.85;
const int Seed = 1;
var dataDir = Path.GetFullPath(Path.Combine("data", "probes"));
var outputPath = Path.GetFullPath(Path.Combine("src", "BrainSim.Engine", "probes.json"));
Directory.CreateDirectory(dataDir);

var config = EngineConfig.LoadDefault();
var llm = LmStudioClient.Create(config.Llm);
using var http = new HttpClient();

// GoEmotions label ids (simplified config). Groups follow the GoEmotions paper's sentiment mapping.
int[] warm = [0, 5, 15, 18];                                            // admiration, caring, gratitude, love
int[] positive = [0, 1, 4, 5, 8, 13, 15, 17, 18, 20, 21, 23];            // admiration ... relief
int[] negative = [2, 3, 9, 10, 11, 12, 14, 16, 19, 24, 25];              // anger ... sadness

var goTrain = Sample(await ReadGoEmotionsAsync("train"), 10_000);
var goTest = await ReadGoEmotionsAsync("test");
var offTrain = Sample(await ReadOffensiveAsync("train"), 8_000);
var offTest = await ReadOffensiveAsync("test");

var goTrainX = await EmbedCachedAsync("go_emotions_train", goTrain.Select(r => r.Text).ToList());
var goTestX = await EmbedCachedAsync("go_emotions_test", goTest.Select(r => r.Text).ToList());
var offTrainX = await EmbedCachedAsync("offensive_train", offTrain.Select(r => r.Text).ToList());
var offTestX = await EmbedCachedAsync("offensive_test", offTest.Select(r => r.Text).ToList());

var probes = new Dictionary<string, LinearProbe>();
var failed = false;
Fit("hostility", "cardiffnlp/tweet_eval offensive", offTrainX, offTrain.Select(r => r.Offensive).ToList(), offTestX, offTest.Select(r => r.Offensive).ToList());
Fit("warmth", "go_emotions: admiration, caring, gratitude, love", goTrainX, Has(goTrain, warm), goTestX, Has(goTest, warm));
Fit("positive", "go_emotions positive group", goTrainX, Has(goTrain, positive), goTestX, Has(goTest, positive));
Fit("negative", "go_emotions negative group", goTrainX, Has(goTrain, negative), goTestX, Has(goTest, negative));

var file = new ProbeFile(config.Llm.EmbeddingModel, config.Llm.EmbeddingPrefix, DateTime.UtcNow, probes);
await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(file, ProbeFile.Json));
Console.WriteLine($"Wrote {probes.Count} probes to {outputPath}");
return failed ? 1 : 0;

void Fit(string name, string source, IReadOnlyList<float[]> trainX, IReadOnlyList<bool> trainY, IReadOnlyList<float[]> testX, IReadOnlyList<bool> testY)
{
    var (weights, bias) = LinearProbe.Train(trainX, trainY);
    var candidate = new LinearProbe(weights, bias, 0, source);
    var auc = LinearProbe.RocAuc([.. testX.Select(x => candidate.Predict(x))], testY);
    var passed = auc > AucGate;
    Console.WriteLine($"{name,-10} AUC {auc:F3} on {testY.Count} held-out ({testY.Count(y => y)} positive)  {(passed ? "ships" : "REJECTED")}");
    if (passed)
    {
        probes[name] = candidate with { Auc = Math.Round(auc, 4) };
    }

    failed |= !passed;
}

static List<bool> Has(IEnumerable<GoEmotionRow> rows, int[] labels) => [.. rows.Select(r => r.Labels.Any(l => labels.Contains((int)l)))];

static List<T> Sample<T>(List<T> rows, int count)
{
    var copy = rows.ToArray();
    new Random(Seed).Shuffle(copy);
    return [.. copy.Take(count)];
}

async Task<List<GoEmotionRow>> ReadGoEmotionsAsync(string split) =>
    [.. (await ParquetSerializer.DeserializeAsync<GoEmotionRow>(await DownloadAsync("google-research-datasets/go_emotions", "simplified", split))).Data];

async Task<List<OffensiveRow>> ReadOffensiveAsync(string split) =>
    [.. (await ParquetSerializer.DeserializeAsync<TweetEvalRow>(await DownloadAsync("cardiffnlp/tweet_eval", "offensive", split))).Data
        .Select(r => new OffensiveRow(r.Text, r.Label == 1))];

async Task<string> DownloadAsync(string dataset, string subset, string split)
{
    var path = Path.Combine(dataDir, $"{dataset.Replace('/', '_')}_{subset}_{split}.parquet");
    if (!File.Exists(path))
    {
        var url = $"https://huggingface.co/api/datasets/{dataset}/parquet/{subset}/{split}/0.parquet";
        Console.WriteLine($"Downloading {url}");
        await File.WriteAllBytesAsync(path, await http.GetByteArrayAsync(url));
    }

    return path;
}

async Task<List<float[]>> EmbedCachedAsync(string name, List<string> texts)
{
    var path = Path.Combine(dataDir, $"{name}_{texts.Count}.emb");
    if (File.Exists(path))
    {
        return ReadEmbeddings(path);
    }

    var vectors = new List<float[]>(texts.Count);
    for (var i = 0; i < texts.Count; i += 256)
    {
        vectors.AddRange(await llm.EmbedAsync(texts.GetRange(i, Math.Min(256, texts.Count - i))));
        Console.Write($"\rEmbedding {name}: {vectors.Count}/{texts.Count}");
    }

    Console.WriteLine();
    await using var stream = File.Create(path);
    using var writer = new BinaryWriter(stream);
    writer.Write(vectors.Count);
    writer.Write(vectors[0].Length);
    foreach (var v in vectors)
    {
        foreach (var f in v)
        {
            writer.Write(f);
        }
    }

    return vectors;
}

static List<float[]> ReadEmbeddings(string path)
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

internal sealed class GoEmotionRow
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("labels")]
    public List<long> Labels { get; set; } = [];
}

internal sealed class TweetEvalRow
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("label")]
    public long? Label { get; set; }
}

internal sealed record OffensiveRow(string Text, bool Offensive);
