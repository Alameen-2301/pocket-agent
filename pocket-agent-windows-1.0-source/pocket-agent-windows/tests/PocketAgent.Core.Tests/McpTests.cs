using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using PocketAgent.Core.Mcp;
using PocketAgent.Core.Tools;

namespace PocketAgent.Core.Tests;

/// <summary>End-to-end: HTTP → Kestrel → Rust engine (native library) → C# tool callback.</summary>
public sealed class McpTests
{
    private sealed class Host : IMcpHost
    {
        public bool ControlActive { get; set; } = true;
        public string Secret { get; set; } = new string('a', 64);
        public List<string> Logs { get; } = new();
        public void Log(string category, string message) { lock (Logs) Logs.Add(message); }
    }

    private static bool RustAvailable()
    {
        try { RustEngine.EnsureLoaded(); return true; }
        catch (DllNotFoundException) { return false; }
    }

    private static StringContent Rpc(string json) => new(json, Encoding.UTF8, "application/json");

    [Test]
    public async Task LoopbackServerEnforcesSecretAndRoutesToolCalls()
    {
        if (!RustAvailable()) throw new AssertException("Native Rust engine not found next to the tests (build rust-core first).");
        var host = new Host();
        var calls = new List<string>();
        var engine = new RustEngine((name, args) =>
        {
            lock (calls) calls.Add(name + " " + args);
            return ToolOutcome.Ok("Windows desktop: unlocked.").ToMcpJson();
        }, ToolCatalog.McpCatalogJson(), ToolCatalog.Instructions);
        int port = FakeHttpServer.FreePort();
        await using var server = new LocalMcpServer(host, engine, port);
        await server.StartAsync();
        using var http = new HttpClient();
        var url = $"http://127.0.0.1:{port}/k/{host.Secret}/mcp";

        // tools/list with the secret in the path
        var list = await http.PostAsync(url, Rpc("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}"));
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var tools = JsonNode.Parse(await list.Content.ReadAsStringAsync())!["result"]!["tools"]!.AsArray();
        Assert.Equal(ToolCatalog.All.Count, tools.Count);
        Assert.Equal("no-store", list.Headers.CacheControl?.ToString());

        // tools/call reaches the C# callback with its arguments
        var call = await http.PostAsync(url, Rpc("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"windows_status\",\"arguments\":{\"x\":1}}}"));
        var result = JsonNode.Parse(await call.Content.ReadAsStringAsync())!["result"]!;
        Assert.Equal("Windows desktop: unlocked.", result["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("complete", result["resultType"]!.GetValue<string>());
        Assert.Equal("windows_status {\"x\":1}", calls.Single());

        // Bearer header works too; the wrong secret does not
        var bearer = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/mcp") { Content = Rpc("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"ping\"}") };
        bearer.Headers.Authorization = new AuthenticationHeaderValue("Bearer", host.Secret);
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(bearer)).StatusCode);
        var wrong = await http.PostAsync($"http://127.0.0.1:{port}/k/{new string('b', 64)}/mcp", Rpc("{}"));
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        var noSecret = await http.PostAsync($"http://127.0.0.1:{port}/mcp", Rpc("{}"));
        Assert.Equal(HttpStatusCode.Unauthorized, noSecret.StatusCode);

        // Browsers (Origin header) are refused
        var browser = new HttpRequestMessage(HttpMethod.Post, url) { Content = Rpc("{}") };
        browser.Headers.Add("Origin", "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(browser)).StatusCode);

        // GET is not allowed; health works
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await http.GetAsync(url)).StatusCode);
        var health = await http.GetAsync($"http://127.0.0.1:{port}/k/{host.Secret}/health");
        Assert.Equal("{\"ok\":true}", await health.Content.ReadAsStringAsync());

        // Switching control off revokes everything at once
        host.ControlActive = false;
        Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsync(url, Rpc("{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"ping\"}"))).StatusCode);
    }

    [Test]
    public void ToolOutcomeSerializesTextAndImagesForMcp()
    {
        var o = ToolOutcome.Ok("Screenshot 10x10");
        o.Content.Add(new Agent.ImagePart("image/jpeg", "QUJD"));
        var node = JsonNode.Parse(o.ToMcpJson())!;
        Assert.Equal("image", node["content"]![1]!["type"]!.GetValue<string>());
        Assert.Equal("image/jpeg", node["content"]![1]!["mimeType"]!.GetValue<string>());
        Assert.Equal(false, node["isError"]!.GetValue<bool>());
    }
}
