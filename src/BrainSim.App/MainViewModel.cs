using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Threading;
using BrainSim.Engine;
using BrainSim.Engine.Circadian;
using BrainSim.Engine.Config;
using BrainSim.Engine.Effectors;
using BrainSim.Engine.Hosting;
using BrainSim.Engine.Journal;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BrainSim.App;

public sealed partial class ChatItem(string role, string text, string detail) : ObservableObject
{
    public string Role { get; } = role;
    public bool IsUser => Role == EventKinds.User;
    public bool IsError => Role == EventKinds.Error;

    [ObservableProperty]
    private string _text = text;

    [ObservableProperty]
    private string _detail = detail;
}

public sealed record ClockMode(string Label, double? Factor)
{
    public override string ToString() => Label;
}

public sealed record WindowOption(string Label, long Ticks)
{
    public override string ToString() => Label;
}

public sealed record Marker(long Tick, Brush Brush);

/// <summary>One dream journal entry as shown in the Dreams panel.</summary>
public sealed record DreamItem(string When, string Details, string Seeds, string Text);

/// <summary>
/// The app state. Everything is drawn "as of" <see cref="CursorTick"/>: live, the cursor follows the newest tick;
/// in replay, or after dragging the timeline, it stays where it was put.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private readonly EngineHost _host;
    private readonly EngineConfig _config;
    private readonly CircadianClock _clockShape;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer _timer;
    private ReplaySession? _replay;
    private string? _streaming;
    private int _chatEventCount = -1;
    private long _chatCursor = -1;
    private DateTime _lastSlowUpdate;
    private (double Value, DateTime At) _lastThreat;

    public MainViewModel(EngineHost host, EngineConfig config, int probeCount)
    {
        _host = host;
        _config = config;
        _clockShape = new CircadianClock(config.Circadian);
        ProbesText = probeCount == 4 ? "Probes: 4 of 4" : $"Probes: {probeCount} of 4, missing signals read as 0";
        SelectedClockMode = ClockModes[0];
        SelectedWindow = Windows[1];
        SubstanceRows = [.. Substances.All.Select(s => new SubstanceRow(s, this))];
        var w = config.Interactions.ToDictionary(i => (Substances.Parse(i.From), Substances.Parse(i.To)), i => i.W);
        WCells = [.. Substances.All.SelectMany(from => Substances.All.Select(to => new WCell(from, to, w.GetValueOrDefault((from, to)), this)))];
        Gains = [.. config.Appraisal.Gains.SelectMany(g => g.Value.Select(s => new GainRow(g.Key, Substances.Parse(s.Key), s.Value, this)))];
        Channels = [.. host.Agent.Brain.Modulation.Names.Select(n => new ChannelRow(n))];

        host.Appraised += (_, signals, _, _) => _dispatcher.BeginInvoke(() =>
        {
            _streaming = "";
            _lastThreat = (signals.Threat, DateTime.UtcNow);
            RebuildChat(force: true);
        });
        host.TokenReceived += token => _dispatcher.BeginInvoke(() => AppendToken(token));
        host.Replied += _ => _dispatcher.BeginInvoke(() => { _streaming = null; RebuildChat(force: true); });
        host.Failed += _ => _dispatcher.BeginInvoke(() => { _streaming = null; RebuildChat(force: true); });

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => OnTimer(), _dispatcher);
        _ = PingLmStudioAsync();
        RefreshStability();
    }

    public EngineConfig Config => _config;
    public TickHistory History => _replay?.History ?? _host.History;
    public EventLog Events => _replay?.Events ?? _host.Events;
    public bool IsReplayFile => _replay is not null;

    /// <summary>The record at the cursor. Null until the first tick.</summary>
    public TickRecord? Frame { get; private set; }

    /// <summary>Raised on the UI thread whenever the frame or the cursor changes.</summary>
    public event Action? FrameChanged;

    public IReadOnlyList<ClockMode> ClockModes { get; } =
    [
        new("Real time", null),
        new("Simulated x1", 1),
        new("Simulated x10", 10),
        new("Simulated x60", 60),
        new("Simulated x600", 600),
        new("Paused", 0),
    ];

    public IReadOnlyList<WindowOption> Windows { get; } =
    [
        new("1k ticks", 1_000),
        new("10k ticks", 10_000),
        new("100k ticks", 100_000),
        new("1 day", 864_000),
    ];

    public IReadOnlyList<double> ReplaySpeeds { get; } = [1, 10, 60, 600];

    public ObservableCollection<ChatItem> Chat { get; } = [];
    public ObservableCollection<DreamItem> Dreams { get; } = [];
    public ObservableCollection<SubstanceRow> SubstanceRows { get; }
    /// <summary>W as a 10 by 10 grid, rows are the source, columns the target, in substance order.</summary>
    public IReadOnlyList<WCell> WCells { get; }
    public IReadOnlyList<string> SubstanceNames { get; } = Substances.Abbreviations;
    public ObservableCollection<GainRow> Gains { get; }
    public ObservableCollection<ChannelRow> Channels { get; }
    public IReadOnlyList<Marker> Markers { get; private set; } = [];

    [ObservableProperty] private bool _isLive = true;
    [ObservableProperty] private string _sourceLabel = "LIVE";
    [ObservableProperty] private long _cursorTick;
    [ObservableProperty] private double _firstTick;
    [ObservableProperty] private double _lastTick;

    /// <summary>
    /// The timeline slider works in doubles. The slider only writes this when the user moves it
    /// (mouse, keyboard or automation), so any change here detaches the cursor from the newest tick.
    /// </summary>
    public double CursorPosition
    {
        get => CursorTick;
        set
        {
            var tick = (long)value;
            if (tick != CursorTick)
            {
                IsLive = false;
                CursorTick = tick;
            }
        }
    }
    [ObservableProperty] private string _cursorText = "";
    [ObservableProperty] private string _clockText = "";
    [ObservableProperty] private string _phaseText = "";
    [ObservableProperty] private string _lmStudioText = "LM Studio: checking";
    [ObservableProperty] private bool _lmStudioOk;
    [ObservableProperty] private string _probesText;
    [ObservableProperty] private ClockMode _selectedClockMode;
    [ObservableProperty] private WindowOption _selectedWindow;
    [ObservableProperty] private double _replaySpeed = 10;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private string _draft = "";
    [ObservableProperty] private string _stabilityText = "";
    [ObservableProperty] private bool _stable = true;
    [ObservableProperty] private string _samplingText = "";
    [ObservableProperty] private string _lastSamplingText = "";
    [ObservableProperty] private bool _dreaming;
    [ObservableProperty] private string _currentDream = "";
    [ObservableProperty] private string _currentDreamSeeds = "";
    [ObservableProperty] private string _dreamsEmptyText = "No dreams yet. They come in REM sleep, once there are memories to dream about.";

    public bool CanEdit => !IsReplayFile;

    /// <summary>1 while a dream is being dreamt, live. Lights the hippocampus, where the seeds come from.</summary>
    public double DreamActivity() => IsLive && _host.Agent.Dreamer.Current is not null ? 1 : 0;

    private int _dreamCount = -1;
    private bool _dreamSource;

    /// <summary>Live: from the dreamer, with seeds. A saved session: from its events journal, without seeds.</summary>
    private void RefreshDreams()
    {
        var current = IsLive ? _host.Agent.Dreamer.Current : null;
        Dreaming = current is not null;
        CurrentDream = current?.Text ?? "";
        CurrentDreamSeeds = current is null ? "" : string.Join("   |   ", current.Seeds.Select(Short));

        if (IsReplayFile)
        {
            var events = Events.Snapshot().Where(e => e.Kind == EventKinds.Dream).ToList();
            if (events.Count == _dreamCount && _dreamSource)
            {
                return;
            }

            _dreamCount = events.Count;
            _dreamSource = true;
            Dreams.Clear();
            foreach (var e in Enumerable.Reverse(events))
            {
                var v = e.Values ?? [];
                Dreams.Add(new DreamItem(
                    $"{e.Time:ddd HH:mm}",
                    $"{Get(v, "minutes", "0.0")} min   {(v.GetValueOrDefault("interrupted") > 0 ? "woken from it" : "slept through")}   NE {Get(v, "NE")}   5-HT {Get(v, "5-HT")}   ACh {Get(v, "ACh")}",
                    $"{Get(v, "seeds", "0")} seed fragments",
                    e.Text));
            }

            return;
        }

        var journal = _host.Agent.Dreamer.Journal;
        if (journal.Count == _dreamCount && !_dreamSource)
        {
            return;
        }

        _dreamCount = journal.Count;
        _dreamSource = false;
        Dreams.Clear();
        foreach (var d in journal.Reverse())
        {
            var c = d.Chemistry;
            Dreams.Add(new DreamItem(
                $"{d.Start:ddd HH:mm}",
                $"{(d.End - d.Start).TotalMinutes:0.0} min   {(d.Interrupted ? "woken from it" : "slept through")}   NE {c[(int)Substance.Norepinephrine]:0.00}   5-HT {c[(int)Substance.Serotonin]:0.00}   ACh {c[(int)Substance.Acetylcholine]:0.00}",
                string.Join("   |   ", d.Seeds.Select(Short)),
                d.Text.Length > 0 ? d.Text : "(nothing came through before the dream ended)"));
        }
    }

    private static string Short(string fragment)
    {
        var line = fragment.Split('\n')[0];
        return line.Length > 60 ? line[..57] + "..." : line;
    }

    /// <summary>Threat A of the last live message, fading over about two seconds. Lights the amygdala.</summary>
    public double ThreatPulse() =>
        IsLive ? _lastThreat.Value * Math.Exp(-(DateTime.UtcNow - _lastThreat.At).TotalSeconds / 2) : 0;

    public void Start() => _timer.Start();

    public void Stop() => _timer.Stop();

    partial void OnSelectedClockModeChanged(ClockMode value)
    {
        if (value.Factor is { } factor)
        {
            _host.SetSimulated(factor);
        }
        else
        {
            _host.SetReal();
        }
    }

    partial void OnIsLiveChanged(bool value)
    {
        if (!IsReplayFile)
        {
            SourceLabel = value ? "LIVE" : "LIVE SESSION, VIEWING THE PAST";
        }
    }

    partial void OnCursorTickChanged(long value) => OnPropertyChanged(nameof(CursorPosition));

    [RelayCommand]
    private void Send()
    {
        var text = Draft.Trim();
        if (text.Length == 0 || !CanEdit)
        {
            return;
        }

        GoLive();
        _host.Send(text);
        Draft = "";
    }

    [RelayCommand]
    private void GoLive()
    {
        _replay = null;
        IsLive = true;
        IsPlaying = false;
        SourceLabel = "LIVE";
        OnPropertyChanged(nameof(CanEdit));
        _chatEventCount = -1;
    }

    [RelayCommand]
    private void TogglePlay() => IsPlaying = !IsPlaying;

    [RelayCommand]
    private async Task OpenSessionAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Session journal (*.parquet)|*.parquet",
            InitialDirectory = _host.JournalPath is { } p ? Path.GetDirectoryName(p) : null,
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            _replay = await ReplaySession.LoadAsync(dialog.FileName);
        }
        catch (Exception e)
        {
            Chat.Add(new ChatItem(EventKinds.Error, $"Could not open {Path.GetFileName(dialog.FileName)}: {e.Message}", ""));
            return;
        }

        IsLive = false;
        IsPlaying = false;
        SourceLabel = "REPLAY " + Path.GetFileNameWithoutExtension(dialog.FileName);
        OnPropertyChanged(nameof(CanEdit));
        CursorTick = History.First?.Tick ?? 0;
        _chatEventCount = -1;
        UpdateFrame();
    }

    // Lab, called by the rows.
    public void Inject(Substance s, double amount) => _host.Inject(s, amount);

    public void Hold(Substance s, double? level) => _host.Hold(s, level);

    public void SetHalfLife(Substance s, double halfLife)
    {
        _host.SetHalfLife(s, halfLife);
        RefreshStability();
    }

    public void SetInteraction(Substance from, Substance to, double w)
    {
        _host.SetInteraction(from, to, w);
        RefreshStability();
    }

    public void SetBaseline(Substance s, double level) => _host.SetBaseline(s, level);

    public void SetGain(string signal, Substance s, double gain) => _host.SetGain(signal, s, gain);

    [RelayCommand]
    private void ResetLab()
    {
        _host.ResetLab();
        foreach (var row in SubstanceRows)
        {
            row.Reset();
        }

        foreach (var cell in WCells)
        {
            cell.Reset();
        }

        foreach (var row in Gains)
        {
            row.Reset();
        }

        RefreshStability();
    }

    private void RefreshStability() => _host.Post(agent =>
    {
        var awake = agent.Brain.Chemistry.LargestEigenvalue(asleep: false);
        var asleep = agent.Brain.Chemistry.LargestEigenvalue(asleep: true);
        _dispatcher.BeginInvoke(() =>
        {
            // Awake, ADO does not decay, so one eigenvalue is exactly 0 by design. The sleep matrix is the real check.
            Stable = asleep < 0 && awake <= 1e-12;
            StabilityText = Stable
                ? $"Stable. Largest eigenvalue of -K + W: {asleep:0.0e+0} (sleep), {awake:0.0e+0} (awake, ADO only)"
                : $"Unstable. Largest eigenvalue of -K + W: {asleep:0.0e+0} (sleep), {awake:0.0e+0} (awake)";
        });
    });

    private void OnTimer()
    {
        var latest = History.Latest;
        FirstTick = History.First?.Tick ?? 0;
        LastTick = latest?.Tick ?? 0;

        if (IsLive)
        {
            CursorTick = latest?.Tick ?? 0;
        }
        else if (IsPlaying)
        {
            var next = CursorTick + (long)Math.Max(1, ReplaySpeed * _config.Time.TicksPerSecond * 0.033);
            if (next >= (long)LastTick)
            {
                next = (long)LastTick;
                IsPlaying = false;
                if (!IsReplayFile)
                {
                    IsLive = true;
                }
            }

            CursorTick = next;
        }

        // Every timer tick, live or not, so panels that skipped a frame while throttled catch up.
        UpdateFrame();

        var now = DateTime.UtcNow;
        if ((now - _lastSlowUpdate).TotalMilliseconds >= 500)
        {
            _lastSlowUpdate = now;
            Markers = [.. Events.Snapshot()
                .Where(e => e.Kind is EventKinds.User or EventKinds.Lab or EventKinds.Sleep)
                .Select(e => new Marker(e.Tick, MarkerBrush(e.Kind)))];
            OnPropertyChanged(nameof(Markers));
            RebuildChat(force: false);
            RefreshDreams();
        }
    }

    private void UpdateFrame()
    {
        Frame = History.At(CursorTick);
        if (Frame is { } f)
        {
            var dark = _clockShape.InDarkWindow(f.Phi) ? "Night" : "Day";
            ClockText = $"{f.Time:ddd HH:mm:ss}  {dark}  {(_host.Clock.Simulated && !IsReplayFile ? SelectedClockMode.Label : "")}".TrimEnd();
            PhaseText = f.Phase == "Wake" ? "Awake" : $"Asleep: {f.Phase}, cycle {f.Cycle + 1}";
            CursorText = $"tick {f.Tick:N0}";
            var next = Sampling.Compute(f.C, _config.Sampling);
            SamplingText = $"T {next.Temperature:0.00}   top-p {next.TopP:0.00}   max {next.MaxTokens} tokens";
            for (var k = 0; k < Channels.Count && k < f.M.Length; k++)
            {
                Channels[k].Value = f.M[k];
            }

            foreach (var row in SubstanceRows)
            {
                var i = (int)row.Substance;
                row.Value = f.C[i];
                row.Rho = f.Rho[i];
                row.Status = ReceptorStatus(f.C[i], f.Rho[i]);
            }
        }

        FrameChanged?.Invoke();
    }

    /// <summary>Tolerance and sensitization from rho; withdrawal is tolerance with the substance back at baseline.</summary>
    private static string ReceptorStatus(double c, double rho) => rho switch
    {
        > 1.1 => "sensitized",
        < 0.9 when c > 1.05 => "tolerant",
        < 0.9 => "withdrawal",
        _ => "normal",
    };

    private void AppendToken(string token)
    {
        if (_streaming is null)
        {
            return;
        }

        _streaming += token;
        if (IsLive && Chat.Count > 0 && Chat[^1].Role == EventKinds.Agent && Chat[^1].Detail == "streaming")
        {
            Chat[^1].Text = _streaming;
        }
    }

    /// <summary>The chat is rebuilt from the events journal, up to the cursor, plus the reply that is streaming now.</summary>
    private void RebuildChat(bool force)
    {
        var events = Events.Snapshot();
        var cursor = IsLive ? long.MaxValue : CursorTick;
        if (!force && events.Count == _chatEventCount && cursor == _chatCursor)
        {
            return;
        }

        _chatEventCount = events.Count;
        _chatCursor = cursor;
        Chat.Clear();

        // Messages from earlier sessions come from the checkpoint; they have no appraisal details.
        if (!IsReplayFile)
        {
            foreach (var m in _host.EarlierMessages.TakeLast(40))
            {
                Chat.Add(new ChatItem(m.Role == "user" ? EventKinds.User : EventKinds.Agent, m.Content, "earlier session"));
            }
        }

        foreach (var e in events.Where(e => e.Tick <= cursor))
        {
            switch (e.Kind)
            {
                case EventKinds.User:
                    Chat.Add(new ChatItem(e.Kind, e.Text, AppraisalDetail(e.Values)));
                    break;
                case EventKinds.Agent:
                    Chat.Add(new ChatItem(e.Kind, e.Text, SamplingDetail(e.Values)));
                    LastSamplingText = SamplingDetail(e.Values);
                    break;
                case EventKinds.Error:
                    Chat.Add(new ChatItem(e.Kind, e.Text, ""));
                    break;
            }
        }

        if (IsLive && _streaming is not null)
        {
            Chat.Add(new ChatItem(EventKinds.Agent, _streaming, "streaming"));
        }
    }

    private static string AppraisalDetail(Dictionary<string, double>? v) => v is null ? "" :
        $"S {Get(v, "S")}   A {Get(v, "A")}   W {Get(v, "W")}   r {Get(v, "r")}   delta {Get(v, "delta")}";

    private static string SamplingDetail(Dictionary<string, double>? v) => v is null ? "" :
        $"T {Get(v, "T")}   top-p {Get(v, "top_p")}   max {Get(v, "max_tokens", "0")}   {Get(v, "tokens", "0")} tokens";

    private static string Get(Dictionary<string, double> v, string key, string format = "0.00") =>
        v.TryGetValue(key, out var x) ? x.ToString(format, Invariant) : "n/a";

    private static Brush MarkerBrush(string kind) => kind switch
    {
        EventKinds.User => Palette.Brush(Palette.Text),
        EventKinds.Lab => Palette.Brush(Palette.Warn),
        _ => Palette.Brush(Palette.Hex(Substance.Melatonin)),
    };

    private async Task PingLmStudioAsync()
    {
        using var http = new HttpClient { BaseAddress = new Uri(_config.Llm.BaseUrl), Timeout = TimeSpan.FromSeconds(3) };
        while (true)
        {
            try
            {
                using var response = await http.GetAsync("models");
                LmStudioOk = response.IsSuccessStatusCode;
            }
            catch (Exception)
            {
                LmStudioOk = false;
            }

            LmStudioText = LmStudioOk ? $"LM Studio: {_config.Llm.ChatModel}" : "LM Studio: not reachable";
            await Task.Delay(TimeSpan.FromSeconds(5));
        }
    }

    internal static bool TryParse(string text, out double value) =>
        double.TryParse(text.Replace(',', '.'), NumberStyles.Float, Invariant, out value) && double.IsFinite(value);
}

