using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using BrainSim.App.Brain3D;
using BrainSim.Engine;
using BrainSim.Engine.Journal;
using HelixToolkit;
using HelixToolkit.Geometry;
using HelixToolkit.Maths;
using HelixToolkit.SharpDX;
using HelixToolkit.Wpf.SharpDX;
using Color = System.Windows.Media.Color;
using MeshGeometry3D = HelixToolkit.SharpDX.MeshGeometry3D;

namespace BrainSim.App.Views;

/// <summary>
/// The hybrid holographic brain (D-08, docs/ui-design.md "3D scene"): transparent cortex, tinted subcortical
/// structures, glowing nuclei whose brightness follows their substance, and particles flowing along the
/// projection pathways at a rate set by how fast each substance is being released.
/// </summary>
public partial class BrainView : UserControl
{
    // Particles per second per pathway, per unit of release flux (concentration per second).
    private const float ParticleGain = 150;
    private const float MaxParticleRate = 300;

    private readonly Dictionary<string, (MeshGeometryModel3D Model, PhongMaterial Material, BrainRegion Region)> _regions = [];
    private readonly List<(MeshGeometryModel3D Model, PhongMaterial Material, Substance Substance, bool Hpa)> _nuclei = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly double[] _release = new double[Substances.Count]; // smoothed release flux per substance, per second
    private readonly List<System.Numerics.Vector3> _particlePositions = [];
    private readonly List<Vector4> _particleColors = [];
    private readonly Random _random = new(2);

    private MainViewModel? _viewModel;
    private BrainModel? _model;
    private List<Pathway> _paths = [];
    private ParticleField? _particles;
    private PointGeometryModel3D? _points;
    private PointGeometry3D? _pointGeometry;
    private LineGeometryModel3D? _pathLines;
    private LineGeometryModel3D? _hpaLines;
    private BillboardTextModel3D? _labels;
    private AmbientLight3D? _ambient;
    private double[] _k = [];
    private TickRecord? _previous;
    private TimeSpan _lastRender;
    private double _tokenPulse;

