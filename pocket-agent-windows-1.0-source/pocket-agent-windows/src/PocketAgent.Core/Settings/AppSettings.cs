using System.Text.Json;
using System.Text.Json.Nodes;
using PocketAgent.Core.Safety;
using PocketAgent.Core.Util;

namespace PocketAgent.Core.Settings;

/// <summary>Encrypts secrets at rest. Windows uses DPAPI (current user); tests use a fake.</summary>
public interface ISecretProtector
{
    string Protect(string plain);
    string Unprotect(string protectedValue);
}

public sealed class ProviderConfig
{
    public string BaseUrl { get; set; } = "";
    public string Model { get; set; } = "";
    /// <summary>Encrypted API key (never sent to the UI).</summary>
    public string ApiKeyProtected { get; set; } = "";
    /// <summary>Optional override: "anthropic", "openai-chat" or "openai-responses".</summary>
    public string ApiStyle { get; set; } = "";
    /// <summary>Optional prices in US$ per million tokens, to show a cost estimate.</summary>
    public double? PriceInput { get; set; }
    public double? PriceOutput { get; set; }
}

public sealed class SpeechServiceConfig
{
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    public string Model { get; set; } = "";
    public string Voice { get; set; } = "";
    public string ApiKeyProtected { get; set; } = "";
    /// <summary>Reuse the API key of the matching AI provider preset when no own key is set.</summary>
    public bool UseProviderKey { get; set; } = true;
}

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;
    public bool SetupComplete { get; set; }

    // AI model
    public string ProviderId { get; set; } = "anthropic";
    public Dictionary<string, ProviderConfig> Providers { get; set; } = new();
    /// <summary>0 = no step limit: keep working until the task is done or the user stops it.</summary>
    public int MaxSteps { get; set; }
    public int KeepScreenshots { get; set; } = 3;
    public int MaxOutputTokens { get; set; } = 4096;
    public string CustomInstructions { get; set; } = "";

    // Voice
    public string TalkKey { get; set; } = "RightCtrl";
    public string TalkMode { get; set; } = "hold";
    public bool WakeWordEnabled { get; set; }
    public bool VoiceCommandsEnabled { get; set; } = true;
    public string SttEngine { get; set; } = "windows";
    public SpeechServiceConfig Transcription { get; set; } = new() { Model = "gpt-transcribe" };
    public string Language { get; set; } = "auto";
    public string TtsEngine { get; set; } = "windows";
    public string WindowsVoice { get; set; } = "";
    public double SpeechRate { get; set; } = 1.0;
    public SpeechServiceConfig Speech { get; set; } = new() { Model = "gpt-4o-mini-tts", Voice = "alloy" };
    public bool SpeakReplies { get; set; } = true;
    public bool NarrateSteps { get; set; } = true;

    // Safety
    /// <summary>Minutes until desktop control switches itself off; 0 = until I stop it.</summary>
    public int AutoStopMinutes { get; set; } = 20;
    public bool ConfirmRiskySteps { get; set; } = true;
    public string RiskyPhrases { get; set; } = RiskPolicy.DefaultSetting;
    public bool ClickPreview { get; set; } = true;
    public int ClickPreviewMs { get; set; } = 350;
    public bool PauseOnUserInput { get; set; } = true;
    public bool TripleEscStop { get; set; } = true;
    public bool AutoStartControlOnTalk { get; set; } = true;
    public int ApprovalTimeoutSeconds { get; set; } = 60;

    // Connections (MCP for other assistants)
    public int Port { get; set; } = 8765;
    public bool McpServerEnabled { get; set; } = true;
    public bool ReuseSecret { get; set; }
    public string SecretProtected { get; set; } = "";
    public string TunnelMode { get; set; } = "off";
    public string CustomTunnelUrl { get; set; } = "";
    public string CloudflaredPath { get; set; } = "";

    // Appearance and behaviour
    public string Theme { get; set; } = "system";
    public string BubbleMode { get; set; } = "always";
    public double? BubbleLeft { get; set; }
    public double? BubbleTop { get; set; }
    public bool StartWithWindows { get; set; }
    public bool CloseToTray { get; set; } = true;
    public bool TrayHintShown { get; set; }

    public ProviderConfig ProviderConfigFor(string id)
    {
        if (!Providers.TryGetValue(id, out var c))
        {
            c = new ProviderConfig();
            Providers[id] = c;
        }
        return c;
    }

    /// <summary>Clamp values that came from disk or the UI into safe ranges.</summary>
    public void Normalize()
    {
        Providers ??= new();
        Transcription ??= new() { Model = "gpt-transcribe" };
        Speech ??= new() { Model = "gpt-4o-mini-tts", Voice = "alloy" };
        MaxSteps = Math.Clamp(MaxSteps, 0, 100000);
        KeepScreenshots = Math.Clamp(KeepScreenshots, 1, 10);
        MaxOutputTokens = Math.Clamp(MaxOutputTokens, 256, 64000);
        AutoStopMinutes = Math.Clamp(AutoStopMinutes, 0, 24 * 60);
        ClickPreviewMs = Math.Clamp(ClickPreviewMs, 0, 1500);
        ApprovalTimeoutSeconds = Math.Clamp(ApprovalTimeoutSeconds, 15, 600);
        SpeechRate = Math.Clamp(SpeechRate, 0.5, 2.0);
        if (Port is < 1024 or > 65535) Port = 8765;
        if (TalkMode is not ("hold" or "toggle")) TalkMode = "hold";
        if (SttEngine is not ("windows" or "whisper")) SttEngine = "windows";
        if (TtsEngine is not ("windows" or "openai" or "off")) TtsEngine = "windows";
        if (TunnelMode is not ("off" or "cloudflare" or "custom")) TunnelMode = "off";
        if (Theme is not ("system" or "light" or "dark")) Theme = "system";
        if (BubbleMode is not ("always" or "active" or "never")) BubbleMode = "always";
        if (!TalkKeys.IsKnown(TalkKey)) TalkKey = "RightCtrl";
        RiskyPhrases ??= RiskPolicy.DefaultSetting;
        CustomInstructions ??= "";
        if (CustomInstructions.Length > 4000) CustomInstructions = CustomInstructions[..4000];
    }
}

