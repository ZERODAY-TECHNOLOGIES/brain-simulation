using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using HelixToolkit;
using HelixToolkit.Geometry;
using HelixToolkit.Maths;
using HelixToolkit.SharpDX;
using HelixToolkit.Wpf.SharpDX;
using Color = System.Windows.Media.Color;

namespace HelixSpike;

/// <summary>
/// Phase 0 spike: transparent cortex, glowing nuclei with bloom, and particles flowing along pathways.
/// Throwaway code. It answers one question: does Helix Toolkit hold 60 fps on the MX550 with this kind of scene?
/// </summary>
public partial class MainWindow : Window
{
    private const int ParticleCount = 20_000;

    private readonly (Vector3 From, Vector3 To, Vector3 Bend, Color4 Color)[] _paths;
    private readonly float[] _phase = new float[ParticleCount];
    private readonly int[] _pathOf = new int[ParticleCount];
    private readonly PointGeometryModel3D _particles = new();
    private readonly List<PhongMaterial> _nucleusMaterials = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TimeSpan _lastFrame;
    private readonly PointGeometry3D _geometry;
    private TimeSpan _windowStart;
    private int _frames;

    public MainWindow()
    {
        InitializeComponent();
        Viewport.EffectsManager = new DefaultEffectsManager();
        Viewport.OITRenderMode = OITRenderType.SinglePassWeighted;
        Viewport.Camera = new HelixToolkit.Wpf.SharpDX.PerspectiveCamera
        {
            Position = new Point3D(0, 0.5, 6),
            LookDirection = new Vector3D(0, -0.5, -6),
            UpDirection = new Vector3D(0, 1, 0),
        };

        Viewport.Items.Add(new AmbientLight3D { Color = Color.FromRgb(0x30, 0x38, 0x44) });
        Viewport.Items.Add(new DirectionalLight3D { Direction = new Vector3D(-1, -1, -1), Color = Color.FromRgb(0x90, 0x98, 0xA0) });
        Viewport.Items.Add(new PostEffectBloom { ThresholdColor = Color.FromArgb(0, 0x40, 0x40, 0x40), NumberOfBlurPass = 2, BloomCombineIntensity = 1.2 });

        // Cortex: a transparent ellipsoid standing in for the real mesh.
        var cortex = new MeshBuilder();
        cortex.AddEllipsoid(Vector3.Zero, 1.7f, 1.25f, 1.45f, 96, 64);
        Viewport.Items.Add(new MeshGeometryModel3D
        {
            Geometry = cortex.ToMeshGeometry3D(),
            Material = new PhongMaterial
            {
                DiffuseColor = new Color4(0.35f, 0.55f, 0.8f, 0.12f),
                EmissiveColor = new Color4(0.03f, 0.06f, 0.1f, 1f),
                SpecularColor = new Color4(0.6f, 0.7f, 0.9f, 1f),
                SpecularShininess = 40,
            },
            IsTransparent = true,
            CullMode = SharpDX.Direct3D11.CullMode.None,
        });

        // Nuclei and their targets, roughly where they sit in a real brain (x right, y up, z front).
        (string Name, Vector3 At, Color4 Color, Vector3[] Targets)[] nuclei =
        [
            ("NE locus coeruleus", new(0.1f, -0.75f, -0.3f), Hex(0xF5A524), [new(1.2f, 0.6f, 0.6f), new(-1.2f, 0.6f, 0.6f), new(0, 0.9f, -0.8f)]),
            ("DA VTA", new(0, -0.45f, 0.05f), Hex(0xD946EF), [new(0.35f, 0.1f, 0.35f), new(0, 0.5f, 1.2f)]),
            ("5-HT raphe", new(0, -0.85f, -0.15f), Hex(0xA78BFA), [new(0.9f, 0.4f, -0.3f), new(-0.9f, 0.4f, -0.3f)]),
            ("ACh basal forebrain", new(0, -0.2f, 0.45f), Hex(0x22D3EE), [new(0.6f, 0.8f, 0.4f), new(-0.6f, 0.8f, 0.4f)]),
            ("MEL pineal", new(0, -0.05f, -0.55f), Hex(0xC7D2FE), [new(0, -1.1f, -0.2f)]),
        ];

        var paths = new List<(Vector3, Vector3, Vector3, Color4)>();
        foreach (var n in nuclei)
        {
            var sphere = new MeshBuilder();
            sphere.AddSphere(n.At, 0.07f, 24, 16);
            var material = new PhongMaterial { DiffuseColor = n.Color, EmissiveColor = n.Color };
            _nucleusMaterials.Add(material);
            Viewport.Items.Add(new MeshGeometryModel3D { Geometry = sphere.ToMeshGeometry3D(), Material = material, PostEffects = "bloom" });

            foreach (var target in n.Targets)
            {
                var mid = (n.At + target) / 2;
                paths.Add((n.At, target, mid + Vector3.Normalize(mid) * 0.4f, n.Color));
            }
        }

        _paths = [.. paths];
        var random = new Random(1);
        var colors = new Color4Collection(ParticleCount);
        for (var i = 0; i < ParticleCount; i++)
        {
            _pathOf[i] = i % _paths.Length;
            _phase[i] = (float)random.NextDouble();
            colors.Add(_paths[_pathOf[i]].Color);
        }

        _geometry = new PointGeometry3D { Positions = Positions(0), Colors = colors };
        _particles.Geometry = _geometry;
        _particles.Color = Colors.White;
        _particles.Size = new Size(2.5, 2.5);
        _particles.Figure = PointFigure.Ellipse;
        _particles.PostEffects = "bloom";
        Viewport.Items.Add(_particles);

        CompositionTarget.Rendering += OnRendering;
    }

    private static Color4 Hex(int rgb) => new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);

    private Vector3Collection Positions(float time)
    {
        var positions = new Vector3Collection(ParticleCount);
        for (var i = 0; i < ParticleCount; i++)
        {
            var (from, to, bend, _) = _paths[_pathOf[i]];
            var t = (_phase[i] + time * 0.25f) % 1f;
            var u = 1 - t;
            positions.Add(u * u * from + 2 * u * t * bend + t * t * to);
        }

        return positions;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = _clock.Elapsed;
        if (now == _lastFrame)
        {
            return;
        }

        _lastFrame = now;
        var time = (float)now.TotalSeconds;

        // Particles move, nuclei pulse: the two things the real scene updates every frame.
        _geometry.Positions = Positions(time);
        _geometry.UpdateVertices();
        for (var i = 0; i < _nucleusMaterials.Count; i++)
        {
            var c = _nucleusMaterials[i].DiffuseColor;
            var pulse = 0.55f + 0.45f * MathF.Sin(time * 2 + i);
            _nucleusMaterials[i].EmissiveColor = new Color4(c.Red * pulse, c.Green * pulse, c.Blue * pulse, 1f);
        }

        _frames++;
        if ((now - _windowStart).TotalSeconds >= 1)
        {
            var fps = _frames / (now - _windowStart).TotalSeconds;
            Stats.Text = $"particles {ParticleCount:N0}   frames/s {fps:F0}   t {time:F0} s";
            if (Environment.GetEnvironmentVariable("SPIKE_LOG") is { } log)
            {
                File.AppendAllText(log, $"t={time:F0} fps={fps:F1}{Environment.NewLine}");
            }

            _frames = 0;
            _windowStart = now;
        }
    }
}
