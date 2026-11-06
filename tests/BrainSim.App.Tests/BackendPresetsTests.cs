using BrainSim.App.Settings;

namespace BrainSim.App.Tests;

public class BackendPresetsTests
{
    [Fact]
    public void Every_preset_address_is_an_http_url_ending_in_v1()
    {
        var urls = BackendPresets.Chat.Select(p => p.BaseUrl).Concat(BackendPresets.Embedding.Select(p => p.BaseUrl)).ToList();

        Assert.NotEmpty(urls);

        Assert.All(urls, url =>
        {
            Assert.True(Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme.StartsWith("http", StringComparison.Ordinal), url);
            Assert.EndsWith("/v1/", url);
        });
    }

    [Fact]
    public void The_usual_vendors_are_offered_by_name()
    {
        Assert.Equal(["LM Studio", "Ollama", "OpenAI", "OpenRouter"], BackendPresets.Chat.Select(p => p.Name));
        Assert.Equal(["LM Studio", "Ollama", "OpenAI"], BackendPresets.Embedding.Select(p => p.Name));
        Assert.Contains(":11434/", BackendPresets.Chat.Single(p => p.Name == "Ollama").BaseUrl);
    }

    [Fact]
    public void An_embedding_preset_on_the_chat_server_leaves_the_embedding_server_empty()
    {
        var ollama = BackendPresets.Embedding.Single(p => p.Name == "Ollama");

        var (url, model, prefix) = BackendPresets.Fill(ollama, chatUrl: "http://localhost:11434/v1/");

        Assert.Equal(("", "nomic-embed-text", "classification: "), (url, model, prefix));
    }

    [Fact]
    public void An_embedding_preset_on_another_server_names_that_server()
    {
        var openAi = BackendPresets.Embedding.Single(p => p.Name == "OpenAI");

        var (url, model, prefix) = BackendPresets.Fill(openAi, chatUrl: "http://localhost:1234/v1/");

        Assert.Equal(("https://api.openai.com/v1/", "text-embedding-3-small", ""), (url, model, prefix));
    }

    [Fact]
    public void The_same_server_is_recognised_without_the_trailing_slash_or_case()
    {
        var lmStudio = BackendPresets.Embedding.Single(p => p.Name == "LM Studio");

        Assert.Equal("", BackendPresets.Fill(lmStudio, chatUrl: "HTTP://localhost:1234/v1").Url);
    }
}
