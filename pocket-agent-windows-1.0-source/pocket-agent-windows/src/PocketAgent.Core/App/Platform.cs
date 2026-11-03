using PocketAgent.Core.Settings;
using PocketAgent.Core.Voice;

namespace PocketAgent.Core.App;

/// <summary>Sends JSON messages to the HTML interface. Must be callable from any thread.</summary>
public interface IUiChannel
{
    void Post(string json);
}

public sealed record PromptChoice(string Id, string Label, string Style = "default");

public sealed record PromptView(
    string Id,
    string Kind,
    string Title,
    string Detail,
    string? AppName,
    IReadOnlyList<PromptChoice> Choices,
    long ExpiresAt);

/// <summary>What the floating voice bubble should show.</summary>
public sealed record OverlayView
{
    /// <summary>idle, listening, transcribing, thinking, acting, speaking, paused, prompt or notice.</summary>
    public string Mode { get; init; } = "idle";
    public string Title { get; init; } = "";
    public string? Caption { get; init; }
    public PromptView? Prompt { get; init; }
    public bool TaskRunning { get; init; }
    public bool Paused { get; init; }
    public bool ControlActive { get; init; }
    public string TalkHint { get; init; } = "";
    /// <summary>always, active or never.</summary>
    public string Visibility { get; init; } = "always";
}

/// <summary>The floating bubble and the click-preview ring.</summary>
public interface IOverlay
{
    void Show(OverlayView view);
    void SetLevel(double level);
    /// <summary>Shows the ring at a physical screen point and blocks the caller for <paramref name="durationMs"/>.</summary>
    void PreviewClick(int x, int y, int durationMs);
    bool Covers(int x, int y);
    void HideFor(int milliseconds);
    /// <summary>talk, pause, resume, stop, open, control or answer:&lt;promptId&gt;:&lt;choice&gt;.</summary>
    event Action<string>? Command;
}

public sealed record VoiceInfo(string Id, string Name, string Language, string Gender);

public sealed class PlatformInfo
{
    public string OsDescription { get; init; } = "";
    public string MachineSummary { get; init; } = "";
    public List<VoiceInfo> Voices { get; init; } = new();
    public List<string> RecognizerLanguages { get; init; } = new();
    public bool LiveRecognizerAvailable { get; init; }
    public string? LiveRecognizerError { get; init; }
    public bool WakeWordAvailable { get; init; }
    public string? WakeWordError { get; init; }
    public bool MicrophoneAvailable { get; init; }
    public bool McpLibraryAvailable { get; init; } = true;
}

/// <summary>Everything the controller needs from the operating system.</summary>
public interface IPlatform
{
    IUiChannel Ui { get; }
    IOverlay Overlay { get; }
    IAudioRecorder Recorder { get; }
    IAudioPlayer Player { get; }
    ILiveSpeechRecognizer? LiveRecognizer { get; }
    ITextToSpeech? NativeTts { get; }
    IWakeWordListener? WakeWord { get; }
    ISecretProtector Protector { get; }
    int OwnProcessId { get; }
    string DataFolder { get; }
    string AppVersion { get; }
    bool MainWindowVisible { get; }

    PlatformInfo Describe();
    void SetClipboard(string text);
    void OpenExternal(string url);
    void Notify(string title, string message);
    void ShowMainWindow();
    void ApplyAppearance(string theme);
    void SetStartWithWindows(bool enabled);
    void ConfigureInput(string talkKey, string talkMode, bool takeoverDetection, bool tripleEsc);
    void ConfigureNativeVoice(string voiceId, double rate);
    /// <summary>Asks where to save; returns the path or null when cancelled.</summary>
    string? SaveTextFile(string suggestedName, string content);
    /// <summary>listen, done or error.</summary>
    void PlayCue(string cue);
    void Quit();
}