/// <summary>One row per substance: current level, receptor state and the Lab controls.</summary>
public sealed partial class SubstanceRow : ObservableObject
{
    private readonly MainViewModel _owner;
    private readonly double _configHalfLife;

    public SubstanceRow(Substance substance, MainViewModel owner)
    {
        Substance = substance;
        _owner = owner;
        _configHalfLife = owner.Config.HalfLives()[(int)substance];
        _halfLife = _configHalfLife.ToString("0", CultureInfo.InvariantCulture);
    }

    public Substance Substance { get; }
    public string Name => Substance.Abbreviation();
    public Brush Brush => Palette.Brush(Substance);

    [ObservableProperty] private double _value = 1;
    [ObservableProperty] private double _rho = 1;
    [ObservableProperty] private string _status = "normal";
    [ObservableProperty] private bool _shown = true;
    [ObservableProperty] private string _amount = "0.5";
    [ObservableProperty] private bool _holding;
    [ObservableProperty] private string _holdLevel = "1.00";
    [ObservableProperty] private string _halfLife;
    [ObservableProperty] private string _baseline = "1.00";
    [ObservableProperty] private string _error = "";

    private bool _resetting;

    partial void OnHoldingChanged(bool value)
    {
        if (!_resetting)
        {
            ApplyHold();
        }
    }

