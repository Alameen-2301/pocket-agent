using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using PocketAgent.Core.App;
using PocketAgent.Windows.Shell;

namespace PocketAgent.Windows;

/// <summary>The main window: the HTML interface in WebView2, talking to the controller over postMessage.</summary>
internal sealed class MainWindow : Window, IUiChannel
{
    public const string Host = "app.pocketagent.example";
    private static readonly string Origin = $"https://{Host}/";

    private readonly WebView2 _web = new();
    private readonly string _dataFolder;
    private volatile bool _ready;
    private bool _webStarted;
    private string _theme = "system";

    public event Action<string>? MessageReceived;
    public bool AllowClose { get; set; }
    public bool CloseToTray { get; set; } = true;
    public event Action? HiddenToTray;
    public volatile bool VisibleForUser;

    public MainWindow(string dataFolder)
    {
        _dataFolder = dataFolder;
        Title = "Pocket Agent";
        Width = 1180;
        Height = 780;
        MinWidth = 880;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = AppIcon.ImageSource();
        Background = new SolidColorBrush(Color.FromRgb(0x0E, 0x10, 0x20));
        _web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 0x0E, 0x10, 0x20);
        Content = _web;
        Loaded += async (_, _) =>
        {
            if (_webStarted) return;   // initialise once, even if Loaded is raised again
            _webStarted = true;
            await InitializeWebAsync();
        };
        SourceInitialized += (_, _) => ApplyTheme(_theme);
        IsVisibleChanged += (_, _) => VisibleForUser = IsVisible && WindowState != WindowState.Minimized;
        StateChanged += (_, _) => VisibleForUser = IsVisible && WindowState != WindowState.Minimized;
    }

    private async Task InitializeWebAsync()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(_dataFolder, "WebView2"));
            await _web.EnsureCoreWebView2Async(env);
            var core = _web.CoreWebView2;
            bool devTools = Environment.GetEnvironmentVariable("POCKET_AGENT_DEVTOOLS") == "1";
            core.Settings.AreDevToolsEnabled = devTools;
            core.Settings.AreDefaultContextMenusEnabled = devTools;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = devTools;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.SetVirtualHostNameToFolderMapping(Host, UiAssets.Extract(_dataFolder), CoreWebView2HostResourceAccessKind.Allow);
            core.WebMessageReceived += (_, e) =>
            {
                if (!e.Source.StartsWith(Origin, StringComparison.OrdinalIgnoreCase)) return;
                // The page registers its message listener before it sends anything (its first message is
                // "ready"), so from here on posts reach it. Waiting for NavigationCompleted instead would
                // drop the state and history the host sends straight back in reply to "ready".
                _ready = true;
                try { MessageReceived?.Invoke(e.WebMessageAsJson); }
                catch (Exception ex) { CrashLog.Write(ex); }
            };
            core.NavigationStarting += (_, e) =>
            {
                if (e.Uri.StartsWith(Origin, StringComparison.OrdinalIgnoreCase))
                {
                    _ready = false;   // a (re)load: wait for the new page to say "ready"
                    return;
                }
                e.Cancel = true;
                OpenExternal(e.Uri);
            };
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                OpenExternal(e.Uri);
            };
            core.NavigationCompleted += (_, e) => { if (!e.IsSuccess) _ready = false; };
            core.ProcessFailed += (_, e) =>
            {
                CrashLog.Info("WebView2 process failed: " + e.ProcessFailedKind);
                _ready = false;
                if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited) return;
                Dispatcher.BeginInvoke(new Action(() => { try { _web.Reload(); } catch { } }));
            };
            ApplyTheme(_theme);
            core.Navigate(Origin + "index.html");
        }
        catch (WebView2RuntimeNotFoundException)
        {
            ShowRuntimeMissing();
        }
        catch (Exception e)
        {
            CrashLog.Write(e);
            ShowRuntimeMissing(e.Message);
        }
    }

    private void ShowRuntimeMissing(string? detail = null)
    {
        var panel = new StackPanel { Margin = new Thickness(48), VerticalAlignment = VerticalAlignment.Center, MaxWidth = 560 };
        panel.Children.Add(new TextBlock
        {
            Text = "Pocket Agent needs the Microsoft Edge WebView2 Runtime",
            FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "It's built into Windows 11 and most Windows 10 PCs. Install it from Microsoft, then start Pocket Agent again."
                   + (detail is null ? "" : "\n\nDetails: " + detail),
            FontSize = 14, Margin = new Thickness(0, 12, 0, 20), Foreground = new SolidColorBrush(Color.FromRgb(0xA5, 0xA8, 0xCE)),
            TextWrapping = TextWrapping.Wrap,
        });
        var button = new Button { Content = "Download WebView2 Runtime", Padding = new Thickness(16, 8, 16, 8), HorizontalAlignment = HorizontalAlignment.Left };
        button.Click += (_, _) => OpenExternal("https://go.microsoft.com/fwlink/p/?LinkId=2124703");
        panel.Children.Add(button);
        Content = panel;
    }

    public static void OpenExternal(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return;
        if (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp && u.Scheme != "ms-settings") return;
        try { Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception e) { CrashLog.Write(e); }
    }

    public void Post(string json)
    {
        if (!_ready) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try { if (_ready) _web.CoreWebView2?.PostWebMessageAsJson(json); }
            catch (Exception e) { Debug.WriteLine(e); }
        }));
    }

    public void ApplyTheme(string theme)
    {
        _theme = theme;
        bool dark = ThemeHelper.IsDark(theme);
        var bg = dark ? Color.FromRgb(0x0E, 0x10, 0x20) : Color.FromRgb(0xF7, 0xF6, 0xFF);
        Background = new SolidColorBrush(bg);
        _web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, bg.R, bg.G, bg.B);
        ThemeHelper.ApplyTitleBar(new WindowInteropHelper(this).Handle, dark);
        try
        {
            if (_web.CoreWebView2 is { } core)
                core.Profile.PreferredColorScheme = theme switch
                {
                    "dark" => CoreWebView2PreferredColorScheme.Dark,
                    "light" => CoreWebView2PreferredColorScheme.Light,
                    _ => CoreWebView2PreferredColorScheme.Auto,
                };
        }
        catch { }
    }

    public void ShowAndActivate()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;  // reliably come to the front, then behave normally
        Topmost = false;
        Focus();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose && CloseToTray)
        {
            e.Cancel = true;
            Hide();
            HiddenToTray?.Invoke();
            return;
        }
        base.OnClosing(e);
    }
}

