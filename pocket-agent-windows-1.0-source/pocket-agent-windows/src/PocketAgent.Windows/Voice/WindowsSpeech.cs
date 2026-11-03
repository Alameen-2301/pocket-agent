using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PocketAgent.Core.App;
using PocketAgent.Core.Voice;
using Windows.Media.SpeechRecognition;
using Windows.Media.SpeechSynthesis;
using Windows.Storage.Streams;

namespace PocketAgent.Windows.Voice;

/// <summary>
/// Free speech-to-text with Windows' own dictation (Windows.Media.SpeechRecognition), with live
/// partial text for the bubble. Needs "Online speech recognition" switched on in Windows Settings.
/// </summary>
internal sealed class WindowsSpeechRecognizer : ILiveSpeechRecognizer, IDisposable
{
    private const int PrivacyNotAccepted = unchecked((int)0x80045509);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly StringBuilder _text = new();
    private SpeechRecognizer? _recognizer;
    private string? _language;
    private Action<string>? _onPartial;
    private TaskCompletionSource? _completed;
    private bool _running;

    public bool IsAvailable { get; private set; } = true;
    public string? UnavailableReason { get; private set; }

    public static IReadOnlyList<string> Languages()
    {
        try { return SpeechRecognizer.SupportedTopicLanguages.Select(l => l.LanguageTag).ToList(); }
        catch { return []; }
    }

    public async Task StartAsync(string language, Action<string> onPartial, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureAsync(language).ConfigureAwait(false);
            lock (_text) _text.Clear();
            _onPartial = onPartial;
            _completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await _recognizer!.ContinuousRecognitionSession.StartAsync().AsTask(ct).ConfigureAwait(false);
            _running = true;
            IsAvailable = true;
            UnavailableReason = null;
        }
        catch (Exception e) when (e.HResult == PrivacyNotAccepted)
        {
            UnavailableReason = "Windows speech recognition needs “Online speech recognition” turned on (Settings → Privacy & security → Speech), or choose the Whisper option in Settings → Voice.";
            throw new InvalidOperationException(UnavailableReason, e);
        }
        finally { _gate.Release(); }
    }

    private async Task EnsureAsync(string language)
    {
        if (_recognizer is not null && _language == language) return;
        _recognizer?.Dispose();
        _recognizer = null;
        var lang = language == "auto" ? SpeechRecognizer.SystemSpeechLanguage : new global::Windows.Globalization.Language(language);
        var recognizer = new SpeechRecognizer(lang);
        recognizer.Constraints.Add(new SpeechRecognitionTopicConstraint(SpeechRecognitionScenario.Dictation, "dictation"));
        var compiled = await recognizer.CompileConstraintsAsync().AsTask().ConfigureAwait(false);
        if (compiled.Status != SpeechRecognitionResultStatus.Success)
        {
            recognizer.Dispose();
            IsAvailable = false;
            UnavailableReason = $"Windows speech recognition is unavailable for {lang.DisplayName} ({compiled.Status}).";
            throw new InvalidOperationException(UnavailableReason);
        }
        recognizer.ContinuousRecognitionSession.AutoStopSilenceTimeout = TimeSpan.FromSeconds(20);
        recognizer.ContinuousRecognitionSession.ResultGenerated += (_, e) =>
        {
            if (e.Result.Status != SpeechRecognitionResultStatus.Success || string.IsNullOrWhiteSpace(e.Result.Text)) return;
            string all;
            lock (_text)
            {
                _text.Append(e.Result.Text).Append(' ');
                all = _text.ToString();
            }
            _onPartial?.Invoke(all.Trim());
        };
        recognizer.HypothesisGenerated += (_, e) =>
        {
            string all;
            lock (_text) all = _text.ToString() + e.Hypothesis.Text;
            _onPartial?.Invoke(all.Trim());
        };
        recognizer.ContinuousRecognitionSession.Completed += (_, _) => _completed?.TrySetResult();
        _recognizer = recognizer;
        _language = language;
    }

    public async Task<string> StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_recognizer is not null && _running)
            {
                try { await _recognizer.ContinuousRecognitionSession.StopAsync().AsTask().ConfigureAwait(false); }
                catch (COMException) { }
                catch (InvalidOperationException) { }
                if (_completed is not null) await Task.WhenAny(_completed.Task, Task.Delay(1500)).ConfigureAwait(false);
            }
            _running = false;
            lock (_text) return _text.ToString().Trim();
        }
        finally { _gate.Release(); }
    }

    public void Cancel()
    {
        try
        {
            if (_recognizer is not null && _running) _ = _recognizer.ContinuousRecognitionSession.CancelAsync().AsTask();
        }
        catch { }
        _running = false;
    }

    public void Dispose() => _recognizer?.Dispose();
}

