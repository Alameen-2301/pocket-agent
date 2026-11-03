using System.Text.Json;
using System.Text.Json.Nodes;
using PocketAgent.Core.Agent;
using PocketAgent.Core.Mcp;
using PocketAgent.Core.Providers;
using PocketAgent.Core.Safety;
using PocketAgent.Core.Settings;
using PocketAgent.Core.Tools;
using PocketAgent.Core.Tunnel;
using PocketAgent.Core.Util;

namespace PocketAgent.Core.App;

public sealed record ChatEntry(string Id, string Role, string Text, long Time, string? RunId, string? Source);

public sealed record LogEntry(long Time, string Category, string Message);

/// <summary>
/// The app's brain, independent of Windows: desktop control on/off, approvals and safety prompts,
/// the built-in assistant, voice, the MCP server and tunnel, and the messages to the UI and bubble.
/// </summary>
public sealed partial class AppController : IToolServices, IMcpHost, IAgentObserver, IAsyncDisposable
{
    private readonly IPlatform _p;
    private readonly SettingsStore _settings;
    private readonly ApprovalStore _approvals;
    private readonly IToolExecutor _tools;
    private readonly TunnelManager _tunnel;
    private readonly AgentSession _agent;
    private readonly SemaphoreSlim _toolGate = new(1, 1);
    private readonly object _sync = new();
    private readonly Timer _tick;
    private readonly Timer _pushTimer;
    private int _pushScheduled;
    private RiskPolicy _risk;
    private bool _disposed;

    private readonly List<ChatEntry> _chat = new();
    private readonly List<AgentStep> _steps = new();
    private readonly Dictionary<string, AgentStep> _stepsById = new();
    private readonly LinkedList<LogEntry> _logs = new();
    private PlatformInfo _platformInfo = new();