/// <summary>Talk-key presets. Kept here so the UI, controller and input monitor agree.</summary>
public static class TalkKeys
{
    public sealed record Preset(string Id, string Label, string Hint);

    public static readonly Preset[] All =
    [
        new("RightCtrl", "Right Ctrl", "Hold the right Ctrl key on its own"),
        new("CtrlSpace", "Ctrl + Space", "May clash with code editors"),
        new("CtrlShiftSpace", "Ctrl + Shift + Space", "Rarely used by other apps"),
        new("CtrlAltSpace", "Ctrl + Alt + Space", "Uses the left Alt key"),
        new("F8", "F8", "Single function key"),
        new("F9", "F9", "Single function key"),
        new("Pause", "Pause/Break", "Desktop keyboards"),
    ];

    public static bool IsKnown(string? id) => All.Any(p => p.Id == id);
    public static string Label(string id) => All.FirstOrDefault(p => p.Id == id)?.Label ?? id;
}

public sealed class SettingsStore
{
    private readonly string _path;
    private readonly ISecretProtector _protector;
    private readonly object _lock = new();
    private AppSettings _current;
    private readonly Action<string>? _log;

    public SettingsStore(string folder, ISecretProtector protector, Action<string>? log = null)
    {
        Directory.CreateDirectory(folder);
        _path = Path.Combine(folder, "settings.json");
        _protector = protector;
        _log = log;
        _current = Load();
    }

    public string FilePath => _path;
    public event Action? Changed;

    public AppSettings Current { get { lock (_lock) return _current; } }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), Json.Options) ?? new AppSettings();
                s.Normalize();
                return s;
            }
        }
        catch (Exception e)
        {
            _log?.Invoke("Settings file was unreadable and has been reset: " + e.Message);
            try { File.Copy(_path, _path + ".broken", overwrite: true); } catch { }
        }
        var fresh = new AppSettings();
        fresh.Normalize();
        return fresh;
    }

    /// <summary>Applies a change under the lock, normalises, saves, and raises <see cref="Changed"/>.</summary>
    public void Update(Action<AppSettings> change)
    {
        lock (_lock)
        {
            change(_current);
            _current.Normalize();
            SaveLocked();
        }
        Changed?.Invoke();
    }

    private void SaveLocked()
    {
        try { AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(_current, Json.Indented)); }
        catch (Exception e) { _log?.Invoke("Could not save settings: " + e.Message); }
    }

    public string Protect(string plain) => string.IsNullOrEmpty(plain) ? "" : _protector.Protect(plain);

    public string Reveal(string protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue)) return "";
        try { return _protector.Unprotect(protectedValue); }
        catch (Exception e)
        {
            _log?.Invoke("A stored key could not be decrypted (was the profile moved?): " + e.Message);
            return "";
        }
    }

    public static string KeyHint(string plain)
        => string.IsNullOrEmpty(plain) ? "" : plain.Length <= 8 ? "••••" : "••••" + plain[^4..];

    /// <summary>
    /// Merges a patch from the UI. Secret fields and unknown keys are ignored; the result is normalised.
    /// </summary>
    public void ApplyPatch(JsonObject patch)
    {
        Update(s =>
        {
            var current = JsonSerializer.SerializeToNode(s, Json.Options)!.AsObject();
            MergeInto(current, patch, depth: 0);
            var merged = current.Deserialize<AppSettings>(Json.Options);
            if (merged is null) return;
            // Copy every public property back (keeps the same instance for observers).
            foreach (var prop in typeof(AppSettings).GetProperties().Where(p => p.CanWrite))
                prop.SetValue(s, prop.GetValue(merged));
        });
    }

    private static readonly HashSet<string> SecretKeys = new(StringComparer.OrdinalIgnoreCase)
        { "apiKeyProtected", "secretProtected" };

    private static void MergeInto(JsonObject target, JsonObject patch, int depth)
    {
        foreach (var (key, value) in patch)
        {
            if (SecretKeys.Contains(key) || depth > 3) continue;
            if (depth == 0 && !target.ContainsKey(key)) continue;
            if (value is JsonObject childPatch && target[key] is JsonObject childTarget)
            {
                MergeInto(childTarget, childPatch, depth + 1);
            }
            else if (value is JsonObject newObject && depth >= 1 && target[key] is null)
            {
                // A provider entry the user hasn't configured yet.
                var fresh = new JsonObject();
                MergeInto(fresh, newObject, depth + 1);
                target[key] = fresh;
            }
            else
            {
                target[key] = value?.DeepClone();
            }
        }
    }
}
