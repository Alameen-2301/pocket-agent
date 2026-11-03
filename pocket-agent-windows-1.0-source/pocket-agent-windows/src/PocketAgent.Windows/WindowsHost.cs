using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using PocketAgent.Core.App;
using PocketAgent.Core.Mcp;
using PocketAgent.Core.Safety;
using PocketAgent.Core.Settings;
using PocketAgent.Core.Voice;
using PocketAgent.Windows.Input;
using PocketAgent.Windows.Overlay;
using PocketAgent.Windows.Shell;
using PocketAgent.Windows.Tools;
using PocketAgent.Windows.Voice;
using WinForms = System.Windows.Forms;

namespace PocketAgent.Windows;

/// <summary>Creates the Windows services, the controller and the windows, and implements <see cref="IPlatform"/>.</summary>
internal sealed class WindowsHost : IPlatform, IDisposable
{
    private readonly Application _app;
    private readonly EventWaitHandle _showEvent;
    private readonly bool _startHidden;
    private readonly Dispatcher _ui;
    private MainWindow _main = null!;
    private OverlayService _overlay = null!;
    private TrayIcon _tray = null!;
    private InputMonitor _input = null!;
    private NAudioRecorder _recorder = null!;
    private NAudioPlayer _player = null!;
    private CuePlayer _cues = null!;
    private WindowsSpeechRecognizer? _live;
    private WindowsVoices? _voices;
    private SapiWakeWord? _wake;
    private SettingsStore _settings = null!;
    private AppController _controller = null!;
    private RegisteredWaitHandle? _showWait;
    private bool _quitting;

