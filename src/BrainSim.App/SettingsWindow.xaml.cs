using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using BrainSim.App.Settings;
using BrainSim.Engine.Appraisal;
using BrainSim.Engine.Config;
using BrainSim.Engine.Llm;

namespace BrainSim.App;

/// <summary>Chooses the LLM backend (D-35). Saves settings.json in the data directory; the app reads it at startup.</summary>
public partial class SettingsWindow : Window
{
    public static string SettingsPath => Path.Combine(App.DataDirectory, "settings.json");

    private readonly LlmConfig _current;
    private CancellationTokenSource? _training;

    public SettingsWindow(LlmConfig current)
    {
        InitializeComponent();
        ChatPresetBox.ItemsSource = BackendPresets.Chat;
        EmbeddingPresetBox.ItemsSource = BackendPresets.Embedding;
        _current = current;
        Show(LlmSettings.From(current));
        Closing += (_, _) => _training?.Cancel();
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

    /// <summary>Retrains the probes with the embedding settings as entered, off the UI thread, and saves them for that setup (D-39).</summary>
    private async void OnRetrain(object sender, RoutedEventArgs e)
    {
        if (_training is not null)
        {
            await _training.CancelAsync();
            return;
        }

        var settings = Entered();
        if (settings.Problem() is { } problem)
        {
            Status.Text = problem;
            return;
        }

        var llm = settings.ApplyTo(_current);
        var question = $"This downloads two public datasets (GoEmotions, tweet_eval) and embeds about 24,000 short texts with {llm.EmbeddingModel}. "
            + "With a local model on a laptop it takes about a quarter of an hour; a hosted embedding API bills for it (a few cents at most). Continue?";
        if (MessageBox.Show(this, question, "Retrain probes", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }

        _training = new CancellationTokenSource();
        RetrainButton.Content = "Cancel retraining";
        Status.Text = await RetrainAsync(llm, _training.Token);
        _training.Dispose();
        _training = null;
        RetrainButton.Content = "Retrain probes for this embedding model";
    }

    private async Task<string> RetrainAsync(LlmConfig llm, CancellationToken cancellationToken)
    {
        var progress = new Progress<string>(line => Status.Text = line);
        try
        {
            using var http = new HttpClient();
            var training = new ProbeTraining(http, LmStudioClient.Create(llm), llm, Path.Combine(App.DataDirectory, "probe-data"));
            var result = await Task.Run(() => training.RunAsync(progress, cancellationToken), cancellationToken);
            if (result.File.Probes.Count == 0)
            {
                return $"No probe cleared the quality gate on {llm.EmbeddingModel}, so nothing was saved.";
            }

            result.File.SaveTo(App.ProbesDirectory);
            var rejected = result.Rejected.Count > 0 ? $" Rejected, so read as 0: {string.Join(", ", result.Rejected)}." : "";
            return $"{result.File.Probes.Count} of 4 probes trained for {llm.EmbeddingModel}.{rejected} Save and restart BrainSim to use them.";
        }
        catch (OperationCanceledException)
        {
            return "Retraining cancelled.";
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            return $"Retraining failed: {ex.Message}";
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
