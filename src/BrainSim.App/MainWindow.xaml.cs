using System.IO;
using System.Windows;
using AvalonDock.Layout;
using AvalonDock.Serializer.Xml;

namespace BrainSim.App;

public partial class MainWindow : Window
{
    private static readonly string LayoutPath = Path.Combine(App.DataDirectory, "layout.xml");

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        // Content inside AvalonDock does not reliably inherit the window's DataContext, so set it on each panel.
        foreach (var panel in new FrameworkElement[] { ChemistryPanel, ChatPanel, ReceptorsPanel, EffectorsPanel, ClockPanel, SleepPanel, LabPanel })
        {
            panel.DataContext = viewModel;
        }

        Loaded += (_, _) => LoadLayout();
    }

    /// <summary>Saves where the panels are. Called on close.</summary>
    public void SaveLayout()
    {
        try
        {
            Directory.CreateDirectory(App.DataDirectory);
            new XmlLayoutSerializer(Dock).Serialize(LayoutPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Losing the layout is not worth failing the shutdown for.
        }
    }

    private void LoadLayout()
    {
        if (!File.Exists(LayoutPath))
        {
            return;
        }

        // The serializer rebuilds the panes; hand each one back its existing view by ContentId.
        var views = Dock.Layout.Descendents().OfType<LayoutContent>()
            .Where(c => c.ContentId is not null)
            .ToDictionary(c => c.ContentId, c => c.Content);
        var serializer = new XmlLayoutSerializer(Dock);
        serializer.LayoutSerializationCallback += (_, e) =>
        {
            if (e.Model.ContentId is { } id && views.TryGetValue(id, out var view))
            {
                e.Content = view;
            }
        };

        try
        {
            serializer.Deserialize(LayoutPath);
        }
        catch (Exception)
        {
            // A stale or broken layout file falls back to the default layout next time.
            File.Delete(LayoutPath);
        }
    }
}
