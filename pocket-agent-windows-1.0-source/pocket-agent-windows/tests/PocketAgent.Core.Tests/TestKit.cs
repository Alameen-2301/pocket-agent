using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace PocketAgent.Core.Tests;

[AttributeUsage(AttributeTargets.Method)]
public sealed class TestAttribute : Attribute { }

public sealed class AssertException : Exception
{
    public AssertException(string message) : base(message) { }
}

public static class Assert
{
    public static void True(bool condition, string what = "condition")
    {
        if (!condition) throw new AssertException("Expected true: " + what);
    }

    public static void False(bool condition, string what = "condition")
    {
        if (condition) throw new AssertException("Expected false: " + what);
    }

    public static void Equal<T>(T expected, T actual, string what = "value")
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new AssertException($"{what}: expected <{expected}> but was <{actual}>");
    }

    public static void Contains(string needle, string? haystack, string what = "text")
    {
        if (haystack is null || !haystack.Contains(needle, StringComparison.Ordinal))
            throw new AssertException($"{what}: expected to contain <{needle}> but was <{Clip(haystack)}>");
    }

    public static void DoesNotContain(string needle, string? haystack, string what = "text")
    {
        if (haystack is not null && haystack.Contains(needle, StringComparison.Ordinal))
            throw new AssertException($"{what}: must not contain <{needle}> but was <{Clip(haystack)}>");
    }

    public static T Throws<T>(Action action, string what = "action") where T : Exception
    {
        try { action(); }
        catch (T e) { return e; }
        catch (Exception e) { throw new AssertException($"{what}: expected {typeof(T).Name} but got {e.GetType().Name}: {e.Message}"); }
        throw new AssertException($"{what}: expected {typeof(T).Name} but nothing was thrown");
    }

    public static async Task Eventually(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
        if (!condition()) throw new AssertException("Timed out waiting for: " + what);
    }

    private static string Clip(string? s) => s is null ? "null" : s.Length > 600 ? s[..600] + "…" : s;
}

/// <summary>A tiny HTTP server for faking AI providers.</summary>
public sealed class FakeHttpServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    public List<(string Method, string Path, string Body, Dictionary<string, string> Headers)> Requests { get; } = new();
    public string BaseUrl { get; }

    private FakeHttpServer(WebApplication app, string baseUrl)
    {
        _app = app;
        BaseUrl = baseUrl;
    }

    public static async Task<FakeHttpServer> StartAsync(Func<HttpContext, string, int, Task> handler)
    {
        int port = FreePort();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, port));
        builder.Logging.ClearProviders();
        var app = builder.Build();
        FakeHttpServer? self = null;
        int count = 0;
        app.Run(async ctx =>
        {
            using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            var headers = ctx.Request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => h.Value.ToString());
            int index;
            lock (self!.Requests)
            {
                self.Requests.Add((ctx.Request.Method, ctx.Request.Path + ctx.Request.QueryString, body, headers));
                index = count++;
            }
            await handler(ctx, body, index);
        });
        self = new FakeHttpServer(app, $"http://127.0.0.1:{port}");
        await app.StartAsync();
        return self;
    }

    public static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var filter = args.FirstOrDefault();
        var tests = typeof(Program).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
            .Where(m => m.GetCustomAttribute<TestAttribute>() is not null)
            .Where(m => filter is null || $"{m.DeclaringType!.Name}.{m.Name}".Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => m.DeclaringType!.Name).ThenBy(m => m.Name)
            .ToList();
        int passed = 0, failed = 0;
        foreach (var test in tests)
        {
            var name = $"{test.DeclaringType!.Name}.{test.Name}";
            var sw = Stopwatch.StartNew();
            object? instance = null;
            try
            {
                instance = test.IsStatic ? null : Activator.CreateInstance(test.DeclaringType!);
                var result = test.Invoke(instance, null);
                if (result is Task task)
                {
                    var finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(60)));
                    if (finished != task) throw new AssertException("Test timed out after 60 s");
                    await task;
                }
                await DisposeAsync(instance);
                instance = null;
                passed++;
                Console.WriteLine($"PASS {name} ({sw.ElapsedMilliseconds} ms)");
            }
            catch (Exception e)
            {
                var inner = e is TargetInvocationException tie && tie.InnerException is not null ? tie.InnerException : e;
                failed++;
                Console.WriteLine($"FAIL {name}: {inner.GetType().Name}: {inner.Message}");
                if (inner is not AssertException) Console.WriteLine(inner.StackTrace);
                try { await DisposeAsync(instance); } catch { }
            }
        }
        Console.WriteLine($"\n{passed} passed, {failed} failed, {tests.Count} total");
        return failed == 0 ? 0 : 1;
    }

    private static async Task DisposeAsync(object? instance)
    {
        if (instance is IAsyncDisposable ad) await ad.DisposeAsync();
        else if (instance is IDisposable d) d.Dispose();
    }
}