    public WindowsHost(Application app, EventWaitHandle showEvent, bool startHidden)
    {
        _app = app;
        _showEvent = showEvent;
        _startHidden = startHidden;
        _ui = app.Dispatcher;
        DataFolder = CrashLog.Folder;
        Protector = new DpapiProtector();
        AppVersion = typeof(WindowsHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "1.0.0";
    }

    public string DataFolder { get; }
    public string AppVersion { get; }
    public ISecretProtector Protector { get; }
    public int OwnProcessId => Environment.ProcessId;
    public IUiChannel Ui => _main;
    public IOverlay Overlay => _overlay;
    public IAudioRecorder Recorder => _recorder;
    public IAudioPlayer Player => _player;
    public ILiveSpeechRecognizer? LiveRecognizer => _live;
    public ITextToSpeech? NativeTts => _voices;
    public IWakeWordListener? WakeWord => _wake is { IsAvailable: true } ? _wake : null;
    public bool MainWindowVisible => _main.VisibleForUser;

    public void Start()
    {
        Directory.CreateDirectory(DataFolder);
        _settings = new SettingsStore(DataFolder, Protector, CrashLog.Info);
        var approvals = new ApprovalStore(DataFolder, CrashLog.Info);
        var s = _settings.Current;

        _overlay = new OverlayService(_ui, s.BubbleLeft, s.BubbleTop);
        _overlay.Moved += (x, y) => _settings.Update(st => { st.BubbleLeft = Math.Round(x, 1); st.BubbleTop = Math.Round(y, 1); });
        _recorder = new NAudioRecorder();
        _player = new NAudioPlayer();
        _cues = new CuePlayer();
        try { _live = new WindowsSpeechRecognizer(); } catch (Exception e) { CrashLog.Info("Windows speech recognition unavailable: " + e.Message); }
        try { _voices = new WindowsVoices(); } catch (Exception e) { CrashLog.Info("Windows voices unavailable: " + e.Message); }
        try { _wake = new SapiWakeWord(); } catch (Exception e) { CrashLog.Info("Wake word unavailable: " + e.Message); }

        _main = new MainWindow(DataFolder) { CloseToTray = s.CloseToTray };
        _main.HiddenToTray += () =>
        {
            if (_settings.Current.TrayHintShown) return;
            _tray.Notify("Pocket Agent is still running", $"Hold {TalkKeys.Label(_settings.Current.TalkKey)} to talk. Right-click the tray icon to quit.");
            _settings.Update(st => st.TrayHintShown = true);
        };

        _controller = new AppController(this, _settings, approvals, services => new WindowsToolExecutor(services, _overlay));
        _main.MessageReceived += json => _controller.HandleUiMessage(json);
        _settings.Changed += () => _ui.BeginInvoke(new Action(() => _main.CloseToTray = _settings.Current.CloseToTray));

        _input = new InputMonitor(OwnProcessId);
        _input.TalkKey += down => _controller.OnTalkKey(down);
        _input.UserInput += () => _controller.OnUserInput();
        _input.Panic += () => _controller.Panic();
        _input.Start();

        _tray = new TrayIcon(AppIcon.TrayIcon(),
            open: ShowMainWindow,
            toggleControl: () => _ = _controller.ControlActive ? _controller.StopControlAsync() : _controller.StartControlAsync(),
            talk: () => _controller.ToggleListening("tray"),
            quit: Quit,
            controlActive: () => _controller.ControlActive);

        _controller.Initialize();
        if (!_startHidden) _main.Show();

        _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) => _ui.BeginInvoke(new Action(ShowMainWindow)), null, -1, false);
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle or UserPreferenceCategory.Color)
            _ui.BeginInvoke(new Action(() => _main.ApplyTheme(_settings.Current.Theme)));
    }

    // ---------------------------------------------------------------- IPlatform

    public PlatformInfo Describe()
    {
        var screens = WinForms.Screen.AllScreens;
        var primary = WinForms.Screen.PrimaryScreen?.Bounds ?? screens[0].Bounds;
        bool mcp;
        try { RustEngine.EnsureLoaded(); mcp = true; }
        catch { mcp = false; }
        return new PlatformInfo
        {
            OsDescription = RuntimeInformation.OSDescription,
            MachineSummary = $"{RuntimeInformation.OSDescription.Replace("Microsoft ", "")}, {screens.Length} monitor{(screens.Length == 1 ? "" : "s")}, primary {primary.Width}x{primary.Height}",
            Voices = WindowsVoices.List(),
            RecognizerLanguages = WindowsSpeechRecognizer.Languages().ToList(),
            LiveRecognizerAvailable = _live is not null,
            LiveRecognizerError = _live?.UnavailableReason,
            WakeWordAvailable = _wake?.IsAvailable == true,
            WakeWordError = _wake?.UnavailableReason,
            MicrophoneAvailable = NAudioRecorder.HasMicrophone,
            McpLibraryAvailable = mcp,
        };
    }

    public void SetClipboard(string text)
    {
        _ui.Invoke(() =>
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    Clipboard.SetText(text);
                    return;
                }
                catch (COMException) { Thread.Sleep(60); }
            }
        });
    }

    public void OpenExternal(string url) => MainWindow.OpenExternal(url);

    public void Notify(string title, string message) => _ui.BeginInvoke(new Action(() => _tray.Notify(title, message)));

    public void ShowMainWindow() => _ui.BeginInvoke(new Action(() => _main.ShowAndActivate()));

    public void ApplyAppearance(string theme) => _ui.BeginInvoke(new Action(() => _main.ApplyTheme(theme)));

    public void SetStartWithWindows(bool enabled)
    {
        try { StartupRegistration.Set(enabled); }
        catch (Exception e) { CrashLog.Info("Start with Windows: " + e.Message); }
    }

    public void ConfigureInput(string talkKey, string talkMode, bool takeoverDetection, bool tripleEsc)
        => _input?.Configure(talkKey, takeoverDetection, tripleEsc);

    public void ConfigureNativeVoice(string voiceId, double rate) => _voices?.Configure(voiceId, rate);

    public string? SaveTextFile(string suggestedName, string content)
    {
        return _ui.Invoke(() =>
        {
            var dialog = new SaveFileDialog
            {
                FileName = suggestedName,
                Filter = "Text file (*.txt)|*.txt|All files (*.*)|*.*",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            };
            if (dialog.ShowDialog(_main) != true) return null;
            File.WriteAllText(dialog.FileName, content);
            return dialog.FileName;
        });
    }

    public void PlayCue(string cue) => _cues.Play(cue);

    public void Quit()
    {
        _ui.BeginInvoke(new Action(() =>
        {
            if (_quitting) return;
            _quitting = true;
            _main.AllowClose = true;
            _app.Shutdown();
        }));
    }

    public void Dispose()
    {
        try { _controller?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(4)); } catch { }
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _showWait?.Unregister(null);
        try { _input?.Dispose(); } catch { }
        try { _wake?.Dispose(); } catch { }
        try { _live?.Dispose(); } catch { }
        try { _recorder?.Stop(); } catch { }
        try { _overlay?.Dispose(); } catch { }
        try { _tray?.Dispose(); } catch { }
    }
}
