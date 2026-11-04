using System.Net;
using System.Text;
using System.Text.Json;

namespace BrainSim.Engine.Tests;

/// <summary>Fake LM Studio: "thanks" embeds as praise, everything else as neutral; replies with a fixed token count.</summary>
internal sealed class FakeLmStudio(int replyTokens) : HttpMessageHandler
{
    /// <summary>Reply requests only. Fact extraction runs in the background, so its requests go to <see cref="ExtractionRequests"/>.</summary>
    public List<JsonDocument> ChatRequests { get; } = [];
    public List<JsonDocument> ExtractionRequests { get; } = [];
    public List<JsonDocument> CompletionRequests { get; } = [];

    /// <summary>When set, embeddings come from this, one vector per input. Otherwise the "thanks" rule above.</summary>
    public Func<string, float[]>? Embed { get; init; }

    /// <summary>When set, a chat request is answered with this text in one chunk. Otherwise tokens t0, t1, ...</summary>
    public Func<JsonDocument, string>? Reply { get; init; }

    /// <summary>When set, embedding and chat calls throw, like LM Studio going away.</summary>
    public bool Down { get; set; }

    /// <summary>When set, only the embeddings endpoint fails; chat keeps answering.</summary>
    public bool EmbeddingsDown { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        if (Down || (EmbeddingsDown && request.RequestUri!.AbsolutePath.EndsWith("embeddings")))
        {
            throw new HttpRequestException("LM Studio is not running");
        }

        string response;
        if (request.RequestUri!.AbsolutePath.EndsWith("embeddings") && Embed is { } embed)
        {
            var inputs = JsonDocument.Parse(body).RootElement.GetProperty("input").EnumerateArray().Select(e => e.GetString()!).ToList();
            var data = inputs.Select((text, i) => new { index = i, embedding = embed(text) });
            response = JsonSerializer.Serialize(new { data });
        }
        else if (request.RequestUri!.AbsolutePath.EndsWith("embeddings"))
        {
            var input = JsonDocument.Parse(body).RootElement.GetProperty("input")[0].GetString()!;
            var vector = input.Contains("thanks") ? "[0,0,1,0]" : "[0.5,0.5,0.5,0.5]";
            response = $$$"""{"data":[{"index":0,"embedding":{{{vector}}}}]}""";
        }
        else if (!request.RequestUri.AbsolutePath.EndsWith("chat/completions"))
        {
            // Raw completion, used for dreams: tokens d0, d1, ...
            CompletionRequests.Add(JsonDocument.Parse(body));
            var sse = new StringBuilder();
            for (var i = 0; i < replyTokens; i++)
            {
                sse.Append("data: {\"choices\":[{\"text\":\"d" + i + " \"}]}\n\n");
            }

            response = sse.Append("data: [DONE]\n\n").ToString();
        }
        else if (Reply is { } reply)
        {
            var document = JsonDocument.Parse(body);
            Record(document);
            response = "data: " + JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = reply(document) } } } }) + "\n\ndata: [DONE]\n\n";
        }
        else
        {
            Record(JsonDocument.Parse(body));
            var sse = new StringBuilder();
            for (var i = 0; i < replyTokens; i++)
            {
                sse.Append("data: {\"choices\":[{\"delta\":{\"content\":\"t" + i + " \"}}]}\n\n");
            }

            response = sse.Append("data: [DONE]\n\n").ToString();
        }

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response) };
    }

    private void Record(JsonDocument request)
    {
        var extraction = request.RootElement.GetProperty("messages")[0].GetProperty("content").GetString() == Memory.FactExtractor.Instruction;
        var list = extraction ? ExtractionRequests : ChatRequests;
        lock (list)
        {
            list.Add(request);
        }
    }
}
