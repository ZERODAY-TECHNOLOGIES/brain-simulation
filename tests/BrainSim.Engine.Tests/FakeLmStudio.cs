using System.Net;
using System.Text;
using System.Text.Json;

namespace BrainSim.Engine.Tests;

/// <summary>Fake LM Studio: "thanks" embeds as praise, everything else as neutral; replies with a fixed token count.</summary>
internal sealed class FakeLmStudio(int replyTokens) : HttpMessageHandler
{
    public List<JsonDocument> ChatRequests { get; } = [];
    public List<JsonDocument> CompletionRequests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        string response;
        if (request.RequestUri!.AbsolutePath.EndsWith("embeddings"))
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
