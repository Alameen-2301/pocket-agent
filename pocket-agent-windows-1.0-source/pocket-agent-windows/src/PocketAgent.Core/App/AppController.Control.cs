using System.Text.Json;
using PocketAgent.Core.Mcp;
using PocketAgent.Core.Tools;
using PocketAgent.Core.Tunnel;
using PocketAgent.Core.Util;

namespace PocketAgent.Core.App;

public sealed partial class AppController
{
    private volatile bool _controlActive;
    private bool _starting;
    private string _secret = "";
    private DateTimeOffset _startedAt;
    private DateTimeOffset? _expiresAt;
    private LocalMcpServer? _server;
    private string? _mcpError;
    private long _lastToolActivity;

    public bool ControlActive => _controlActive;
    public string Secret => _controlActive ? _secret : "";
    public int OwnProcessId => _p.OwnProcessId;

    public string LocalUrl => $"http://127.0.0.1:{_server?.Port ?? _settings.Current.Port}/k/{_secret}/mcp";

    public string? PublicUrl
    {
        get
        {
            var s = _settings.Current;
            if (!_controlActive || _server is null) return null;
            if (s.TunnelMode == "cloudflare" && _tunnel.Status == TunnelStatus.Online && _tunnel.PublicUrl is { } u) return $"{u}/k/{_secret}/mcp";
            if (s.TunnelMode == "custom" && Uri.TryCreate(s.CustomTunnelUrl.Trim(), UriKind.Absolute, out var custom) && custom.Scheme == Uri.UriSchemeHttps)
                return $"{custom.GetLeftPart(UriPartial.Authority)}{custom.AbsolutePath.TrimEnd('/')}/k/{_secret}/mcp";
            return null;
        }
    }

    public async Task StartControlAsync(string why = "You turned it on")
    {
        lock (_sync)
        {
            if (_controlActive || _starting) return;
            _starting = true;
        }
        try
        {
            var s = _settings.Current;
            string secret = "";
            if (s.ReuseSecret) secret = _settings.Reveal(s.SecretProtected);
            if (secret.Length != 64)
            {
                secret = Ids.Secret();
                if (s.ReuseSecret) _settings.Update(x => x.SecretProtected = _settings.Protect(secret));
            }
            lock (_sync)
            {
                _secret = secret;
                _startedAt = DateTimeOffset.Now;
                _expiresAt = s.AutoStopMinutes > 0 ? _startedAt.AddMinutes(s.AutoStopMinutes) : null;
                _controlActive = true;
                _mcpError = null;
            }
            Log("system", $"Desktop control is ON ({why}).");
            if (s.McpServerEnabled) await StartServerAsync(s.Port).ConfigureAwait(false);
            if (_server is not null && s.TunnelMode == "cloudflare") _ = StartTunnelAsync();
        }
        finally
        {
            lock (_sync) _starting = false;
            RequestPush();
        }
    }

