using System.Diagnostics;
using System.Text.RegularExpressions;
using PocketAgent.Core.Providers;

namespace PocketAgent.Core.Tunnel;

public enum TunnelStatus { Off, Downloading, Starting, Online, Error }

/// <summary>
/// Tunnel mode: runs a Cloudflare quick tunnel (cloudflared, no account needed) that forwards a
/// random https://….trycloudflare.com address to the loopback MCP server, so a cloud assistant can connect.
/// The secret path still protects every request.
/// </summary>
public sealed partial class TunnelManager : IDisposable
{
    public const string DownloadUrl = "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe";

    private readonly object _lock = new();
    private readonly Action<string, string> _log;
    private Process? _process;
    private readonly Queue<string> _lastLines = new();

    public TunnelManager(Action<string, string> log) { _log = log; }

    public TunnelStatus Status { get; private set; } = TunnelStatus.Off;
    public string? PublicUrl { get; private set; }
    public string? Error { get; private set; }
    public double DownloadProgress { get; private set; }
    public event Action? Changed;

    [GeneratedRegex(@"https://[a-z0-9-]+\.trycloudflare\.com", RegexOptions.IgnoreCase)]
    private static partial Regex QuickTunnelUrl();

    public static string ExecutableName => OperatingSystem.IsWindows() ? "cloudflared.exe" : "cloudflared";

    /// <summary>Looks in the configured path, the app's own folder, the download folder and PATH.</summary>
    public static string? Find(string? configuredPath, string downloadFolder)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configuredPath)) candidates.Add(configuredPath.Trim().Trim('"'));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, ExecutableName));
        candidates.Add(Path.Combine(downloadFolder, ExecutableName));
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { candidates.Add(Path.Combine(dir.Trim().Trim('"'), ExecutableName)); } catch (ArgumentException) { }
        }
        if (OperatingSystem.IsWindows())
        {
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (pf.Length > 0) candidates.Add(Path.Combine(pf, "cloudflared", ExecutableName));
            var pf64 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (pf64.Length > 0) candidates.Add(Path.Combine(pf64, "cloudflared", ExecutableName));
        }
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Downloads the official cloudflared release from GitHub into <paramref name="folder"/>.</summary>
    public async Task<string> DownloadAsync(string folder, CancellationToken ct, string url = DownloadUrl)
    {
        Set(TunnelStatus.Downloading, null, null, 0);
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, ExecutableName);
        var temp = target + ".download";
        try
        {
            using var response = await HttpJson.Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? -1;
            await using (var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var output = File.Create(temp))
            {
                var buffer = new byte[81920];
                long done = 0;
                int read;
                var last = DateTime.UtcNow;
                while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    done += read;
                    if (total > 0 && (DateTime.UtcNow - last).TotalMilliseconds > 250)
                    {
                        last = DateTime.UtcNow;
                        DownloadProgress = Math.Clamp(done / (double)total, 0, 1);
                        Changed?.Invoke();
                    }
                }
            }
            var head = new byte[2];
            await using (var check = File.OpenRead(temp))
                if (await check.ReadAsync(head, ct).ConfigureAwait(false) < 2 || head[0] != 'M' || head[1] != 'Z')
                    throw new InvalidDataException("The download is not a Windows program.");
            File.Move(temp, target, overwrite: true);
            _log("system", "Downloaded cloudflared to " + target);
            Set(TunnelStatus.Off, null, null, 1);
            return target;
        }
        catch (Exception e)
        {
            try { File.Delete(temp); } catch { }
            Set(TunnelStatus.Error, null, "Download failed: " + e.Message, 0);
            throw;
        }
    }

    /// <summary>Starts cloudflared and waits until it reports the public address.</summary>
    public async Task StartAsync(string executable, int port, CancellationToken ct, TimeSpan? timeout = null)
    {
        Stop();
        Set(TunnelStatus.Starting, null, null, DownloadProgress);
        var found = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var psi = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var a in new[] { "tunnel", "--no-autoupdate", "--url", $"http://127.0.0.1:{port}" }) psi.ArgumentList.Add(a);
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        void OnLine(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            lock (_lastLines)
            {
                _lastLines.Enqueue(line.Trim());
                while (_lastLines.Count > 8) _lastLines.Dequeue();
            }
            var m = QuickTunnelUrl().Match(line);
            if (m.Success && !m.Value.StartsWith("https://api.", StringComparison.OrdinalIgnoreCase)) found.TrySetResult(m.Value);
        }
        process.OutputDataReceived += (_, e) => OnLine(e.Data);
        process.ErrorDataReceived += (_, e) => OnLine(e.Data);
        process.Exited += (_, _) =>
        {
            found.TrySetException(new InvalidOperationException("cloudflared stopped: " + LastLine()));
            lock (_lock)
            {
                if (!ReferenceEquals(_process, process)) return;
                _process = null;
            }
            if (Status is TunnelStatus.Online or TunnelStatus.Starting)
                Set(TunnelStatus.Error, null, "The tunnel stopped: " + LastLine(), DownloadProgress);
        };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("cloudflared did not start.");
        }
        catch (Exception e)
        {
            process.Dispose();
            Set(TunnelStatus.Error, null, "Could not start cloudflared: " + e.Message, DownloadProgress);
            throw;
        }
        lock (_lock) _process = process;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            var url = await found.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
            Set(TunnelStatus.Online, url.TrimEnd('/'), null, DownloadProgress);
            _log("system", "Tunnel online at " + url);
        }
        catch (Exception e)
        {
            Stop();
            var message = e is TimeoutException ? "cloudflared did not report an address in time. " + LastLine() : e.Message;
            Set(TunnelStatus.Error, null, message, DownloadProgress);
            throw new InvalidOperationException(message, e);
        }
    }

    private string LastLine()
    {
        lock (_lastLines) return _lastLines.Count == 0 ? "" : _lastLines.Last();
    }

    public void Stop()
    {
        Process? p;
        lock (_lock)
        {
            p = _process;
            _process = null;
        }
        if (p is not null)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
            try { p.Dispose(); } catch { }
            _log("system", "Tunnel stopped");
        }
        if (Status != TunnelStatus.Downloading) Set(TunnelStatus.Off, null, null, DownloadProgress);
    }

    private void Set(TunnelStatus status, string? url, string? error, double progress)
    {
        Status = status;
        PublicUrl = url;
        Error = error;
        DownloadProgress = progress;
        Changed?.Invoke();
    }

    public void Dispose() => Stop();
}
