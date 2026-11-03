using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using PocketAgent.Core.Agent;
using PocketAgent.Core.Tools;

namespace PocketAgent.Core.Providers;

/// <summary>The wire format a provider speaks.</summary>
public enum ApiStyle { Anthropic, OpenAiChat, OpenAiResponses }

public sealed class ChatRequest
{
    public required string SystemPrompt { get; init; }
    public required IReadOnlyList<ChatMessage> Messages { get; init; }
    public IReadOnlyList<ToolDefinition> Tools { get; init; } = [];
    public int MaxOutputTokens { get; init; } = 4096;
}

public sealed class ChatResult
{
    public List<ContentPart> Parts { get; } = new();
    public string StopReason { get; init; } = "";
    public TokenUsage Usage { get; init; } = TokenUsage.Zero;
    public string Text => string.Join("\n", Parts.OfType<TextPart>().Select(t => t.Text).Where(t => t.Length > 0)).Trim();
    public IEnumerable<ToolCallPart> ToolCalls => Parts.OfType<ToolCallPart>();
}

public interface IChatProvider
{
    string DisplayName { get; }
    string Model { get; }
    Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken ct);
    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct);
}

public sealed class ProviderOptions
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required ApiStyle Style { get; init; }
    public required string BaseUrl { get; init; }
    public string ApiKey { get; init; } = "";
    public required string Model { get; init; }
    public Dictionary<string, string> ExtraHeaders { get; init; } = new();
}

public sealed class ProviderException : Exception
{
    public ProviderException(string message, int? status = null, string? body = null) : base(message)
    {
        StatusCode = status;
        Body = body;
        var m = (message + " " + body).ToLowerInvariant();
        IsContextTooLong = m.Contains("prompt is too long") || m.Contains("context length") || m.Contains("context_length")
            || m.Contains("maximum context") || m.Contains("too many tokens") || m.Contains("input is too long")
            || m.Contains("context window") || m.Contains("request too large") || status == 413;
        IsAuth = status is 401 or 403;
        IsModelNotFound = status == 404 || (m.Contains("model") && (m.Contains("not found") || m.Contains("does not exist") || m.Contains("not_found") || m.Contains("unknown model")));
    }
    public int? StatusCode { get; }
    public string? Body { get; }
    public bool IsContextTooLong { get; }
    public bool IsAuth { get; }
    public bool IsModelNotFound { get; }

    /// <summary>A short message suitable for the chat and for speaking.</summary>
    public string Friendly
    {
        get
        {
            if (IsAuth) return "The AI provider rejected the API key. Check it in Settings → AI model.";
            if (IsModelNotFound) return "The AI provider doesn't know this model. Pick one with “Load models” in Settings → AI model.";
            if (StatusCode == 429) return "The AI provider is rate-limiting requests. Wait a moment and try again.";
            return Message;
        }
    }
}

/// <summary>HTTP + JSON plumbing shared by every provider, with retries for overload and rate limits.</summary>
public static class HttpJson
{
    private static readonly Lazy<HttpClient> SharedClient = new(() =>
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.All,
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PocketAgent", ToolCatalog.ServerVersion));
        return client;
    });

    public static HttpClient Client => SharedClient.Value;

    /// <summary>Overridable for tests.</summary>
    public static Func<int, TimeSpan> BackoffForAttempt { get; set; } =
        attempt => TimeSpan.FromMilliseconds(Math.Min(16000, 800 * Math.Pow(2, attempt)) + Random.Shared.Next(0, 300));

    public static TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(180);

    private static bool Retryable(HttpStatusCode code) => (int)code is 408 or 409 or 425 or 429 or 500 or 502 or 503 or 504 or 520 or 522 or 524 or 529;

    public static async Task<JsonNode> SendAsync(Func<HttpRequestMessage> makeRequest, CancellationToken ct, int maxAttempts = 4)
    {
        for (int attempt = 0; ; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(RequestTimeout);
            HttpResponseMessage? response = null;
            string body;
            try
            {
                using var request = makeRequest();
                response = await Client.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
                body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException)
            {
                response?.Dispose();
                if (attempt + 1 < maxAttempts)
                {
                    await Task.Delay(BackoffForAttempt(attempt), ct).ConfigureAwait(false);
                    continue;
                }
                var what = e is OperationCanceledException ? "timed out" : "could not be reached";
                throw new ProviderException($"The AI provider {what}: {e.Message}");
            }

            using (response)
            {
                if (response.IsSuccessStatusCode)
                {
                    try { return JsonNode.Parse(body) ?? throw new ProviderException("The AI provider returned an empty response."); }
                    catch (JsonException) { throw new ProviderException("The AI provider returned a response that isn't JSON.", (int)response.StatusCode, Clip(body)); }
                }
                if (Retryable(response.StatusCode) && attempt + 1 < maxAttempts)
                {
                    var wait = RetryAfter(response) ?? BackoffForAttempt(attempt);
                    await Task.Delay(wait, ct).ConfigureAwait(false);
                    continue;
                }
                throw new ProviderException(
                    $"AI provider error {(int)response.StatusCode}: {ExtractError(body) ?? response.ReasonPhrase}",
                    (int)response.StatusCode, Clip(body));
            }
        }
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var ra = response.Headers.RetryAfter;
        if (ra?.Delta is TimeSpan d) return d > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : d;
        if (ra?.Date is DateTimeOffset when)
        {
            var delta = when - DateTimeOffset.UtcNow;
            if (delta > TimeSpan.Zero) return delta > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : delta;
        }
        return null;
    }

    public static string? ExtractError(string body)
    {
        try
        {
            var node = JsonNode.Parse(body);
            if (node is JsonArray arr && arr.Count > 0) node = arr[0];
            var error = node?["error"];
            if (error is JsonValue v && v.TryGetValue(out string? s)) return s;
            if (error?["message"] is JsonValue m && m.TryGetValue(out string? msg)) return msg;
            if (node?["message"] is JsonValue m2 && m2.TryGetValue(out string? msg2)) return msg2;
            if (node?["detail"] is JsonValue d && d.TryGetValue(out string? det)) return det;
        }
        catch (JsonException) { }
        var trimmed = body.Trim();
        return trimmed.Length == 0 ? null : Clip(trimmed, 300);
    }

    private static string Clip(string s, int max = 2000) => s.Length <= max ? s : s[..max] + "…";

    public static StringContent JsonContent(JsonNode node)
        => new(node.ToJsonString(), System.Text.Encoding.UTF8, "application/json");

    public static string JoinUrl(string baseUrl, string path)
        => baseUrl.TrimEnd('/') + "/" + path.TrimStart('/');

    /// <summary>A function schema with empty properties removed, which some OpenAI-compatible servers reject.</summary>
    public static JsonObject? CompactSchema(ToolDefinition tool)
    {
        if (!tool.HasParameters) return null;
        var schema = (JsonObject)tool.InputSchema.DeepClone();
        if (schema["required"] is JsonArray req && req.Count == 0) schema.Remove("required");
        return schema;
    }
}
