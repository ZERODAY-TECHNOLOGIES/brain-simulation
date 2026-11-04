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

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestPath = request.RequestUri!.AbsolutePath;
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responseBody, Encoding.UTF8, "text/event-stream") };
        }
    }

    private static LmStudioClient Client(FakeHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri(Config.BaseUrl) }, Config);

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
}
