using System.Net;
using System.Text;
using System.Text.Json;

namespace BrainSim.Engine.Tests;

/// <summary>Fake LM Studio: "thanks" embeds as praise, everything else as neutral; replies with a fixed token count.</summary>
internal sealed class FakeLmStudio(int replyTokens) : HttpMessageHandler
{
    public List<JsonDocument> ChatRequests { get; } = [];
    public List<JsonDocument> CompletionRequests { get; } = [];

    /// <summary>When set, embeddings come from this, one vector per input. Otherwise the "thanks" rule above.</summary>
    public Func<string, float[]>? Embed { get; init; }

    /// <summary>When set, a chat request is answered with this text in one chunk. Otherwise tokens t0, t1, ...</summary>
    public Func<JsonDocument, string>? Reply { get; init; }

    /// <summary>When set, embedding and chat calls throw, like LM Studio going away.</summary>
    public bool Down { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        if (Down)
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
            ChatRequests.Add(document);
            response = "data: " + JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = reply(document) } } } }) + "\n\ndata: [DONE]\n\n";
        }
        else
        {
            ChatRequests.Add(JsonDocument.Parse(body));
            var sse = new StringBuilder();
            for (var i = 0; i < replyTokens; i++)
            {
                sse.Append("data: {\"choices\":[{\"delta\":{\"content\":\"t" + i + " \"}}]}\n\n");
            }

            response = sse.Append("data: [DONE]\n\n").ToString();
        }

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response) };
    }
}
