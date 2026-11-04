using System.IO;
using System.Windows;
using BrainSim.Engine.Appraisal;
using BrainSim.Engine.Config;
using BrainSim.Engine.Hosting;
using BrainSim.Engine.Llm;

namespace BrainSim.App;

public partial class App : Application
{
    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrainSim");

    private EngineHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var config = EngineConfig.LoadDefault();
            var probes = ProbeFile.LoadDefault()?.Probes ?? [];
            _host = new EngineHost(config, LmStudioClient.Create(config.Llm), probes, DataDirectory);
            var viewModel = new MainViewModel(_host, config, probes.Count);
            await _host.StartAsync();

            var window = new MainWindow(viewModel);
            window.Closing += (_, _) =>
            {
                viewModel.Stop();

                // The engine loop never needs the UI thread, so waiting here cannot deadlock.
                _host.DisposeAsync().AsTask().GetAwaiter().GetResult();
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