    [RelayCommand]
    private void Inject()
    {
        if (MainViewModel.TryParse(Amount, out var amount) && Math.Abs(amount) <= 5)
        {
            Error = "";
            _owner.Inject(Substance, amount);
        }
        else
        {
            Error = "Amount must be a number between -5 and 5";
        }
    }

    [RelayCommand]
    private void ApplyHold()
    {
        if (!Holding)
        {
            _owner.Hold(Substance, null);
            Error = "";
        }
        else if (MainViewModel.TryParse(HoldLevel, out var level) && level is >= 0 and <= 5)
        {
            _owner.Hold(Substance, level);
            Error = "";
        }
        else
        {
            Error = "Hold level must be between 0 and 5";
        }
    }

    [RelayCommand]
    private void ApplyBaseline()
    {
        if (MainViewModel.TryParse(Baseline, out var level) && level is >= 0.1 and <= 4)
        {
            Error = "";
            _owner.SetBaseline(Substance, level);
        }
        else
        {
            Error = "Baseline must be between 0.1 and 4";
        }
    }

    [RelayCommand]
    private void ApplyHalfLife()
    {
        if (MainViewModel.TryParse(HalfLife, out var t) && t >= 1)
        {
            Error = "";
            _owner.SetHalfLife(Substance, t);
        }
        else
        {
            Error = "Half-life must be at least 1 tick";
        }
    }

