using System.Net;
using System.Text;
using System.Text.Json;
using BrainSim.Engine.Config;
using BrainSim.Engine.Llm;

namespace BrainSim.Engine.Tests;

public class LmStudioClientTests
{
    private static readonly LlmConfig Config = EngineConfig.LoadDefault().Llm;

    private sealed class FakeHandler(string responseBody) : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }
        public string? RequestPath { get; private set; }
        public Uri? RequestUri { get; private set; }
        public string? Authorization { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            RequestPath = request.RequestUri!.AbsolutePath;
            Authorization = request.Headers.Authorization?.ToString();
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responseBody, Encoding.UTF8, "text/event-stream") };
        }
    }

    private static LmStudioClient Client(FakeHandler handler, LlmConfig? config = null) =>
        new(new HttpClient(handler) { BaseAddress = new Uri((config ?? Config).BaseUrl) }, config ?? Config);

    private const string Sse = "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\ndata: [DONE]\n\n";
    private const string Vectors = """{"data":[{"index":0,"embedding":[1,0]}]}""";

    [Fact]
    public async Task Stream_yields_one_token_per_chunk_and_sends_the_sampling_parameters()
    {
        const string sse = """
            data: {"choices":[{"index":0,"delta":{"role":"assistant","content":"The"},"finish_reason":null}]}

            data: {"choices":[{"index":0,"delta":{"content":" ocean"},"finish_reason":null}]}

            data: {"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

            data: [DONE]

            """;
        var handler = new FakeHandler(sse);
        var tokens = new List<string>();

        await foreach (var token in Client(handler).StreamChatAsync([ChatMessage.User("hi")], new SamplingParameters(0.7, 0.9, 128), TestContext.Current.CancellationToken))
        {
            tokens.Add(token);
        }

        Assert.Equal(["The", " ocean"], tokens);
        Assert.EndsWith("/v1/chat/completions", handler.RequestPath);
        using var body = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal(0.7, body.RootElement.GetProperty("temperature").GetDouble());
        Assert.Equal(0.9, body.RootElement.GetProperty("top_p").GetDouble());
        Assert.Equal(128, body.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal(Config.ChatModel, body.RootElement.GetProperty("model").GetString());
    }

    /// <summary>LM Studio reports a failed generation (a chat template error, say) as an SSE error event with HTTP 200.</summary>
    [Fact]
    public async Task A_streamed_error_is_raised_not_read_as_an_empty_reply()
    {
        const string sse = """
            event: error
            data: {"error":{"message":"Error: Jinja Exception: System message must be at the beginning."}}

            """;

        var e = await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var _ in Client(new FakeHandler(sse)).StreamChatAsync([ChatMessage.User("hi")], new SamplingParameters(0.7, 0.9, 128), TestContext.Current.CancellationToken))
            {
            }
        });
        Assert.Contains("System message must be at the beginning", e.Message);

        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var _ in Client(new FakeHandler(sse)).StreamCompletionAsync("a dream", new SamplingParameters(1, 0.9, 64), [], TestContext.Current.CancellationToken))
            {
            }
        });
    }

    [Fact]
    public async Task Embeddings_come_back_in_input_order_with_the_task_prefix_applied()
    {
        var handler = new FakeHandler("""{"data":[{"index":1,"embedding":[0.5,0.5]},{"index":0,"embedding":[1,0]}]}""");

        var vectors = await Client(handler).EmbedAsync(["first", "second"], TestContext.Current.CancellationToken);

        Assert.Equal([1f, 0f], vectors[0]);
        Assert.Equal([0.5f, 0.5f], vectors[1]);
        using var body = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal(Config.EmbeddingPrefix + "first", body.RootElement.GetProperty("input")[0].GetString());
    }

    [Fact]
    public async Task An_empty_embedding_response_is_an_error_that_says_so()
    {
        var e = await Assert.ThrowsAsync<InvalidDataException>(() =>
            Client(new FakeHandler("null")).EmbedAsync(["first"], TestContext.Current.CancellationToken));

        Assert.Equal("LM Studio returned an empty embedding response.", e.Message);
    }

    /// <summary>The agent treats a connection error as LM Studio being away and carries on; a client without the URL would crash it.</summary>
    [Fact]
    public async Task A_created_client_calls_the_configured_url()
    {
        var client = LmStudioClient.Create(Config with { BaseUrl = "http://127.0.0.1:9/v1/" }); // nothing listens on the discard port

        await Assert.ThrowsAsync<HttpRequestException>(() => client.EmbedAsync(["first"], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_api_key_is_sent_as_a_bearer_token_on_every_call()
    {
        var keyed = Config with { ApiKey = "sk-123" };
        var chat = new FakeHandler(Sse);
        var embed = new FakeHandler(Vectors);
        var ct = TestContext.Current.CancellationToken;

        await foreach (var _ in Client(chat, keyed).StreamChatAsync([new ChatMessage("user", "hi")], new SamplingParameters(0.7, 0.9, 10), ct)) { }
        await Client(embed, keyed).EmbedAsync(["x"], ct);

        Assert.Equal(("Bearer sk-123", "Bearer sk-123"), (chat.Authorization, embed.Authorization));
    }

    [Fact]
    public async Task Without_a_key_no_authorization_header_is_sent()
    {
        var handler = new FakeHandler(Vectors);

        await Client(handler).EmbedAsync(["x"], TestContext.Current.CancellationToken);

        Assert.Null(handler.Authorization);
    }

    [Fact]
    public async Task Embeddings_go_to_their_own_server_when_one_is_set()
    {
        var handler = new FakeHandler(Vectors);

        await Client(handler, Config with { EmbeddingBaseUrl = "http://embedder:1235/v1/" }).EmbedAsync(["x"], TestContext.Current.CancellationToken);

        Assert.Equal("http://embedder:1235/v1/embeddings", handler.RequestUri!.ToString());
    }

    [Fact]
    public async Task Models_lists_what_a_server_offers_with_the_key()
    {
        var handler = new FakeHandler("""{"data":[{"id":"qwen/qwen3.5-0.8b"},{"id":"nomic"}]}""");

        var models = await Client(handler, Config with { ApiKey = "sk-1" }).ModelsAsync(embeddings: false, TestContext.Current.CancellationToken);

        Assert.Equal(["qwen/qwen3.5-0.8b", "nomic"], models);
        Assert.Equal(("/v1/models", "Bearer sk-1"), (handler.RequestPath, handler.Authorization));
    }

    [Fact]
    public async Task Embedding_models_are_listed_by_the_embedding_server()
    {
        var handler = new FakeHandler("""{"data":[]}""");

        await Client(handler, Config with { EmbeddingBaseUrl = "http://embedder:1235/v1/" }).ModelsAsync(embeddings: true, TestContext.Current.CancellationToken);

        Assert.Equal("http://embedder:1235/v1/models", handler.RequestUri!.ToString());
    }
}