    public BrainView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => _viewModel = DataContext as MainViewModel;
        Loaded += (_, _) => Build();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                CompositionTarget.Rendering += OnRendering;
            }
            else
            {
                CompositionTarget.Rendering -= OnRendering;
            }
        };
    }

    private void Build()
    {
        if (_model is not null || _viewModel is null)
        {
            return;
        }

        try
        {
            _model = BrainModel.Load(Path.Combine(AppContext.BaseDirectory, "assets", "brain.bin"));
        }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            LoadError.Text = $"The brain model could not be loaded: {e.Message}. Run tools/MeshPrep to build assets/models/brain.bin.";
            LoadError.Visibility = Visibility.Visible;
            return;
        }

        _k = [.. _viewModel.Config.HalfLives().Select(t => Math.Log(2) / t)];
        Viewport.EffectsManager = new DefaultEffectsManager();
        Viewport.OITRenderMode = OITRenderType.SinglePassWeighted;
        Viewport.MouseMove3D += OnMouseMove3D;

        _ambient = new AmbientLight3D { Color = Color.FromRgb(0x30, 0x38, 0x44) };
        Viewport.Items.Add(_ambient);
        Viewport.Items.Add(new DirectionalLight3D { Direction = new Vector3D(-0.6, -1, -0.8), Color = Color.FromRgb(0x80, 0x88, 0x96) });
        Viewport.Items.Add(new DirectionalLight3D { Direction = new Vector3D(0.6, 0.3, 1), Color = Color.FromRgb(0x30, 0x36, 0x40) });
        Viewport.Items.Add(new PostEffectBloom { ThresholdColor = Color.FromArgb(0, 0x30, 0x30, 0x30), NumberOfBlurPass = 2, BloomCombineIntensity = 1.1 });

        // Helix has no fresnel material, so a soft border highlight gives the cortex its holographic rim (D-02 spike notes).
        Viewport.Items.Add(new PostEffectMeshBorderHighlight { EffectName = "rim", Color = Color.FromArgb(0x90, 0x5A, 0xA9, 0xE6), NumberOfBlurPass = 2, ScaleX = 1, ScaleY = 1 });

        // Opaque things first, transparent cortex last, so blending reads well.
        foreach (var region in _model.Regions.OrderBy(r => r.Group == "cortex"))
        {
            var material = RegionMaterial(region.Group);
            var model = new MeshGeometryModel3D
            {
                Geometry = new MeshGeometry3D
                {
                    Positions = new Vector3Collection(region.Positions),
                    Normals = new Vector3Collection(region.Normals),
                    TriangleIndices = new IntCollection(region.Indices),
                },
                Material = material,
                IsTransparent = true,
                CullMode = SharpDX.Direct3D11.CullMode.None,
                PostEffects = region.Group switch { "gland" => "bloom", "cortex" => "rim", _ => string.Empty },
            };
            _regions[region.Key] = (model, material, region);
            Viewport.Items.Add(model);
        }

        // Nuclei and particles glow through the tissue, so they are drawn on top of it (holographic style).
        var glowing = new TopMostGroup3D { EnableTopMost = true };
        Viewport.Items.Add(glowing);

        foreach (var (anchor, substance, radius, hpa) in Nuclei)
        {
            var sphere = new MeshBuilder();
            sphere.AddSphere(_model.Anchors[anchor], radius, 20, 14);
            var material = new PhongMaterial { DiffuseColor = Rgba(Palette.Hex(substance), 1), EmissiveColor = Rgba(Palette.Hex(substance), 0.35f) };
            var model = new MeshGeometryModel3D { Geometry = sphere.ToMeshGeometry3D(), Material = material, PostEffects = "bloom" };
            _nuclei.Add((model, material, substance, hpa));
            glowing.Children.Add(model);
        }

        _paths = Pathways.Build(_model);
        _particles = new ParticleField(_paths);
        _pathLines = PathLines(_paths.Where(p => !p.Hpa));
        _hpaLines = PathLines(_paths.Where(p => p.Hpa));
        Viewport.Items.Add(_pathLines);
        Viewport.Items.Add(_hpaLines);

        _pointGeometry = new PointGeometry3D { Positions = [], Colors = [] };
        _points = new PointGeometryModel3D
        {
            Geometry = _pointGeometry,
            Color = Colors.White,
            Size = new System.Windows.Size(4.5, 4.5),
            Figure = PointFigure.Ellipse,
            PostEffects = "bloom",
        };
        glowing.Children.Add(_points);

        var text = new BillboardText3D();
        foreach (var (anchor, label) in Labels)
        {
            text.TextInfo.Add(new TextInfo(label, _model.Anchors[anchor]) { Foreground = new Color4(0.85f, 0.9f, 0.95f, 1), Scale = 0.55f });
        }

        _labels = new BillboardTextModel3D { Geometry = text };
        Viewport.Items.Add(_labels);

        SetCamera("lateral", animate: false);
        ApplyLayers();
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (_model is null || _viewModel?.Frame is not { } frame || _particles is null || _points is null)
        {
            return;
        }

        var now = _clock.Elapsed;
        var dt = (float)Math.Clamp((now - _lastRender).TotalSeconds, 0, 0.1);
        _lastRender = now;

        UpdateRelease(frame);
        if (frame.Token is not null && frame != _previous)
        {
            _tokenPulse = 1;
        }

        _tokenPulse = Math.Max(0, _tokenPulse - dt * 5);

        // Particles: rate follows the release of each pathway's substance.
        _particles.Update(
            dt,
            path => (float)Math.Min(MaxParticleRate, Math.Max(0, _release[(int)_paths[path].Substance]) * ParticleGain),
            path => _paths[path].Hpa ? ShowHpa.IsChecked == true : ShowPaths.IsChecked == true);
        _particles.Fill(_particlePositions, _particleColors, s => Rgb(Palette.Hex(s)));
        var geometry = _pointGeometry!;
        geometry.Positions = new Vector3Collection(_particlePositions);
        geometry.Colors = new Color4Collection(_particleColors.Select(c => new Color4(c.X, c.Y, c.Z, c.W)));
        geometry.UpdateVertices();
        geometry.UpdateColors();

        // Nuclei: brightness follows the concentration; below baseline they dim.
        foreach (var (_, material, substance, _) in _nuclei)
        {
            material.EmissiveColor = Rgba(Palette.Hex(substance), Glow(frame.C[(int)substance]));
        }

        // Glands and nuclei with meshes of their own.
        Tint("hypothalamus", Substance.Oxytocin, frame);
        Tint("pineal", Substance.Melatonin, frame);
        Tint("pituitary", Substance.GrowthHormone, frame);

        // Amygdala lights up with the threat of the last message, fading over a few seconds.
        var threat = _viewModel.ThreatPulse();
        foreach (var key in new[] { "amygdala_l", "amygdala_r" })
        {
            _regions[key].Material.EmissiveColor = Rgba(Palette.Hex(Substance.Cortisol), 0.08f + (float)threat * 0.9f);
        }

        UpdateCortex(frame, dt);
        UpdateLighting(frame);
    }

    /// <summary>
    /// Release flux per second: how fast each substance is being produced, not where its level sits.
    /// From the spec's dynamics, production = dC/dt + k (C - b). At baseline this is 0, so the calm scene stays calm.
    /// </summary>
    private void UpdateRelease(TickRecord frame)
    {
        if (_previous is null || frame.Tick < _previous.Tick || frame.Tick - _previous.Tick > 5000)
        {
            // First frame, or the cursor jumped in replay: start over instead of drawing a burst.
            _previous = frame;
            Array.Clear(_release);
            _particles?.Clear();
            return;
        }

        var ticks = frame.Tick - _previous.Tick;
        if (ticks == 0)
        {
            return;
        }

        // Exact for any number of ticks between frames (fast simulated time skips hundreds):
        // C_t = b + (C_0 - b) e^(-k n) + (p / k)(1 - e^(-k n)), solved for the production p per tick.
        // Release is measured per simulated second (ticks_per_second ticks), so the stream shows how strong a
        // release is, not how fast time is playing: at x600 slow background production would otherwise flood.
        var ticksPerSecond = _viewModel!.Config.Time.TicksPerSecond;
        for (var i = 0; i < Substances.Count; i++)
        {
            var b = frame.Baseline[i];
            var k = frame.Phase == "Wake" && i == (int)Substance.Adenosine ? 0 : _k[i];
            var decay = Math.Exp(-k * ticks);
            var perTick = k == 0
                ? (frame.C[i] - _previous.C[i]) / ticks
                : k * (frame.C[i] - b - (_previous.C[i] - b) * decay) / (1 - decay);
            var perSecond = perTick * ticksPerSecond;
            _release[i] += (perSecond - _release[i]) * 0.5;
        }

        _previous = frame;
    }

    private void UpdateCortex(TickRecord frame, float dt)
    {
        var gaba = Math.Clamp(frame.C[(int)Substance.Gaba] - 1, 0, 1.5);
        var fatigue = 1 - 0.3 * Math.Clamp((frame.C[(int)Substance.Adenosine] - 1) / 3, 0, 1);
        var rem = frame.Phase == "Rem" ? _random.NextDouble() * 0.12 : 0;
        var opacity = (float)CortexOpacity.Value;
        foreach (var (_, material, region) in _regions.Values.Where(r => r.Region.Group == "cortex"))
        {
            var e = (0.05 + 0.10 * _tokenPulse + rem) * fatigue;
            material.EmissiveColor = new Color4((float)(e * 0.6), (float)(e * 0.9 + gaba * 0.05), (float)(e * 1.4 + gaba * 0.25), 1);
            material.DiffuseColor = new Color4(0.35f, 0.6f, 0.9f, opacity);
        }
    }

    /// <summary>Wake is neutral; sleep dims and cools the scene, deepest in N3.</summary>
    private void UpdateLighting(TickRecord frame)
    {
        if (_ambient is null)
        {
            return;
        }

        _ambient.Color = frame.Phase switch
        {
            "N1" => Color.FromRgb(0x24, 0x2C, 0x3C),
            "N2" => Color.FromRgb(0x1C, 0x24, 0x3A),
            "N3" => Color.FromRgb(0x10, 0x18, 0x34),
            "Rem" => Color.FromRgb(0x22, 0x1C, 0x3A),
            _ => Color.FromRgb(0x30, 0x38, 0x44),
        };
    }

    private void Tint(string region, Substance substance, TickRecord frame) =>
        _regions[region].Material.EmissiveColor = Rgba(Palette.Hex(substance), Glow(frame.C[(int)substance]) * 0.8f);

    /// <summary>0.35 at baseline, up to 1 when well above it, dimmer below it.</summary>
    private static float Glow(double c) => c >= 1 ? (float)(0.35 + 0.65 * Math.Clamp((c - 1) / 1.5, 0, 1)) : (float)(0.35 * Math.Max(c, 0.1));

    private void OnLayersChanged(object sender, RoutedEventArgs e) => ApplyLayers();

    private bool _medial;

    private void ApplyLayers()
    {
        if (_model is null)
        {
            return;
        }

        foreach (var (model, _, region) in _regions.Values)
        {
            var shown = region.Group switch
            {
                "cortex" => ShowCortex.IsChecked == true,
                "gland" => true,
                _ => ShowSubcortical.IsChecked == true,
            };
            if (_medial && region.IsRight)
            {
                shown = false;
            }

            model.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        }

        foreach (var (model, _, _, hpa) in _nuclei)
        {
            model.Visibility = (hpa ? ShowHpa.IsChecked == true : ShowNuclei.IsChecked == true) ? Visibility.Visible : Visibility.Collapsed;
        }

        _pathLines!.Visibility = ShowPaths.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        _hpaLines!.Visibility = ShowHpa.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        _labels!.Visibility = ShowLabels.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnCamera(object sender, RoutedEventArgs e) => SetCamera((string)((Button)sender).Tag, animate: true);

    private void SetCamera(string view, bool animate)
    {
        _medial = view == "medial";
        ApplyLayers();
        var (position, up) = view switch
        {
            "medial" => (new Point3D(4.0, 0.2, 0), new Vector3D(0, 1, 0)),
            "top" => (new Point3D(0, 4.3, 0.01), new Vector3D(0, 0, -1)),
            "front" => (new Point3D(0, 0.3, 4.3), new Vector3D(0, 1, 0)),
            _ => (new Point3D(-4.0, 0.45, 0.3), new Vector3D(0, 1, 0)),
        };
        var look = new Vector3D(-position.X, -position.Y, -position.Z);
        if (Viewport.Camera is HelixToolkit.Wpf.SharpDX.ProjectionCamera camera && animate)
        {
            camera.AnimateTo(position, look, up, 400);
        }
        else
        {
            Viewport.Camera = new HelixToolkit.Wpf.SharpDX.PerspectiveCamera { Position = position, LookDirection = look, UpDirection = up, FieldOfView = 40 };
        }
    }

    private void OnMouseMove3D(object? sender, RoutedEventArgs e)
    {
        if (e is not MouseMove3DEventArgs args || _viewModel?.Frame is not { } frame)
        {
            return;
        }

        var hit = args.HitTestResult?.ModelHit;
        var region = _regions.FirstOrDefault(r => ReferenceEquals(r.Value.Model, hit));
        var nucleus = _nuclei.FindIndex(n => ReferenceEquals(n.Model, hit));
        string? text = null;
        if (region.Key is { } key)
        {
            text = RegionName(key) + RegionSubstances(key, frame);
        }
        else if (nucleus >= 0)
        {
            var (anchor, substance, _, _) = Nuclei[nucleus];
            text = $"{RegionName(anchor)}\n{substance.Abbreviation()} {frame.C[(int)substance]:0.00}, release {_release[(int)substance]:+0.00;-0.00}/s";
        }

        InfoPanel.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        Info.Text = text ?? "";
    }

    private static string RegionSubstances(string key, TickRecord f)
    {
        Substance[] shown = key switch
        {
            "hypothalamus" => [Substance.Oxytocin, Substance.Cortisol],
            "pineal" => [Substance.Melatonin],
            "pituitary" => [Substance.GrowthHormone, Substance.Oxytocin],
            "amygdala_l" or "amygdala_r" => [Substance.Cortisol, Substance.Norepinephrine],
            "hippocampus_l" or "hippocampus_r" => [Substance.Acetylcholine, Substance.Cortisol],
            "caudate_l" or "caudate_r" or "putamen_l" or "putamen_r" => [Substance.Dopamine],
            _ when key.StartsWith("frontal", StringComparison.Ordinal) => [Substance.Dopamine, Substance.Norepinephrine, Substance.Gaba],
            _ => [Substance.Norepinephrine, Substance.Gaba],
        };
        return "\n" + string.Join("   ", shown.Select(s => $"{s.Abbreviation()} {f.C[(int)s]:0.00}"));
    }

    private static string RegionName(string key)
    {
        var side = key.EndsWith("_l", StringComparison.Ordinal) ? " (left)" : key.EndsWith("_r", StringComparison.Ordinal) ? " (right)" : "";
        var name = key.TrimEnd('l', 'r').TrimEnd('_') switch
        {
            "frontal" => "Frontal lobe",
            "parietal" => "Parietal lobe",
            "temporal" => "Temporal lobe",
            "occipital" => "Occipital lobe",
            "limbic" => "Cingulate and parahippocampal gyri",
            "insula" => "Insula",
            "orbital" => "Orbital gyri",
            "thalamus" => "Thalamus",
            "caudate" => "Caudate nucleus",
            "putamen" => "Putamen",
            "pallidus" => "Globus pallidus",
            "hippocampus" => "Hippocampus",
            "amygdala" => "Amygdala",
            "hypothalamus" => "Hypothalamus",
            "mammillary" => "Mammillary body",
            "corpus_callosum" => "Corpus callosum",
            "fornix" => "Fornix",
            "midbrain" => "Midbrain",
            "pons" => "Pons",
            "medulla" => "Medulla oblongata",
            "cerebellum" => "Cerebellum",
            "pineal" => "Pineal gland",
            "pituitary" => "Pituitary gland",
            "locus_coeruleus" => "Locus coeruleus",
            "raphe" => "Raphe nuclei",
            "vta" => "VTA and substantia nigra",
            "basal_forebrain" => "Nucleus basalis",
            "scn" => "Suprachiasmatic nucleus",
            "adrenal" => "Adrenal glands (HPA axis, not to scale)",
            var other => other,
        };
        return name + side;
    }

    private static PhongMaterial RegionMaterial(string group) => group switch
    {
        "cortex" => new PhongMaterial { DiffuseColor = new Color4(0.35f, 0.6f, 0.9f, 0.12f), SpecularColor = new Color4(0.5f, 0.6f, 0.8f, 1), SpecularShininess = 30 },
        "gland" => new PhongMaterial { DiffuseColor = new Color4(0.75f, 0.8f, 0.95f, 0.75f), SpecularShininess = 20 },
        "brainstem" => new PhongMaterial { DiffuseColor = new Color4(0.45f, 0.52f, 0.68f, 0.35f), EmissiveColor = new Color4(0.03f, 0.04f, 0.06f, 1), SpecularShininess = 20 },
        _ => new PhongMaterial { DiffuseColor = new Color4(0.5f, 0.56f, 0.74f, 0.45f), EmissiveColor = new Color4(0.04f, 0.05f, 0.08f, 1), SpecularShininess = 20 },
    };

    private LineGeometryModel3D PathLines(IEnumerable<Pathway> paths)
    {
        var positions = new Vector3Collection();
        var indices = new IntCollection();
        foreach (var path in paths)
        {
            for (var s = 0; s <= 16; s++)
            {
                if (s > 0)
                {
                    indices.Add(positions.Count - 1);
                    indices.Add(positions.Count);
                }

                positions.Add(path.At(s / 16f));
            }
        }

        return new LineGeometryModel3D
        {
            Geometry = new LineGeometry3D { Positions = positions, Indices = indices },
            Color = Color.FromArgb(0x50, 0x8B, 0x98, 0xA5),
            Thickness = 0.6,
        };
    }

    private static System.Numerics.Vector3 Rgb(string hex)
    {
        var c = (Color)ColorConverter.ConvertFromString(hex);
        return new System.Numerics.Vector3(c.R / 255f, c.G / 255f, c.B / 255f);
    }

    private static Color4 Rgba(string hex, float intensity)
    {
        var c = Rgb(hex) * intensity;
        return new Color4(c.X, c.Y, c.Z, 1);
    }

    /// <summary>Proxy spheres for nuclei too small to have meshes, at the anchors MeshPrep computed.</summary>
    private static readonly (string Anchor, Substance Substance, float Radius, bool Hpa)[] Nuclei =
    [
        ("locus_coeruleus_l", Substance.Norepinephrine, 0.045f, false),
        ("locus_coeruleus_r", Substance.Norepinephrine, 0.045f, false),
        ("raphe", Substance.Serotonin, 0.05f, false),
        ("vta", Substance.Dopamine, 0.055f, false),
        ("basal_forebrain_l", Substance.Acetylcholine, 0.045f, false),
        ("basal_forebrain_r", Substance.Acetylcholine, 0.045f, false),
        ("scn", Substance.Melatonin, 0.035f, false),
        ("adrenal", Substance.Cortisol, 0.09f, true),
    ];

    private static readonly (string Anchor, string Label)[] Labels =
    [
        ("locus_coeruleus_l", "LC"), ("raphe", "Raphe"), ("vta", "VTA"), ("basal_forebrain_l", "N. basalis"),
        ("scn", "SCN"), ("pineal", "Pineal"), ("pituitary", "Pituitary"), ("hypothalamus", "Hypothalamus"),
        ("hippocampus_l", "Hippocampus"), ("amygdala_l", "Amygdala"), ("thalamus_l", "Thalamus"),
        ("caudate_l", "Caudate"), ("putamen_l", "Putamen"), ("cerebellum", "Cerebellum"), ("frontal_l", "Frontal"),
        ("occipital_l", "Occipital"), ("adrenal", "Adrenal"),
    ];
}
