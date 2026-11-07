using System.IO;
using System.Windows;
using BrainSim.Engine.Appraisal;
using BrainSim.Engine.Config;
using BrainSim.Engine.Hosting;
using BrainSim.Engine.Llm;

namespace BrainSim.App;

public partial class App : Application
{
    /// <summary>Checkpoint, journals and layout. BRAINSIM_DATA points a test run somewhere else, so it leaves the real brain alone.</summary>
    public static string DataDirectory { get; } =
        Environment.GetEnvironmentVariable("BRAINSIM_DATA") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrainSim");

    /// <summary>Probes retrained in Settings, one file per embedding model and prefix (D-39).</summary>
    public static string ProbesDirectory => Path.Combine(DataDirectory, "probes");

    private EngineHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            // The Settings dialog's choice of backend replaces the llm section of config.yaml (D-35).
            var config = EngineConfig.LoadDefault();
            var hadSettings = File.Exists(SettingsWindow.SettingsPath);
            if (LlmSettingsFile.Load(SettingsWindow.SettingsPath, Dpapi.Unprotect) is { } settings)
            {
                config = config with { Llm = settings.ApplyTo(config.Llm) };
            }

            // Probes trained on another embedding model would read noise, so only probes for this one are used:
            // retrained in Settings, or the shipped ones (D-35, D-39).
            var probeFile = ProbeFile.Pick(ProbesDirectory, config.Llm);
            var probesFit = probeFile is not null || ProbeFile.LoadDefault() is null;
            var probes = probeFile?.Probes ?? [];
            _host = new EngineHost(config, LmStudioClient.Create(config.Llm), probes, DataDirectory);
            var viewModel = new MainViewModel(_host, config, probes.Count, probesFit);
            await _host.StartAsync();
            if (hadSettings && !File.Exists(SettingsWindow.SettingsPath))
            {
                _host.Report("settings.json could not be read. It was set aside as settings.json.bad and the backend from config.yaml is used.");
            }

            if (!probesFit)
            {
                _host.Report($"No appraisal probes were trained on {BrainSim.Engine.Memory.EmbeddingStamp.From(config.Llm)}, so messages are not appraised. Retrain them in Settings.");
            }

            var window = new MainWindow(viewModel);
            window.Closing += (_, _) =>
            {
                viewModel.Stop();
                window.SaveLayout();

                // Dispose on the thread pool: awaiting it here would resume on the UI thread we are blocking.
                Task.Run(() => _host.DisposeAsync().AsTask()).GetAwaiter().GetResult();
            };
            MainWindow = window;
            window.Show();
            viewModel.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"BrainSim could not start:\n\n{ex.Message}", "BrainSim", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