/// <summary>Unpacks the embedded HTML interface into %LOCALAPPDATA% for WebView2's virtual host.</summary>
internal static class UiAssets
{
    public static string Extract(string dataFolder)
    {
        var assembly = typeof(UiAssets).Assembly;
        var version = assembly.ManifestModule.ModuleVersionId.ToString("N")[..12];
        var root = Path.Combine(dataFolder, "ui");
        var folder = Path.Combine(root, version);
        var names = assembly.GetManifestResourceNames().Where(n => n.StartsWith("ui/", StringComparison.Ordinal)).ToList();
        if (!Directory.Exists(folder) || Directory.GetFiles(folder).Length < names.Count)
        {
            Directory.CreateDirectory(folder);
            foreach (var name in names)
            {
                using var resource = assembly.GetManifestResourceStream(name)!;
                var target = Path.Combine(folder, name[3..].Replace('\\', '/').Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var file = File.Create(target);
                resource.CopyTo(file);
            }
        }
        // Remove interface files left by older versions.
        foreach (var old in Directory.GetDirectories(root).Where(d => !d.EndsWith(version, StringComparison.Ordinal)))
        {
            try { Directory.Delete(old, recursive: true); } catch { }
        }
        return folder;
    }
}

internal static class AppIcon
{
    public static System.Windows.Media.ImageSource? ImageSource()
    {
        try
        {
            var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("PocketAgent.ico");
            return stream is null ? null : System.Windows.Media.Imaging.BitmapFrame.Create(stream);
        }
        catch { return null; }
    }

    public static System.Drawing.Icon TrayIcon()
    {
        var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("PocketAgent.ico");
        return stream is null ? System.Drawing.SystemIcons.Application : new System.Drawing.Icon(stream, System.Windows.Forms.SystemInformation.SmallIconSize);
    }
}
