using System.Threading.Channels;
using PocketAgent.Core.Providers;
using PocketAgent.Core.Settings;
using PocketAgent.Core.Util;
using PocketAgent.Core.Voice;

namespace PocketAgent.Core.App;

public sealed partial class AppController
{
    private readonly object _voiceLock = new();
    private volatile bool _listening;
    private bool _listenTest;
    private bool _liveActive;
    private volatile string? _liveError;
    private bool _pausedForListening;
    private VoiceActivityDetector? _vad;
    private DateTimeOffset _listenStarted;
    private string _voiceState = "idle";
    private string? _partial;
    private string? _lastHeard;
    private string? _speakingText;
    private long _lastLevelPost;
    private string? _notice;
    private DateTimeOffset _noticeUntil;
    private bool _micTest;

    private sealed record SpeechItem(string Text, string Kind, int Version);
    private readonly Channel<SpeechItem> _speechQueue = Channel.CreateUnbounded<SpeechItem>();
    private CancellationTokenSource _speechCts = new();
    private int _narrationVersion;

    public string VoiceState => _voiceState;
    public bool Listening => _listening;

    private string SpeechLanguage => _settings.Current.Language is { Length: > 0 } l && l != "auto" ? l : "auto";

    // ---------------------------------------------------------------- triggers

    /// <summary>The talk key went down or up (from the input monitor).</summary>
    public void OnTalkKey(bool down)
    {
        if (_settings.Current.TalkMode == "hold")
        {
            if (down) BeginListening("key", useVad: false);
            else EndListening(cancel: false);
        }
        else if (down)
        {
            if (_listening) EndListening(cancel: false);
            else BeginListening("key", useVad: true);
        }
    }

    /// <summary>Mic buttons in the app and the bubble: click to start, click again (or pause speaking) to finish.</summary>
    public void ToggleListening(string trigger, bool test = false)
    {
        if (_listening) EndListening(cancel: false);
        else BeginListening(trigger, useVad: true, test);
    }

    private void OnWakeWord()
    {
        if (_listening || !_settings.Current.WakeWordEnabled) return;
        Log("voice", "Heard the wake word.");
        BeginListening("wake", useVad: true);
    }

    private void OnVoiceCommandHeard(VoiceCommand command)
    {
        if (_listening || _voiceState == "speaking") return;
        if (!_agent.IsRunning && !_automationPaused) return;
        Log("voice", "Voice command: " + command);
        HandleCommand(command, null);
    }

    /// <summary>
    /// The offline listener runs only when needed: always for the (opt-in) wake word, and for
    /// "Pocket, stop/pause/continue" only while a task is running, so the microphone isn't in use otherwise.
    /// </summary>
    private void ConfigureWakeWord(AppSettings s)
    {
        var w = _p.WakeWord;
        if (w is null || !w.IsAvailable) return;
        bool commands = s.VoiceCommandsEnabled && (_lastAgentState != PocketAgent.Core.Agent.AgentState.Idle || _automationPaused);
        try { w.Configure(s.WakeWordEnabled, commands, SpeechLanguage); }
        catch (Exception e) { Log("voice", "Wake word unavailable: " + e.Message); }
    }

    // ---------------------------------------------------------------- listening

