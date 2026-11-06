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
/// An OpenAI-compatible API (LM Studio by default, see D-35): streamed chat, raw completions and embeddings.
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
    public IAsyncEnumerable<string> StreamChatAsync(
        IReadOnlyList<ChatMessage> messages,
        SamplingParameters sampling,
        CancellationToken cancellationToken = default)
    {
        var body = new ChatRequest(
            config.ChatModel,
            [.. messages.Select(m => new ChatRequestMessage(m.Role, m.Content))],
            sampling.Temperature,
            sampling.TopP,
            sampling.MaxTokens,
            Stream: true);
        return StreamAsync<ChatChunk>("chat/completions", body, c => c?.Choices is [{ Delta.Content: { } text }, ..] ? text : null, cancellationToken);
    }

    /// <summary>
    /// Raw continuation of <paramref name="prompt"/>, with no chat template and no instructions. Used for dreams.
    /// LM Studio streams one token per chunk here too, but returns no logprobs on this endpoint.
    /// </summary>
    public IAsyncEnumerable<string> StreamCompletionAsync(
        string prompt,
        SamplingParameters sampling,
        IReadOnlyList<string>? stop = null,
        CancellationToken cancellationToken = default)
    {
        var body = new CompletionRequest(config.ChatModel, prompt, sampling.Temperature, sampling.TopP, sampling.MaxTokens, Stream: true, stop?.ToArray());
        return StreamAsync<CompletionChunk>("completions", body, c => c?.Choices is [{ Text: { } text }, ..] ? text : null, cancellationToken);
    }

    /// <summary>Posts <paramref name="body"/> and yields the non-empty text of each SSE chunk until [DONE].</summary>
    private async IAsyncEnumerable<string> StreamAsync<TChunk>(
        string path,
        object body,
        Func<TChunk?, string?> textOf,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Post, path, embeddings: false, body);
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

            ThrowIfError(data);
            if (textOf(JsonSerializer.Deserialize<TChunk>(data, Json)) is { Length: > 0 } text)
            {
                yield return text;
            }
        }
    }

    /// <summary>Embeds each text with the configured task prefix. One vector per input, in order.</summary>
    public async Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
    {
        var body = new EmbeddingRequest(config.EmbeddingModel, [.. texts.Select(t => config.EmbeddingPrefix + t)]);
        using var request = Request(HttpMethod.Post, "embeddings", embeddings: true, body);
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(Json, cancellationToken)
            ?? throw new InvalidDataException("LM Studio returned an empty embedding response.");
        return [.. result.Data.OrderBy(d => d.Index).Select(d => d.Embedding)];
    }

    /// <summary>The model ids the chat server (or, with <paramref name="embeddings"/>, the embedding server) offers.</summary>
    public async Task<IReadOnlyList<string>> ModelsAsync(bool embeddings, CancellationToken cancellationToken = default)
    {
        using var request = Request(HttpMethod.Get, "models", embeddings, body: null);
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ModelList>(Json, cancellationToken);
        return [.. result?.Data.Select(m => m.Id) ?? []];
    }

    /// <summary>
    /// Embeddings may live on their own server, with their own key; everything else goes to the chat server (the
    /// client's base address) with the chat key. The chat key never leaves for another host (D-39).
    /// </summary>
    private HttpRequestMessage Request(HttpMethod method, string path, bool embeddings, object? body)
    {
        var separate = embeddings && config.EmbeddingBaseUrl.Length > 0;
        var request = new HttpRequestMessage(method, separate ? new Uri(new Uri(config.EmbeddingBaseUrl), path) : new Uri(path, UriKind.Relative));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType(), options: Json);
        }

        if ((separate ? config.EmbeddingApiKey : config.ApiKey) is { Length: > 0 } key)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        }

        return request;
    }

    /// <summary>
    /// LM Studio reports a generation that failed after the headers (a chat template error, a crashed model)
    /// as an SSE error event with HTTP 200. Without this it reads as an empty reply.
    /// </summary>
    private static void ThrowIfError(string data)
    {
        if (!data.Contains("\"error\"", StringComparison.Ordinal))
        {
            return;
        }

        using var document = JsonDocument.Parse(data);
        if (document.RootElement.TryGetProperty("error", out var error))
        {
            var message = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var m) ? m.GetString() : error.ToString();
            throw new HttpRequestException($"LM Studio failed to generate: {message}");
        }
    }

    private sealed record ChatRequest(
        string Model, ChatRequestMessage[] Messages, double Temperature, double TopP, int MaxTokens, bool Stream);

    private sealed record ChatRequestMessage(string Role, string Content);

    private sealed record ChatChunk(ChatChoice[]? Choices);

    private sealed record ChatChoice(ChatDelta? Delta);

    private sealed record ChatDelta(string? Content);

    private sealed record CompletionRequest(string Model, string Prompt, double Temperature, double TopP, int MaxTokens, bool Stream, string[]? Stop);

    private sealed record CompletionChunk(CompletionChoice[]? Choices);

    private sealed record CompletionChoice(string? Text);

    private sealed record EmbeddingRequest(string Model, string[] Input);

    private sealed record EmbeddingResponse(EmbeddingData[] Data);

    private sealed record EmbeddingData(int Index, float[] Embedding);

    private sealed record ModelList(ModelEntry[] Data);

    private sealed record ModelEntry(string Id);
}
