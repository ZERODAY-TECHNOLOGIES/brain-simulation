using System.Security.Cryptography;
using BrainSim.Engine.Config;

namespace BrainSim.Engine.Tests;

public class LlmSettingsTests : IDisposable
{
    private static readonly LlmConfig Defaults = EngineConfig.LoadDefault().Llm;
    private readonly string _dir = Directory.CreateTempSubdirectory("brainsim-settings").FullName;

    private string PathOf => Path.Combine(_dir, "settings.json");

    // A stand-in for DPAPI: reversible, and nothing like the plain text.
    private static string Protect(string s) => "enc:" + new string([.. s.Reverse()]);
    private static string Unprotect(string s) => new([.. s["enc:".Length..].Reverse()]);

    private static readonly LlmSettings Cloud = new(
        "https://api.example.com/v1/", "http://localhost:1235/v1/", "big-model", "nomic", "classification: ", "sk-secret-123");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Saved_settings_load_back_the_same()
    {
        LlmSettingsFile.Save(PathOf, Cloud, Protect);

        Assert.Equal(Cloud, LlmSettingsFile.Load(PathOf, Unprotect));
    }

    [Fact]
    public void The_api_key_is_not_stored_in_clear()
    {
        LlmSettingsFile.Save(PathOf, Cloud, Protect);

        Assert.DoesNotContain("sk-secret-123", File.ReadAllText(PathOf));
    }

    [Fact]
    public void No_file_means_no_settings()
    {
        Assert.Null(LlmSettingsFile.Load(PathOf, Unprotect));
    }

    [Fact]
    public void A_corrupt_file_is_set_aside_and_gives_no_settings()
    {
        File.WriteAllText(PathOf, "{ not json");

        Assert.Null(LlmSettingsFile.Load(PathOf, Unprotect));
        Assert.False(File.Exists(PathOf));
        Assert.True(File.Exists(PathOf + ".bad"));
    }

    /// <summary>DPAPI keys belong to one Windows user: a settings.json copied from another account cannot be read.</summary>
    [Fact]
    public void A_key_that_cannot_be_decrypted_is_dropped_and_the_rest_kept()
    {
        LlmSettingsFile.Save(PathOf, Cloud, Protect);

        var loaded = LlmSettingsFile.Load(PathOf, _ => throw new CryptographicException("wrong user"));

        Assert.Equal(Cloud with { ApiKey = "" }, loaded);
    }

    [Fact]
    public void Settings_replace_the_llm_section_and_keep_the_rest()
    {
        var applied = Cloud.ApplyTo(Defaults);

        Assert.Equal(("https://api.example.com/v1/", "http://localhost:1235/v1/", "big-model", "nomic", "sk-secret-123"),
            (applied.BaseUrl, applied.EmbeddingBaseUrl, applied.ChatModel, applied.EmbeddingModel, applied.ApiKey));
        Assert.Equal(Defaults.SystemPrompt, applied.SystemPrompt);
        Assert.Equal(Defaults.HistoryMessages, applied.HistoryMessages);
    }

    /// <summary>Without the slash, "http://host/v1" + "models" resolves to "http://host/models".</summary>
    [Fact]
    public void Urls_get_a_trailing_slash()
    {
        var applied = (Cloud with { BaseUrl = "http://localhost:11434/v1", EmbeddingBaseUrl = "http://localhost:1235/v1" }).ApplyTo(Defaults);

        Assert.Equal(("http://localhost:11434/v1/", "http://localhost:1235/v1/"), (applied.BaseUrl, applied.EmbeddingBaseUrl));
    }

    [Fact]
    public void An_empty_embedding_url_stays_empty_so_the_chat_server_is_used()
    {
        Assert.Equal("", (Cloud with { EmbeddingBaseUrl = " " }).ApplyTo(Defaults).EmbeddingBaseUrl);
    }

    [Fact]
    public void From_reads_the_current_llm_section()
    {
        Assert.Equal(
            new LlmSettings(Defaults.BaseUrl, Defaults.EmbeddingBaseUrl, Defaults.ChatModel, Defaults.EmbeddingModel, Defaults.EmbeddingPrefix, Defaults.ApiKey),
            LlmSettings.From(Defaults));
    }

    [Theory]
    [InlineData("localhost:1234/v1", "x", "y", "", "The server address must start with http:// or https://.")]
    [InlineData("http://localhost:1234/v1", "x", "y", "ftp://other", "The embedding server address must start with http:// or https://, or be empty.")]
    [InlineData("http://localhost:1234/v1", " ", "y", "", "The chat model is required.")]
    [InlineData("http://localhost:1234/v1", "x", "", "", "The embedding model is required.")]
    [InlineData("https://api.example.com/v1/", "x", "y", "http://localhost:1235/v1", null)]
    public void Problem_explains_what_is_wrong(string url, string chat, string embedding, string embeddingUrl, string? expected)
    {
        Assert.Equal(expected, new LlmSettings(url, embeddingUrl, chat, embedding, "", "").Problem());
    }
}
