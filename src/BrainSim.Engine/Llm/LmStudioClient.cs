using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using BrainSim.Engine.Config;

namespace BrainSim.Engine.Llm;

public readonly record struct ChatMessage(string Role, string Content)
{
    public static ChatMessage System(string content) => new("system", content);
    public static ChatMessage User(string content) => new("user", content);
    public static ChatMessage Assistant(string content) => new("assistant", content);
}

public readonly record struct SamplingParameters(double Temperature, double TopP, int MaxTokens);

/// <summary>
/// LM Studio's OpenAI-compatible API: streamed chat and embeddings.
/// LM Studio sends one SSE chunk per token, so each chunk is one tick.
/// </summary>
public sealed class LmStudioClient(HttpClient http, LlmConfig config)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static LmStudioClient Create(LlmConfig config) =>
        new(new HttpClient { BaseAddress = new Uri(config.BaseUrl), Timeout = TimeSpan.FromMinutes(5) }, config);

    /// <summary>Streams the reply one token at a time.</summary>
    public async IAsyncEnumerable<string> StreamChatAsync(
        IReadOnlyList<ChatMessage> messages,
        SamplingParameters sampling,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var body = new ChatRequest(
            config.ChatModel,
            [.. messages.Select(m => new ChatRequestMessage(m.Role, m.Content))],
            sampling.Temperature,
            sampling.TopP,
            sampling.MaxTokens,
            Stream: true);

        using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions") { Content = JsonContent.Create(body, options: Json) };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellationToken));

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal))
            {
                continue;
            }

            var data = line["data: ".Length..];
            if (data == "[DONE]")
            {
                yield break;
            }

            var chunk = JsonSerializer.Deserialize<ChatChunk>(data, Json);
            var content = chunk?.Choices is [{ Delta.Content: { Length: > 0 } text }, ..] ? text : null;
            if (content is not null)
            {
                yield return content;
            }
        }
    }

    /// <summary>
    /// Raw continuation of <paramref name="prompt"/>, with no chat template and no instructions. Used for dreams.
    /// LM Studio streams one token per chunk here too, but returns no logprobs on this endpoint.
    /// </summary>
    public async IAsyncEnumerable<string> StreamCompletionAsync(
        string prompt,
        SamplingParameters sampling,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var body = new CompletionRequest(config.ChatModel, prompt, sampling.Temperature, sampling.TopP, sampling.MaxTokens, Stream: true);
        using var request = new HttpRequestMessage(HttpMethod.Post, "completions") { Content = JsonContent.Create(body, options: Json) };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellationToken));

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal))
            {
                continue;
            }

            var data = line["data: ".Length..];
            if (data == "[DONE]")
            {
                yield break;
            }

            var chunk = JsonSerializer.Deserialize<CompletionChunk>(data, Json);
            if (chunk?.Choices is [{ Text: { Length: > 0 } text }, ..])
            {
                yield return text;
            }
        }
    }

    /// <summary>Embeds each text with the configured task prefix. One vector per input, in order.</summary>
    public async Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
    {
        var body = new EmbeddingRequest(config.EmbeddingModel, [.. texts.Select(t => config.EmbeddingPrefix + t)]);
        using var response = await http.PostAsJsonAsync("embeddings", body, Json, cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(Json, cancellationToken)
            ?? throw new InvalidDataException("LM Studio returned an empty embedding response.");
        return [.. result.Data.OrderBy(d => d.Index).Select(d => d.Embedding)];
    }

    private sealed record ChatRequest(
        string Model, ChatRequestMessage[] Messages, double Temperature, double TopP, int MaxTokens, bool Stream);

    private sealed record ChatRequestMessage(string Role, string Content);

    private sealed record ChatChunk(ChatChoice[]? Choices);

    private sealed record ChatChoice(ChatDelta? Delta);

    private sealed record ChatDelta(string? Content);

    private sealed record CompletionRequest(string Model, string Prompt, double Temperature, double TopP, int MaxTokens, bool Stream);

    private sealed record CompletionChunk(CompletionChoice[]? Choices);

    private sealed record CompletionChoice(string? Text);

    private sealed record EmbeddingRequest(string Model, string[] Input);

    private sealed record EmbeddingResponse(EmbeddingData[] Data);

    private sealed record EmbeddingData(int Index, float[] Embedding);
}
