using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace PocketAgent.Core.Mcp;

public interface IMcpHost
{
    bool ControlActive { get; }
    string Secret { get; }
    void Log(string category, string message);
}

/// <summary>
/// Loopback-only MCP endpoint (http://127.0.0.1:port/k/&lt;secret&gt;/mcp). Same rules as the original:
/// a 256-bit secret in the path or a Bearer header, browser requests (Origin header) refused,
/// nothing works while control is off.
/// </summary>
public sealed class LocalMcpServer : IAsyncDisposable
{
    private readonly IMcpHost _host;
    private readonly RustEngine _engine;
    private readonly int _port;
    private WebApplication? _app;
    private const int MaxBody = 1024 * 1024;

    public LocalMcpServer(IMcpHost host, RustEngine engine, int port)
    {
        _host = host;
        _engine = engine;
        _port = port;
    }

    public int Port => _port;
    public bool Running => _app is not null;

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [],
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, _port);
            options.Limits.MaxRequestBodySize = MaxBody;
            options.AddServerHeader = false;
        });
        builder.Logging.ClearProviders();
        builder.Services.Configure<KestrelServerOptions>(o => o.AllowSynchronousIO = false);
        var app = builder.Build();
        app.Run(Dispatch);
        await app.StartAsync().ConfigureAwait(false);
        _app = app;
    }

    private async Task Dispatch(HttpContext context)
    {
        var request = context.Request;
        var response = context.Response;
        response.Headers["Cache-Control"] = "no-store";
        response.Headers["X-Content-Type-Options"] = "nosniff";
        if (!_host.ControlActive || request.Headers.ContainsKey("Origin"))
        {
            response.StatusCode = 403;
            return;
        }
        var path = request.Path.Value?.TrimEnd('/') ?? "";
        string? pathSecret = null;
        if (path.StartsWith("/k/", StringComparison.Ordinal))
        {
            var rest = path[3..];
            var separator = rest.IndexOf('/');
            if (separator < 0) { response.StatusCode = 404; return; }
            pathSecret = rest[..separator];
            path = rest[separator..];
        }
        string? bearer = null;
        var auth = request.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) bearer = auth[7..].Trim();
        if (!TokenMatches(pathSecret) && !TokenMatches(bearer))
        {
            response.StatusCode = 401;
            response.Headers["WWW-Authenticate"] = "Bearer";
            _host.Log("mcp", "Refused a request without the valid secret");
            return;
        }
        if (path == "/health")
        {
            response.ContentType = "application/json";
            await response.WriteAsync("{\"ok\":true}").ConfigureAwait(false);
            return;
        }
        if (path != "/mcp") { response.StatusCode = 404; return; }
        if (request.Method != "POST") { response.StatusCode = 405; response.Headers["Allow"] = "POST"; return; }
        if (request.ContentLength > MaxBody) { response.StatusCode = 413; return; }
        string body;
        try
        {
            using var reader = new StreamReader(request.Body, Encoding.UTF8, false, leaveOpen: true);
            body = await reader.ReadToEndAsync(context.RequestAborted).ConfigureAwait(false);
            if (Encoding.UTF8.GetByteCount(body) > MaxBody) { response.StatusCode = 413; return; }
        }
        catch { response.StatusCode = 400; return; }
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in request.Headers) headers[header.Key.ToLowerInvariant()] = header.Value.ToString();
        try
        {
            var result = await Task.Run(() => _engine.Process(headers, body), context.RequestAborted).ConfigureAwait(false);
            if (!_host.ControlActive) { response.StatusCode = 403; return; }
            response.StatusCode = result.Status;
            if (!string.IsNullOrEmpty(result.Body))
            {
                response.ContentType = "application/json";
                await response.WriteAsync(result.Body).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            _host.Log("error", "MCP error: " + e.GetType().Name + ": " + e.Message);
            if (!response.HasStarted) response.StatusCode = 500;
        }
    }

    private bool TokenMatches(string? actual)
    {
        var secret = _host.Secret;
        if (!_host.ControlActive || actual is null || actual.Length != 64 || secret.Length != 64) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(secret));
    }

    public async Task StopAsync()
    {
        if (_app is { } app)
        {
            _app = null;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await app.StopAsync(timeout.Token).ConfigureAwait(false); }
            catch { }
            finally { await app.DisposeAsync().ConfigureAwait(false); }
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
