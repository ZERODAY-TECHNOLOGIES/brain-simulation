using System.Net;
using System.Text.Json.Serialization;
using BrainSim.Engine.Appraisal;
using BrainSim.Engine.Config;
using BrainSim.Engine.Llm;
using Parquet.Serialization;

namespace BrainSim.Engine.Tests;

/// <summary>Retraining the appraisal probes for any embedding model, as Settings does it (D-39).</summary>
public sealed class ProbeTrainingTests : IDisposable
{
    private static readonly LlmConfig Llm = EngineConfig.LoadDefault().Llm;
    private readonly string _cache = Directory.CreateTempSubdirectory("brainsim-probe-cache").FullName;

    public void Dispose() => Directory.Delete(_cache, recursive: true);

    public sealed class GoRow
    {
        [JsonPropertyName("text")] public string Text { get; set; } = "";
        [JsonPropertyName("labels")] public List<long> Labels { get; set; } = [];
    }

    public sealed class OffensiveRow
    {
        [JsonPropertyName("text")] public string Text { get; set; } = "";
        [JsonPropertyName("label")] public long? Label { get; set; }
    }

    /// <summary>Hugging Face's parquet API, serving tiny datasets, and counting what it was asked for.</summary>
    private sealed class FakeHub(bool offensiveIsNoise) : HttpMessageHandler
    {
        public int Downloads { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Downloads++;
            var path = request.RequestUri!.AbsolutePath;
            var stream = new MemoryStream();
            if (path.Contains("go_emotions"))
            {
                // admiration (0) is warm and positive, anger (2) negative, neutral (27) neither.
                await ParquetSerializer.SerializeAsync(
                    Enumerable.Range(0, 90).Select(i => new GoRow { Text = $"{(i % 3) switch { 0 => "kind", 1 => "angry", _ => "plain" }} {i}", Labels = [(i % 3) switch { 0 => 0, 1 => 2, _ => 27 }] }),
                    stream, cancellationToken: cancellationToken);
            }
            else
            {
                await ParquetSerializer.SerializeAsync(
                    Enumerable.Range(0, 60).Select(i => new OffensiveRow { Text = $"{(i % 2 == 0 ? "rude" : "fine")} {i}", Label = offensiveIsNoise ? (i / 2) % 2 : 1 - i % 2 }),
                    stream, cancellationToken: cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(stream.ToArray()) };
        }
    }

    private int _embedded;

    /// <summary>One direction per keyword, so a linear probe can separate them perfectly.</summary>
    private float[] Embed(string text)
    {
        _embedded++;
        string[] words = ["kind", "angry", "plain", "rude", "fine"];
        return [.. words.Select(w => text.Contains(w) ? 1f : 0f)];
    }

    private ProbeTraining Training(FakeHub hub, LlmConfig? llm = null)
    {
        var config = llm ?? Llm;
        var fake = new FakeLmStudio(0) { Embed = Embed };
        var client = new LmStudioClient(new HttpClient(fake) { BaseAddress = new Uri(config.BaseUrl) }, config);
        return new ProbeTraining(new HttpClient(hub), client, config, _cache) { GoTrainSize = 60, OffensiveTrainSize = 40 };
    }

    [Fact]
    public async Task Probes_that_separate_their_data_ship_with_the_embedding_setup_they_were_trained_on()
    {
        var result = await Training(new FakeHub(offensiveIsNoise: false)).RunAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(["hostility", "negative", "positive", "warmth"], result.File.Probes.Keys.Order());
        Assert.All(result.File.Probes.Values, p => Assert.True(p.Auc > ProbeTraining.AucGate, $"{p.Source}: {p.Auc}"));
        Assert.True(result.File.Fits(Llm));
        Assert.Empty(result.Rejected);
    }

    [Fact]
    public async Task A_probe_its_data_cannot_teach_is_rejected_and_left_out()
    {
        var result = await Training(new FakeHub(offensiveIsNoise: true)).RunAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(["hostility"], result.Rejected);
        Assert.DoesNotContain("hostility", result.File.Probes.Keys);
        Assert.Equal(3, result.File.Probes.Count);
    }

    [Fact]
    public async Task A_second_run_reuses_the_downloads_and_the_embeddings_of_the_same_model()
    {
        var ct = TestContext.Current.CancellationToken;
        var hub = new FakeHub(offensiveIsNoise: false);
        await Training(hub).RunAsync(null, ct);
        var (downloads, embedded) = (hub.Downloads, _embedded);

        await Training(hub).RunAsync(null, ct);

        Assert.Equal((downloads, embedded), (hub.Downloads, _embedded));
    }

    [Fact]
    public async Task Another_embedding_model_embeds_again_instead_of_reading_the_other_models_cache()
    {
        var ct = TestContext.Current.CancellationToken;
        var hub = new FakeHub(offensiveIsNoise: false);
        await Training(hub).RunAsync(null, ct);
        var embedded = _embedded;

        var other = await Training(hub, Llm with { EmbeddingModel = "text-embedding-3-small", EmbeddingPrefix = "" }).RunAsync(null, ct);

        Assert.True(_embedded > embedded);
        Assert.Equal(("text-embedding-3-small", ""), (other.File.EmbeddingModel, other.File.EmbeddingPrefix));
    }

    [Fact]
    public async Task Cancelling_stops_the_training()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Training(new FakeHub(false)).RunAsync(null, cancelled.Token));
    }

    [Fact]
    public async Task Progress_says_what_is_happening()
    {
        var lines = new List<string>();

        await Training(new FakeHub(false)).RunAsync(new SyncProgress(lines.Add), TestContext.Current.CancellationToken);

        Assert.Contains(lines, l => l.StartsWith("Downloading", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("Embedding", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("AUC", StringComparison.Ordinal));
    }

    private sealed class SyncProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