    public void Reset()
    {
        _resetting = true;
        Holding = false; // the engine reset already released every hold
        _resetting = false;
        HalfLife = _configHalfLife.ToString("0", CultureInfo.InvariantCulture);
        Baseline = "1.00";
        Error = "";
    }
}

/// <summary>One cell of the W heatmap: the effect of the row substance on the column substance, per tick.</summary>
public sealed partial class WCell : ObservableObject
{
    private readonly MainViewModel _owner;
    private readonly double _configValue;
    private double _applied;
    private bool _resetting;

    public WCell(Substance from, Substance to, double w, MainViewModel owner)
    {
        From = from;
        To = to;
        _owner = owner;
        _configValue = w;
        _applied = w;
        _text = Format(w);
    }

    public Substance From { get; }
    public Substance To { get; }
    public string Tip => $"{From.Abbreviation()} on {To.Abbreviation()}. Empty means 0. Edit, then press Tab to apply.";

    [ObservableProperty] private string _text;
    [ObservableProperty] private bool _invalid;

    /// <summary>Green for excitation, red for inhibition, stronger color for a larger effect.</summary>
    public Brush Background => _applied == 0 ? Brushes.Transparent : Heat(_applied);

    partial void OnTextChanged(string value)
    {
        if (_resetting)
        {
            return;
        }

        var w = 0.0;
        Invalid = value.Trim().Length > 0 && !(MainViewModel.TryParse(value, out w) && Math.Abs(w) <= 0.1);
        if (!Invalid && w != _applied)
        {
            _applied = w;
            _owner.SetInteraction(From, To, w);
            OnPropertyChanged(nameof(Background));
        }
    }

