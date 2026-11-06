using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using BrainSim.App.Settings;
using BrainSim.Engine.Config;
using BrainSim.Engine.Llm;

namespace BrainSim.App;

/// <summary>Chooses the LLM backend (D-35). Saves settings.json in the data directory; the app reads it at startup.</summary>
public partial class SettingsWindow : Window
{
    public static string SettingsPath => Path.Combine(App.DataDirectory, "settings.json");

    private readonly LlmConfig _current;

    public SettingsWindow(LlmConfig current)
    {
        InitializeComponent();
        ChatPresetBox.ItemsSource = BackendPresets.Chat;
        EmbeddingPresetBox.ItemsSource = BackendPresets.Embedding;
        _current = current;
        Show(LlmSettings.From(current));
    }

    private void Show(LlmSettings s)
    {
        BaseUrl.Text = s.BaseUrl;
        ApiKey.Password = s.ApiKey;
        ChatModel.Text = s.ChatModel;
        EmbeddingUrl.Text = s.EmbeddingBaseUrl;
        EmbeddingModel.Text = s.EmbeddingModel;
        EmbeddingPrefix.Text = s.EmbeddingPrefix;
        EmbeddingApiKey.Password = s.EmbeddingApiKey;
    }

    private LlmSettings Entered() =>
        new(BaseUrl.Text, EmbeddingUrl.Text, ChatModel.Text, EmbeddingModel.Text, EmbeddingPrefix.Text, ApiKey.Password, EmbeddingApiKey.Password);

    private void OnChatPreset(object sender, SelectionChangedEventArgs e)
    {
        if (ChatPresetBox.SelectedItem is ChatPreset preset)
        {
            BaseUrl.Text = preset.BaseUrl;
            Status.Text = $"{preset.Name}: address filled in. Enter the chat model, and the API key for a hosted vendor.";
        }
    }

    private void OnEmbeddingPreset(object sender, SelectionChangedEventArgs e)
    {
        if (EmbeddingPresetBox.SelectedItem is EmbeddingPreset preset)
        {
            (EmbeddingUrl.Text, EmbeddingModel.Text, EmbeddingPrefix.Text) = BackendPresets.Fill(preset, BaseUrl.Text);
            Status.Text = $"{preset.Name}: embedding server, model and prefix filled in.";
        }
    }

    private void OnDefaults(object sender, RoutedEventArgs e)
    {
        Show(LlmSettings.From(EngineConfig.LoadDefault().Llm));
        Status.Text = "Filled in from config.yaml. Save to use them.";
    }

    private async void OnTest(object sender, RoutedEventArgs e)
    {
        var settings = Entered();
        if (settings.Problem() is { } problem)
        {
            Status.Text = problem;
            return;
        }

        TestButton.IsEnabled = false;
        Status.Text = "Testing...";
        var llm = settings.ApplyTo(_current);
        var client = LmStudioClient.Create(llm);
        var lines = new List<string> { await Check(client, embeddings: false, llm.ChatModel, "Chat server") };
        lines.Add(await Check(client, embeddings: true, llm.EmbeddingModel, llm.EmbeddingBaseUrl.Length > 0 ? "Embedding server" : "Embedding model"));
        Status.Text = string.Join("\n", lines);
        TestButton.IsEnabled = true;
    }

    private static async Task<string> Check(LmStudioClient client, bool embeddings, string model, string what)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var models = await client.ModelsAsync(embeddings, timeout.Token);
            return models.Contains(model)
                ? $"{what}: {model} found."
                : $"{what}: reachable, but {model} is not listed. It offers: {(models.Count > 0 ? string.Join(", ", models) : "nothing")}.";
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            return $"{what}: not reachable ({ex.Message})";
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var settings = Entered();
        if (settings.Problem() is { } problem)
        {
            Status.Text = problem;
            return;
        }

        try
        {
            Directory.CreateDirectory(App.DataDirectory);
            LlmSettingsFile.Save(SettingsPath, settings, Dpapi.Protect);
        }
        catch (IOException ex)
        {
            Status.Text = $"Could not save: {ex.Message}";
            return;
        }

        MessageBox.Show(this, "Saved. Restart BrainSim to use the new backend.", "Settings", MessageBoxButton.OK, MessageBoxImage.Information);
        DialogResult = true;
    }
}