/// <summary>Offline Windows voices (Windows.Media.SpeechSynthesis), returned as WAV.</summary>
internal sealed class WindowsVoices : ITextToSpeech
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SpeechSynthesizer _synth = new();
    private volatile string _voiceId = "";
    private double _rate = 1.0;

    private static readonly string[] German = ["der", "die", "das", "und", "ist", "nicht", "ich", "sie", "ein", "eine", "mit", "auf", "für", "habe", "wurde", "ja", "nein", "bitte", "danke", "öffne", "geöffnet", "erledigt"];
    private static readonly string[] English = ["the", "and", "is", "not", "you", "it", "to", "with", "on", "for", "have", "was", "yes", "no", "please", "done", "opened"];

    public static List<VoiceInfo> List()
    {
        try
        {
            return SpeechSynthesizer.AllVoices
                .Select(v => new VoiceInfo(v.Id, v.DisplayName, v.Language, v.Gender.ToString()))
                .OrderBy(v => v.Language).ThenBy(v => v.Name)
                .ToList();
        }
        catch { return []; }
    }

    public void Configure(string voiceId, double rate)
    {
        _voiceId = voiceId ?? "";
        _rate = Math.Clamp(rate, 0.5, 2.0);
    }

    private VoiceInformation? Pick(string text, string language)
    {
        var voices = SpeechSynthesizer.AllVoices;
        if (_voiceId.Length > 0)
        {
            var chosen = voices.FirstOrDefault(v => v.Id == _voiceId);
            if (chosen is not null) return chosen;
        }
        string? wanted = language != "auto" ? language : Guess(text);
        if (wanted is not null)
        {
            var prefix = wanted.Split('-')[0];
            var match = voices.FirstOrDefault(v => v.Language.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                        ?? voices.FirstOrDefault(v => v.Language.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }
        return SpeechSynthesizer.DefaultVoice;
    }

    /// <summary>Very small English/German guess so replies in German get a German voice.</summary>
    private static string? Guess(string text)
    {
        var words = text.ToLowerInvariant().Split(new[] { ' ', ',', '.', '!', '?', ';', ':' }, StringSplitOptions.RemoveEmptyEntries);
        int de = words.Count(w => German.Contains(w)) + (text.IndexOfAny(['ä', 'ö', 'ü', 'ß', 'Ä', 'Ö', 'Ü']) >= 0 ? 2 : 0);
        int en = words.Count(w => English.Contains(w));
        if (de > en && de >= 2) return "de-DE";
        if (en > de && en >= 2) return null; // the default voice is usually English
        return null;
    }

    public async Task<byte[]> SynthesizeAsync(string text, string language, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var voice = Pick(text, language);
            if (voice is not null) _synth.Voice = voice;
            try { _synth.Options.SpeakingRate = _rate; } catch { }
            using var stream = await _synth.SynthesizeTextToStreamAsync(text).AsTask(ct).ConfigureAwait(false);
            var bytes = new byte[stream.Size];
            using var reader = new DataReader(stream.GetInputStreamAt(0));
            await reader.LoadAsync((uint)stream.Size).AsTask(ct).ConfigureAwait(false);
            reader.ReadBytes(bytes);
            return bytes;
        }
        finally { _gate.Release(); }
    }
}