    public void Reset()
    {
        _resetting = true;
        _applied = _configValue;
        Text = Format(_configValue);
        Invalid = false;
        _resetting = false;
        OnPropertyChanged(nameof(Background));
    }

    private static string Format(double w) => w == 0 ? "" : w.ToString("0.#####", CultureInfo.InvariantCulture);

    private static SolidColorBrush Heat(double w)
    {
        var strength = Math.Clamp(Math.Abs(w) / 0.01, 0.15, 1.0);
        var color = (Color)ColorConverter.ConvertFromString(w > 0 ? "#34D399" : "#EF4444");
        var brush = new SolidColorBrush(Color.FromArgb((byte)(40 + 160 * strength), color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }
}

/// <summary>One appraisal gain: how much of a substance one unit of a signal releases.</summary>
public sealed partial class GainRow(string signal, Substance substance, double gain, MainViewModel owner) : ObservableObject
{
    public string Signal { get; } = signal;
    public string SubstanceName => substance.Abbreviation();
    public Brush Brush => Palette.Brush(substance);

    [ObservableProperty] private string _gain = gain.ToString("0.###", CultureInfo.InvariantCulture);
    [ObservableProperty] private string _error = "";

    [RelayCommand]
    private void Apply()
    {
        if (MainViewModel.TryParse(Gain, out var value) && Math.Abs(value) <= 10)
        {
            Error = "";
            owner.SetGain(Signal, substance, value);
        }
        else
        {
            Error = "Gain must be between -10 and 10";
        }
    }

    public void Reset()
    {
        Gain = gain.ToString("0.###", CultureInfo.InvariantCulture);
        Error = "";
    }
}

/// <summary>A modulation channel m_k, shown as a bipolar bar.</summary>
public sealed partial class ChannelRow(string name) : ObservableObject
{
    public string Name { get; } = name;

    public string Label => Name.Replace('_', ' ');

    /// <summary>Steering and plasticity are computed but not applied in reduced mode (D-03).</summary>
    public string Note => Name.StartsWith("memory", StringComparison.Ordinal) ? "exported to memory (E4)" : "computed, not applied";

    [ObservableProperty] private double _value;
}