    private async Task StartServerAsync(int port)
    {
        try
        {
            RustEngine.EnsureLoaded();
            var engine = new RustEngine(InvokeForMcp, ToolCatalog.McpCatalogJson(), ToolCatalog.Instructions);
            var server = new LocalMcpServer(this, engine, port);
            await server.StartAsync().ConfigureAwait(false);
            _server = server;
            Log("system", $"Connection server started on 127.0.0.1:{port} (this PC only).");
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            _mcpError = "pocket_agent_core.dll is missing or damaged next to the app, so other assistants can't connect. The built-in assistant still works.";
            Log("error", _mcpError);
        }
        catch (Exception e)
        {
            bool inUse = e.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase)
                         || e.InnerException?.GetType().Name == "AddressInUseException"
                         || e.Message.Contains("access permissions", StringComparison.OrdinalIgnoreCase);
            _mcpError = inUse
                ? $"Port {port} is already in use, so other assistants can't connect. Pick another port in Connections."
                : "The connection server could not start: " + e.Message;
            Log("error", _mcpError);
        }
    }

    public async Task StopControlAsync(string why = "You turned it off")
    {
        bool was;
        lock (_sync)
        {
            was = _controlActive;
            _controlActive = false; // revokes tool execution immediately
            _expiresAt = null;
        }
        _automationPaused = false;
        if (_agent.IsRunning) _agent.Stop();
        ResolveAllPrompts("deny");
        _approvals.ClearSession();
        _tunnel.Stop();
        var server = _server;
        _server = null;
        if (server is not null)
        {
            try { await server.StopAsync().ConfigureAwait(false); }
            catch (Exception e) { Log("error", "Stop warning: " + e.Message); }
        }
        if (!_settings.Current.ReuseSecret) _secret = "";
        if (was) Log("system", $"Desktop control is OFF ({why}). Session approvals cleared.");
        RequestPush();
    }

    public void ExtendControl(int minutes)
    {
        lock (_sync)
        {
            if (!_controlActive || _expiresAt is null) return;
            var from = _expiresAt.Value > DateTimeOffset.Now ? _expiresAt.Value : DateTimeOffset.Now;
            _expiresAt = from.AddMinutes(Math.Clamp(minutes, 1, 240));
        }
        Log("system", $"Auto-stop extended by {minutes} minutes.");
        RequestPush();
    }

    public void RotateSecret()
    {
        var fresh = Ids.Secret();
        lock (_sync) _secret = fresh;
        if (_settings.Current.ReuseSecret) _settings.Update(s => s.SecretProtected = _settings.Protect(fresh));
        Log("system", "Created a new connection secret. Old links stop working now.");
        RequestPush();
    }

    // ---------------------------------------------------------------- tunnel

    public async Task StartTunnelAsync()
    {
        var s = _settings.Current;
        if (!_controlActive || _server is null)
        {
            Toast("info", "Start desktop control first; the tunnel forwards to the connection server.");
            return;
        }
        var downloads = Path.Combine(_p.DataFolder, "bin");
        var exe = TunnelManager.Find(s.CloudflaredPath, downloads);
        if (exe is null)
        {
            Toast("info", "cloudflared isn't installed yet. Use “Download cloudflared” in Connections.");
            return;
        }
        try { await _tunnel.StartAsync(exe, _server.Port, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception e) { Log("error", "Tunnel: " + e.Message); }
        RequestPush();
    }

    public async Task DownloadCloudflaredAsync()
    {
        try
        {
            var path = await _tunnel.DownloadAsync(Path.Combine(_p.DataFolder, "bin"), CancellationToken.None).ConfigureAwait(false);
            Toast("success", "cloudflared downloaded.");
            if (_controlActive && _settings.Current.TunnelMode == "cloudflare") await StartTunnelAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Log("error", "cloudflared download failed: " + e.Message);
            Toast("error", "Download failed: " + e.Message);
        }
        RequestPush();
    }

    // ---------------------------------------------------------------- MCP tool calls from other assistants

    private string InvokeForMcp(string name, string argsJson)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson); }
        catch (JsonException) { return ToolOutcome.Fail("Invalid JSON arguments.").ToMcpJson(); }
        using (doc)
        {
            var argsNode = System.Text.Json.Nodes.JsonNode.Parse(doc.RootElement.GetRawText()) as System.Text.Json.Nodes.JsonObject ?? new();
            var step = new Agent.AgentStep
            {
                Id = Ids.New("step"),
                RunId = "mcp",
                Index = 0,
                Tool = name,
                Title = StepTitles.Describe(name, argsNode),
                Source = "mcp",
            };
            UpsertStep(step);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            ToolOutcome outcome;
            _toolGate.Wait();
            try
            {
                MarkToolActivity();
                outcome = ExecuteGuarded(name, doc.RootElement);
            }
            finally
            {
                MarkToolActivity();
                _toolGate.Release();
            }
            step.DurationMs = watch.ElapsedMilliseconds;
            step.Status = outcome.Interrupted ? "interrupted" : outcome.Denied ? "denied" : outcome.IsError ? "error" : "done";
            step.Detail = outcome.Summary;
            step.Thumbnail = outcome.Thumbnail;
            UpsertStep(step);
            return outcome.ToMcpJson();
        }
    }

    private void MarkToolActivity() => Interlocked.Exchange(ref _lastToolActivity, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    private bool RecentToolActivity(TimeSpan window)
        => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - Interlocked.Read(ref _lastToolActivity) < window.TotalMilliseconds;

    private ToolOutcome ExecuteGuarded(string name, JsonElement args)
    {
        if (!_controlActive)
            return ToolOutcome.Fail("Windows control is switched off. Ask the user to turn it on in Pocket Agent.");
        if (_automationPaused)
            return ToolOutcome.Fail("Paused: the user is using the PC. Ask them to press Resume in Pocket Agent (or say “continue”).", interrupted: true);
        try { return _tools.Execute(name, args); }
        catch (ToolDeniedException e) { return ToolOutcome.Fail(e.Message, denied: true); }
        catch (ToolInterruptedException e) { return ToolOutcome.Fail(e.Message, interrupted: true); }
        catch (Exception e) { return ToolOutcome.Fail("Tool error: " + e.Message); }
    }

    private async Task<ToolOutcome> RunToolForAgentAsync(string name, JsonElement args, CancellationToken ct)
    {
        await _toolGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            MarkToolActivity();
            return await Task.Run(() => ExecuteGuarded(name, args), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            MarkToolActivity();
            _toolGate.Release();
        }
    }

    /// <summary>For the UI: the snippet other assistants need, with the real secret.</summary>
    public string ClientConfig(string which)
    {
        var url = which.EndsWith("-public") ? PublicUrl ?? LocalUrl : LocalUrl;
        return which.Replace("-public", "") switch
        {
            "cursor" => JsonSerializer.Serialize(new { mcpServers = new Dictionary<string, object> { ["pocket-agent"] = new { url } } }, Json.Indented),
            "vscode" => JsonSerializer.Serialize(new { servers = new Dictionary<string, object> { ["pocket-agent"] = new { type = "http", url } } }, Json.Indented),
            "claude-desktop" => JsonSerializer.Serialize(new { mcpServers = new Dictionary<string, object> { ["pocket-agent"] = new { command = "npx", args = new[] { "-y", "mcp-remote", url } } } }, Json.Indented),
            "public" => PublicUrl ?? "",
            _ => url,
        };
    }
}