    private void BeginListening(string trigger, bool useVad, bool test = false)
    {
        lock (_voiceLock)
        {
            if (_listening) return;
            if (_micTest) StopMicTestLocked();
            _listening = true;
            _listenTest = test;
            _listenStarted = DateTimeOffset.Now;
            _vad = useVad ? new VoiceActivityDetector(_p.Recorder.SampleRate) : null;
            _partial = null;
        }
        CancelSpeech(); // talking interrupts the assistant's voice
        _p.WakeWord?.SetSuspended(true);
        if (!test && _agent.IsRunning && !_agent.IsPaused)
        {
            _pausedForListening = true;
            _agent.Pause("Listening…", takeover: false);
        }
        try
        {
            _p.Recorder.Start(OnAudioFrame);
        }
        catch (Exception e)
        {
            lock (_voiceLock) _listening = false;
            _p.WakeWord?.SetSuspended(false);
            ResumeAfterListening();
            ShowNotice("Microphone unavailable: " + e.Message);
            Log("voice", "Microphone error: " + e.Message);
            return;
        }
        _liveActive = false;
        if (_settings.Current.SttEngine == "windows" && _p.LiveRecognizer is { IsAvailable: true } live)
        {
            _liveActive = true;
            _liveError = null;
            _ = live.StartAsync(SpeechLanguage, OnPartial, CancellationToken.None).ContinueWith(t =>
            {
                if (t.Exception is not null)
                {
                    _liveActive = false;
                    _liveError = t.Exception.GetBaseException().Message;
                    Log("voice", "Windows speech recognition failed: " + _liveError);
                }
            }, TaskScheduler.Default);
        }
        _voiceState = "listening";
        _p.PlayCue("listen");
        RequestPush();
    }

    private void OnPartial(string text)
    {
        _partial = text;
        RequestPush();
    }

