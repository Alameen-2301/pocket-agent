using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using PocketAgent.Core.App;
using PocketAgent.Core.Settings;
using PocketAgent.Core.Tools;
using PocketAgent.Core.Voice;

namespace PocketAgent.Core.Tests;

public sealed class FakeUi : IUiChannel
{
    public ConcurrentQueue<JsonObject> Messages { get; } = new();
    public void Post(string json) => Messages.Enqueue(JsonNode.Parse(json)!.AsObject());
    public IEnumerable<JsonNode> Of(string type) => Messages.Where(m => m["type"]!.GetValue<string>() == type).Select(m => m["data"]!);
    public JsonNode? LastState => Of("state").LastOrDefault();
    public string AllJson => string.Join("\n", Messages.Select(m => m.ToJsonString()));
}

public sealed class FakeOverlay : IOverlay
{
    public OverlayView? Last { get; private set; }
    public ConcurrentQueue<(int X, int Y)> Previews { get; } = new();
    public event Action<string>? Command;
    public void Show(OverlayView view) => Last = view;
    public void SetLevel(double level) { }
    public void PreviewClick(int x, int y, int durationMs) => Previews.Enqueue((x, y));
    public bool Covers(int x, int y) => false;
    public void HideFor(int milliseconds) { }
    public void Fire(string command) => Command?.Invoke(command);
}

public sealed class FakeRecorder : IAudioRecorder
{
    private Action<short[]>? _onFrame;
    private readonly List<short> _captured = new();
    public bool IsRecording { get; private set; }
    public int SampleRate => 16000;
    public int Starts { get; private set; }
    public void Start(Action<short[]> onFrame)
    {
        _onFrame = onFrame;
        _captured.Clear();
        IsRecording = true;
        Starts++;
    }
    public void Feed(int milliseconds, double amplitude = 0.2)
    {
        var samples = new short[16 * milliseconds];
        for (int i = 0; i < samples.Length; i++) samples[i] = (short)(Math.Sin(i * 0.09) * amplitude * 32767);
        lock (_captured) _captured.AddRange(samples);
        _onFrame?.Invoke(samples);
    }
    public byte[] Stop()
    {
        IsRecording = false;
        _onFrame = null;
        byte[] pcm;
        lock (_captured)
        {
            pcm = new byte[_captured.Count * 2];
            Buffer.BlockCopy(_captured.ToArray(), 0, pcm, 0, pcm.Length);
        }
        return Wav.FromPcm16(pcm, 16000);
    }
}

public sealed class FakePlayer : IAudioPlayer
{
    public ConcurrentQueue<byte[]> Played { get; } = new();
    public int Stops;
    public bool IsPlaying { get; private set; }
    public async Task PlayAsync(byte[] wav, CancellationToken ct)
    {
        IsPlaying = true;
        Played.Enqueue(wav);
        try { await Task.Delay(30, ct); }
        catch (OperationCanceledException) { }
        IsPlaying = false;
    }
    public void Stop() { Interlocked.Increment(ref Stops); IsPlaying = false; }
}

public sealed class FakeLive : ILiveSpeechRecognizer
{
    public string NextText { get; set; } = "";
    public int Starts;
    public bool IsAvailable => true;
    public string? UnavailableReason => null;
    public Task StartAsync(string language, Action<string> onPartial, CancellationToken ct)
    {
        Interlocked.Increment(ref Starts);
        onPartial("…");
        return Task.CompletedTask;
    }
    public Task<string> StopAsync() => Task.FromResult(NextText);
    public void Cancel() { }
}

public sealed class FakeTts : ITextToSpeech
{
    public ConcurrentQueue<string> Spoken { get; } = new();
    public Task<byte[]> SynthesizeAsync(string text, string language, CancellationToken ct)
    {
        Spoken.Enqueue(text);
        return Task.FromResult(Wav.FromPcm16(new byte[320], 16000));
    }
}

public sealed class FakeWake : IWakeWordListener
{
    public bool IsAvailable => true;
    public string? UnavailableReason => null;
    public bool WakeWord, Commands, Suspended;
    public event Action? WakeWordHeard;
    public event Action<VoiceCommand>? CommandHeard;
    public void Configure(bool wakeWord, bool commands, string language) { WakeWord = wakeWord; Commands = commands; }
    public void SetSuspended(bool suspended) => Suspended = suspended;
    public void Wake() => WakeWordHeard?.Invoke();
    public void Say(VoiceCommand c) => CommandHeard?.Invoke(c);
}

public sealed class FakeTools : IToolExecutor
{
    public IToolServices? Services { get; set; }
    public Func<IToolServices, string, JsonElement, ToolOutcome> Handler { get; set; } = (s, n, a) => ToolOutcome.Ok(n + " ok");
    public ConcurrentQueue<string> Calls { get; } = new();
    public ToolOutcome Execute(string name, JsonElement args)
    {
        Calls.Enqueue(name);
        return Handler(Services!, name, args);
    }
}

public sealed class FakePlatform : IPlatform
{
    public FakeUi UiFake { get; } = new();
    public FakeOverlay OverlayFake { get; } = new();
    public FakeRecorder RecorderFake { get; } = new();
    public FakePlayer PlayerFake { get; } = new();
    public FakeLive LiveFake { get; } = new();
    public FakeTts TtsFake { get; } = new();
    public FakeWake WakeFake { get; } = new();
    public string Clipboard { get; private set; } = "";
    public ConcurrentQueue<string> Notifications { get; } = new();
    public ConcurrentQueue<string> Cues { get; } = new();
    public string InputConfig { get; private set; } = "";

    public FakePlatform(string folder) { DataFolder = folder; }

    public IUiChannel Ui => UiFake;
    public IOverlay Overlay => OverlayFake;
    public IAudioRecorder Recorder => RecorderFake;
    public IAudioPlayer Player => PlayerFake;
    public ILiveSpeechRecognizer? LiveRecognizer => LiveFake;
    public ITextToSpeech? NativeTts => TtsFake;
    public IWakeWordListener? WakeWord => WakeFake;
    public ISecretProtector Protector { get; } = new FakeProtector();
    public int OwnProcessId => Environment.ProcessId;
    public string DataFolder { get; }
    public string AppVersion => "1.0.0-test";
    public bool MainWindowVisible { get; set; } = true;
    public PlatformInfo Describe() => new() { OsDescription = "Test OS", MachineSummary = "Test PC, 1 monitor", MicrophoneAvailable = true };
    public void SetClipboard(string text) => Clipboard = text;
    public void OpenExternal(string url) { }
    public void Notify(string title, string message) => Notifications.Enqueue(message);
    public void ShowMainWindow() { }
    public void ApplyAppearance(string theme) { }
    public void SetStartWithWindows(bool enabled) { }
    public void ConfigureInput(string talkKey, string talkMode, bool takeoverDetection, bool tripleEsc) => InputConfig = $"{talkKey}/{talkMode}/{takeoverDetection}/{tripleEsc}";
    public void ConfigureNativeVoice(string voiceId, double rate) { }
    public string? SaveTextFile(string suggestedName, string content) => null;
    public void PlayCue(string cue) => Cues.Enqueue(cue);
    public void Quit() { }
}
