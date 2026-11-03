using System;
using System.Globalization;
using System.Linq;
using System.Speech.Recognition;
using PocketAgent.Core.Voice;

namespace PocketAgent.Windows.Voice;

/// <summary>
/// Offline phrase spotting with the Windows desktop speech engine (SAPI): "Hey Pocket" to start
/// talking hands-free, and "Pocket, stop / pause / continue" while a task runs. Nothing is sent
/// anywhere; only the listed phrases can be recognised.
/// </summary>
internal sealed class SapiWakeWord : IWakeWordListener, IDisposable
{
    private readonly object _lock = new();
    private SpeechRecognitionEngine? _engine;
    private string _config = "";
    private volatile bool _suspended;

    public event Action? WakeWordHeard;
    public event Action<VoiceCommand>? CommandHeard;

    public bool IsAvailable { get; }
    public string? UnavailableReason { get; }

    public SapiWakeWord()
    {
        try
        {
            IsAvailable = SpeechRecognitionEngine.InstalledRecognizers().Count > 0;
            if (!IsAvailable) UnavailableReason = "No Windows speech recognizer is installed. Add a speech language in Settings → Time & language → Speech.";
        }
        catch (Exception e)
        {
            IsAvailable = false;
            UnavailableReason = "Windows speech recognition is not available: " + e.Message;
        }
    }

    public void SetSuspended(bool suspended) => _suspended = suspended;

    public void Configure(bool wakeWord, bool commands, string language)
    {
        if (!IsAvailable) return;
        var config = $"{wakeWord}|{commands}|{language}";
        lock (_lock)
        {
            if (config == _config && (_engine is not null || (!wakeWord && !commands))) return;
            _config = config;
            StopLocked();
            if (!wakeWord && !commands) return;

            var recognizers = SpeechRecognitionEngine.InstalledRecognizers();
            var info = recognizers.FirstOrDefault(r => language != "auto" && r.Culture.Name.Equals(language, StringComparison.OrdinalIgnoreCase))
                       ?? recognizers.FirstOrDefault(r => r.Culture.Name.Equals(CultureInfo.CurrentUICulture.Name, StringComparison.OrdinalIgnoreCase))
                       ?? recognizers.FirstOrDefault(r => r.Culture.TwoLetterISOLanguageName == "en")
                       ?? recognizers[0];
            var phrases = new Choices();
            if (wakeWord) phrases.Add("hey pocket", "okay pocket", "hi pocket");
            if (commands)
            {
                phrases.Add("pocket stop", "stop pocket", "pocket pause", "pocket continue", "pocket resume");
                if (info.Culture.TwoLetterISOLanguageName == "de") phrases.Add("pocket stopp", "pocket weiter", "pocket warte");
            }
            var engine = new SpeechRecognitionEngine(info);
            engine.LoadGrammar(new Grammar(new GrammarBuilder(phrases) { Culture = info.Culture }) { Name = "pocket" });
            engine.SpeechRecognized += OnRecognized;
            try
            {
                engine.SetInputToDefaultAudioDevice();
                engine.RecognizeAsync(RecognizeMode.Multiple);
                _engine = engine;
            }
            catch (Exception e)
            {
                engine.Dispose();
                CrashLog.Info("Wake word listener could not start: " + e.Message);
            }
        }
    }

    private void OnRecognized(object? sender, SpeechRecognizedEventArgs e)
    {
        if (_suspended || e.Result is null || e.Result.Confidence < 0.80f) return;
        var text = e.Result.Text.ToLowerInvariant();
        if (text.StartsWith("hey") || text.StartsWith("okay") || text.StartsWith("hi "))
            WakeWordHeard?.Invoke();
        else if (text.Contains("stop") || text.Contains("stopp"))
            CommandHeard?.Invoke(VoiceCommand.Stop);
        else if (text.Contains("pause") || text.Contains("warte"))
            CommandHeard?.Invoke(VoiceCommand.Pause);
        else if (text.Contains("continue") || text.Contains("resume") || text.Contains("weiter"))
            CommandHeard?.Invoke(VoiceCommand.Resume);
    }

    private void StopLocked()
    {
        var engine = _engine;
        _engine = null;
        if (engine is null) return;
        engine.SpeechRecognized -= OnRecognized;
        try { engine.RecognizeAsyncCancel(); } catch { }
        try { engine.Dispose(); } catch { }
    }

    public void Dispose()
    {
        lock (_lock) StopLocked();
    }
}