    private void OnAudioFrame(short[] samples)
    {
        var level = Level.Of(samples);
        try { _p.Overlay.SetLevel(level); } catch { }
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastLevelPost) > 70)
        {
            Interlocked.Exchange(ref _lastLevelPost, now);
            Post("level", new { value = Math.Round(level, 3) });
        }
        var vad = _vad;
        if (vad is null || !_listening) return;
        var ev = vad.Process(samples);
        if (ev == VadEvent.Ended) _ = Task.Run(() => EndListening(cancel: false));
        else if (ev == VadEvent.NoSpeech) _ = Task.Run(() => EndListening(cancel: true));
    }

    private void CheckListeningTimeout()
    {
        if (_listening && DateTimeOffset.Now - _listenStarted > TimeSpan.FromSeconds(60))
            EndListening(cancel: false);
    }

    public void EndListening(bool cancel)
    {
        bool test, live;
        lock (_voiceLock)
        {
            if (!_listening) return;
            _listening = false;
            test = _listenTest;
            live = _liveActive;
            _vad = null;
        }
        byte[] wav;
        try { wav = _p.Recorder.Stop(); }
        catch (Exception e) { wav = []; Log("voice", "Microphone stop error: " + e.Message); }
        _p.WakeWord?.SetSuspended(false);
        double seconds = Wav.DurationSeconds(wav);
        if (cancel || seconds < 0.35)
        {
            if (live) _p.LiveRecognizer?.Cancel();
            _voiceState = "idle";
            if (!cancel) ShowNotice("Hold the talk key while you speak.");
            else if (seconds >= 0.35) ShowNotice("I didn't hear anything.");
            ResumeAfterListening();
            RequestPush();
            return;
        }
        _voiceState = "transcribing";
        RequestPush();
        _ = Task.Run(async () =>
        {
            string text = "";
            try
            {
                if (live && _p.LiveRecognizer is { } recognizer)
                {
                    text = await recognizer.StopAsync().ConfigureAwait(false);
                }
                else
                {
                    var transcriber = CreateTranscriber(out var why);
                    if (transcriber is null && _liveError is not null) { ShowNotice(_liveError, 8); }
                    else if (transcriber is null) { ShowNotice(why); Log("voice", why); }
                    else
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                        text = await transcriber.TranscribeAsync(wav, SpeechLanguage, timeout.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception e)
            {
                var message = e is ProviderException pe ? pe.Friendly : e.Message;
                ShowNotice("Couldn't understand the recording: " + message);
                Log("voice", "Transcription failed: " + message);
            }
            _voiceState = "idle";
            _partial = null;
            HandleTranscript(text, test);
            RequestPush();
        });
    }

    private void HandleTranscript(string text, bool test)
    {
        text = (text ?? "").Trim();
        if (test)
        {
            Post("transcript", new { text });
            return;
        }
        if (text.Length == 0)
        {
            ShowNotice("I didn't catch that.");
            ResumeAfterListening();
            return;
        }
        _lastHeard = text;
        var command = VoiceCommands.Parse(text);
        if (command != VoiceCommand.None && HandleCommand(command, text)) return;
        bool resume = _pausedForListening;
        _pausedForListening = false;
        _ = SubmitAsync(text, "voice");
        if (resume && !_automationPaused) _agent.Resume();
    }

    /// <summary>Returns true when the command was handled (otherwise the text goes to the assistant).</summary>
    private bool HandleCommand(VoiceCommand command, string? heard)
    {
        bool busy = _agent.IsRunning || _automationPaused;
        switch (command)
        {
            case VoiceCommand.Stop:
                if (heard is not null) AddChat("user", heard, "voice");
                if (busy) StopTask();
                else CancelSpeech();
                ShowNotice("Stopped.");
                return true;
            case VoiceCommand.Pause when busy:
                _pausedForListening = false;
                PauseAutomation("Paused. Say “continue” or press Resume.");
                return true;
            case VoiceCommand.Resume when busy:
                ResumeAutomation();
                return true;
            default:
                if (!busy) return false;
                ResumeAfterListening();
                return true;
        }
    }

    private void ResumeAfterListening()
    {
        if (!_pausedForListening) return;
        _pausedForListening = false;
        if (!_automationPaused) _agent.Resume();
    }

    private IBatchTranscriber? CreateTranscriber(out string why)
    {
        var t = _settings.Current.Transcription;
        why = "";
        var key = _settings.Reveal(t.ApiKeyProtected);
        if (key.Length == 0 && t.UseProviderKey) key = ProviderKeyFor(t.BaseUrl);
        if (string.IsNullOrWhiteSpace(t.BaseUrl))
        {
            why = "Set the speech-to-text address in Settings → Voice.";
            return null;
        }
        bool local = Uri.TryCreate(t.BaseUrl, UriKind.Absolute, out var u) && (u.IsLoopback || u.Host == "localhost");
        if (key.Length == 0 && !local)
        {
            why = "Add an API key for speech-to-text in Settings → Voice, or switch to Windows speech.";
            return null;
        }
        return new WhisperApiTranscriber(t.BaseUrl, key, t.Model);
    }

    /// <summary>Reuses an AI provider key whose server matches the speech service's host.</summary>
    private string ProviderKeyFor(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var target)) return "";
        var s = _settings.Current;
        foreach (var preset in ProviderPresets.All)
        {
            var o = ProviderPresets.Resolve(s, _settings, preset.Id);
            if (o.ApiKey.Length > 0 && Uri.TryCreate(o.BaseUrl, UriKind.Absolute, out var u) && u.Host.Equals(target.Host, StringComparison.OrdinalIgnoreCase))
                return o.ApiKey;
        }
        return "";
    }

    // ---------------------------------------------------------------- microphone test (setup and settings)

    public void SetMicTest(bool on)
    {
        lock (_voiceLock)
        {
            if (on == _micTest || _listening) return;
            if (!on) { StopMicTestLocked(); return; }
            try
            {
                _p.Recorder.Start(samples =>
                {
                    var now = Environment.TickCount64;
                    if (now - Interlocked.Read(ref _lastLevelPost) > 60)
                    {
                        Interlocked.Exchange(ref _lastLevelPost, now);
                        Post("level", new { value = Math.Round(Level.Of(samples), 3) });
                    }
                });
                _micTest = true;
            }
            catch (Exception e) { Toast("error", "Microphone unavailable: " + e.Message); }
        }
    }

    private void StopMicTestLocked()
    {
        if (!_micTest) return;
        _micTest = false;
        try { _p.Recorder.Stop(); } catch { }
    }

    // ---------------------------------------------------------------- speaking

    /// <summary>Queues text to be spoken. kind: reply, narration or test.</summary>
    public void Speak(string text, string kind)
    {
        var s = _settings.Current;
        if (s.TtsEngine == "off" && kind != "test") return;
        if (kind == "reply" && !s.SpeakReplies) return;
        if (kind == "narration" && (!s.NarrateSteps || !s.SpeakReplies)) return;
        var clean = TextUtil.ForSpeech(text, kind == "narration" ? 220 : 700);
        if (clean.Length == 0) return;
        int version = kind == "narration" ? Interlocked.Increment(ref _narrationVersion) : 0;
        _speechQueue.Writer.TryWrite(new SpeechItem(clean, kind, version));
    }

    public void CancelSpeech()
    {
        var old = Interlocked.Exchange(ref _speechCts, new CancellationTokenSource());
        try { old.Cancel(); } catch { }
        while (_speechQueue.Reader.TryRead(out _)) { }
        try { _p.Player.Stop(); } catch { }
        if (_voiceState == "speaking") _voiceState = "idle";
        _speakingText = null;
    }

    private void StartSpeechWorker() => _ = Task.Run(async () =>
    {
        try
        {
            await foreach (var item in _speechQueue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (item.Kind == "narration" && item.Version != Volatile.Read(ref _narrationVersion)) continue; // a newer narration replaced it
                // Don't talk over the user.
                for (int i = 0; _listening && i < 300; i++) await Task.Delay(100).ConfigureAwait(false);
                if (_listening && item.Kind == "narration") continue;
                await SpeakNowAsync(item).ConfigureAwait(false);
            }
        }
        catch (ChannelClosedException) { }
    });

    private async Task SpeakNowAsync(SpeechItem item)
    {
        var ct = _speechCts.Token;
        var engine = CreateTts(out var why, item.Kind == "test");
        if (engine is null)
        {
            if (item.Kind == "test" && why.Length > 0) Toast("error", why);
            return;
        }
        try
        {
            var wav = await engine.SynthesizeAsync(item.Text, SpeechLanguage, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested) return;
            _voiceState = "speaking";
            _speakingText = item.Text;
            _p.WakeWord?.SetSuspended(true);
            RequestPush();
            await _p.Player.PlayAsync(wav, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            var message = e is ProviderException pe ? pe.Friendly : e.Message;
            Log("voice", "Speech failed: " + message);
            if (item.Kind != "narration") ShowNotice("Couldn't speak the reply: " + message);
        }
        finally
        {
            if (_voiceState == "speaking") _voiceState = "idle";
            _speakingText = null;
            if (!_listening) _p.WakeWord?.SetSuspended(false);
            RequestPush();
        }
    }

    private ITextToSpeech? CreateTts(out string why, bool test)
    {
        var s = _settings.Current;
        why = "";
        if (s.TtsEngine == "openai")
        {
            var key = _settings.Reveal(s.Speech.ApiKeyProtected);
            if (key.Length == 0 && s.Speech.UseProviderKey) key = ProviderKeyFor(s.Speech.BaseUrl);
            bool local = Uri.TryCreate(s.Speech.BaseUrl, UriKind.Absolute, out var u) && (u.IsLoopback || u.Host == "localhost");
            if (key.Length == 0 && !local)
            {
                why = "Add an API key for the speech voice in Settings → Voice, or use a Windows voice.";
                return null;
            }
            return new OpenAiSpeechSynthesizer(s.Speech.BaseUrl, key, s.Speech.Model, s.Speech.Voice, s.SpeechRate);
        }
        if (_p.NativeTts is null) why = "Windows voices aren't available on this PC.";
        return _p.NativeTts;
    }

    // ---------------------------------------------------------------- notices in the bubble

    public void ShowNotice(string text, int seconds = 4)
    {
        _notice = text;
        _noticeUntil = DateTimeOffset.Now.AddSeconds(seconds);
        RequestPush();
    }
}