    public AppController(IPlatform platform, SettingsStore settings, ApprovalStore approvals, Func<IToolServices, IToolExecutor> toolFactory)
    {
        _p = platform;
        _settings = settings;
        _approvals = approvals;
        _risk = RiskPolicy.FromSetting(settings.Current.RiskyPhrases);
        _tools = toolFactory(this);
        _tunnel = new TunnelManager(Log);
        _tunnel.Changed += RequestPush;
        _approvals.Changed += RequestPush;
        _settings.Changed += OnSettingsChanged;
        _agent = new AgentSession(new AgentOptions
        {
            ProviderFactory = CreateProvider,
            SystemPromptFactory = BuildSystemPrompt,
            Tools = ToolCatalog.All,
            RunTool = RunToolForAgentAsync,
            MaxSteps = () => _settings.Current.MaxSteps,
            KeepScreenshots = () => _settings.Current.KeepScreenshots,
            MaxOutputTokens = () => _settings.Current.MaxOutputTokens,
        }, this);
        _tick = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
        _pushTimer = new Timer(_ => PushNow(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public SettingsStore Settings => _settings;
    public ApprovalStore Approvals => _approvals;
    public AgentSession Agent => _agent;
    public TunnelManager Tunnel => _tunnel;

    /// <summary>Call once after the platform is ready.</summary>
    public void Initialize()
    {
        _platformInfo = SafeDescribe();
        _p.Overlay.Command += OnOverlayCommand;
        if (_p.WakeWord is { } wake)
        {
            wake.WakeWordHeard += OnWakeWord;
            wake.CommandHeard += OnVoiceCommandHeard;
        }
        var s = _settings.Current;
        ApplyPlatformSettings(s);
        _lastSettings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(s, Json.Options), Json.Options) ?? new AppSettings();
        TryPlatform(() => _p.SetStartWithWindows(s.StartWithWindows));
        StartSpeechWorker();
        _tick.Change(1000, 1000);
        Log("system", "Ready. Desktop control is off until you start it or ask the assistant to do something.");
        RequestPush();
    }

    private PlatformInfo SafeDescribe()
    {
        try { return _p.Describe(); }
        catch (Exception e)
        {
            Log("error", "Could not read system details: " + e.Message);
            return new PlatformInfo();
        }
    }

    private void ApplyPlatformSettings(AppSettings s)
    {
        TryPlatform(() => _p.ApplyAppearance(s.Theme));
        TryPlatform(() => _p.ConfigureInput(s.TalkKey, s.TalkMode, s.PauseOnUserInput, s.TripleEscStop));
        TryPlatform(() => _p.ConfigureNativeVoice(s.WindowsVoice, s.SpeechRate));
        ConfigureWakeWord(s);
    }

    private void TryPlatform(Action a)
    {
        try { a(); } catch (Exception e) { Log("error", e.Message); }
    }

    private AppSettings _lastSettings = new();

    private void OnSettingsChanged()
    {
        var s = _settings.Current;
        _risk = RiskPolicy.FromSetting(s.RiskyPhrases);
        ApplyPlatformSettings(s);
        lock (_sync)
        {
            if (_controlActive)
                _expiresAt = s.AutoStopMinutes > 0 ? _startedAt.AddMinutes(s.AutoStopMinutes) : null;
            if (_expiresAt is { } e && e < DateTimeOffset.Now.AddSeconds(30)) _expiresAt = DateTimeOffset.Now.AddSeconds(30);
        }
        if (s.StartWithWindows != _lastSettings.StartWithWindows) TryPlatform(() => _p.SetStartWithWindows(s.StartWithWindows));
        _lastSettings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(s, Json.Options), Json.Options) ?? new AppSettings();
        RequestPush();
    }

    private void Tick()
    {
        if (_disposed) return;
        DateTimeOffset? expires;
        bool active;
        lock (_sync) { expires = _expiresAt; active = _controlActive; }
        if (active && expires is { } e && DateTimeOffset.Now >= e)
        {
            _ = StopControlAsync("Auto-stop timer ended");
            _p.Notify("Pocket Agent", "Desktop control switched off automatically.");
        }
        ExpirePrompts();
        CheckListeningTimeout();
        if (_noticeUntil != default && DateTimeOffset.Now >= _noticeUntil)
        {
            _noticeUntil = default;
            _notice = null;
            RequestPush();
        }
    }

    // ---------------------------------------------------------------- logging and activity

    public void Log(string category, string message)
    {
        var entry = new LogEntry(DateTimeOffset.Now.ToUnixTimeMilliseconds(), category, message);
        lock (_logs)
        {
            _logs.AddLast(entry);
            while (_logs.Count > 1000) _logs.RemoveFirst();
        }
        Post("log", entry);
    }

    void IMcpHost.Log(string category, string message) => Log(category, message);
    void IToolServices.Log(string category, string message) => Log(category, message);

    private ChatEntry AddChat(string role, string text, string? source = null, string? runId = null)
    {
        var entry = new ChatEntry(Ids.New("msg"), role, text, DateTimeOffset.Now.ToUnixTimeMilliseconds(), runId, source);
        lock (_chat)
        {
            _chat.Add(entry);
            if (_chat.Count > 500) _chat.RemoveRange(0, _chat.Count - 500);
        }
        Post("chat", entry);
        return entry;
    }

    private void UpsertStep(AgentStep step)
    {
        lock (_steps)
        {
            if (!_stepsById.ContainsKey(step.Id))
            {
                _steps.Add(step);
                _stepsById[step.Id] = step;
                if (_steps.Count > 2000)
                {
                    foreach (var old in _steps.Take(_steps.Count - 2000)) _stepsById.Remove(old.Id);
                    _steps.RemoveRange(0, _steps.Count - 2000);
                }
            }
        }
        Post("step", StepDto(step));
    }

    private static object StepDto(AgentStep s) => new
    {
        s.Id, s.RunId, s.Index, s.Tool, s.Title, s.Source, s.Status, s.Detail,
        Thumb = s.Thumbnail,
        Time = s.Started.ToUnixTimeMilliseconds(),
        Ms = s.DurationMs,
    };

    public string ActivityText()
    {
        var lines = new List<string>();
        lock (_logs)
            foreach (var l in _logs)
                lines.Add($"{DateTimeOffset.FromUnixTimeMilliseconds(l.Time).ToLocalTime():yyyy-MM-dd HH:mm:ss}  [{l.Category}]  {l.Message}");
        return string.Join(Environment.NewLine, lines);
    }

    // ---------------------------------------------------------------- UI messages

    private void Post(string type, object data)
    {
        try
        {
            var json = JsonSerializer.Serialize(new { type, data }, Json.Options);
            _p.Ui.Post(json);
        }
        catch (Exception e) { System.Diagnostics.Debug.WriteLine("UI post failed: " + e); }
    }

    /// <summary>Coalesces many changes into one state message (and bubble update) ~50 ms later.</summary>
    public void RequestPush()
    {
        if (_disposed) return;
        if (Interlocked.Exchange(ref _pushScheduled, 1) == 0) _pushTimer.Change(50, Timeout.Infinite);
    }

    private void PushNow()
    {
        Interlocked.Exchange(ref _pushScheduled, 0);
        if (_disposed) return;
        try
        {
            Post("state", BuildState());
            _p.Overlay.Show(BuildOverlayView());
        }
        catch (Exception e) { System.Diagnostics.Debug.WriteLine("Push failed: " + e); }
    }

    private void SendHistory()
    {
        object[] steps;
        lock (_steps) steps = _steps.TakeLast(400).Select(StepDto).ToArray();
        ChatEntry[] chat;
        lock (_chat) chat = _chat.ToArray();
        LogEntry[] logs;
        lock (_logs) logs = _logs.TakeLast(400).ToArray();
        Post("history", new { chat, steps, logs });
    }

    // ---------------------------------------------------------------- assistant plumbing

    private IChatProvider CreateProvider()
    {
        var s = _settings.Current;
        var options = ProviderPresets.Resolve(s, _settings);
        var reason = ProviderPresets.NotReadyReason(options, ProviderPresets.Get(options.Id));
        if (reason is not null) throw new ProviderException(reason);
        return ProviderPresets.Create(options);
    }

    private string? AssistantNotReadyReason()
    {
        var options = ProviderPresets.Resolve(_settings.Current, _settings);
        return ProviderPresets.NotReadyReason(options, ProviderPresets.Get(options.Id));
    }

    private string BuildSystemPrompt()
    {
        var s = _settings.Current;
        return SystemPrompt.Build(new PromptContext(
            DateTimeOffset.Now,
            _platformInfo.MachineSummary.Length > 0 ? _platformInfo.MachineSummary : _platformInfo.OsDescription,
            ControlActive,
            s.SpeakReplies && s.TtsEngine != "off",
            s.Language,
            s.CustomInstructions));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _agent.Stop();
        try { await StopControlAsync("Closing").ConfigureAwait(false); } catch { }
        _disposed = true;
        _tick.Dispose();
        _pushTimer.Dispose();
        CancelSpeech();
        _speechQueue.Writer.TryComplete();
        _tunnel.Dispose();
    }
}
