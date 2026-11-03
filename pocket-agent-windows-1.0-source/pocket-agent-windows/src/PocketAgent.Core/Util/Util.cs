using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PocketAgent.Core.Util;

public static class AtomicFile
{
    /// <summary>Writes to a temp file then swaps it in, so a crash never leaves half a settings file.</summary>
    public static void WriteAllText(string path, string content)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        File.WriteAllText(temp, content, new UTF8Encoding(false));
        try { File.Move(temp, path, overwrite: true); }
        catch
        {
            try { File.Delete(temp); } catch { }
            throw;
        }
    }
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static readonly JsonSerializerOptions Indented = new(Options) { WriteIndented = true };

    public static string? Str(this JsonNode? node, string key)
        => node is JsonObject o && o[key] is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    public static double? Num(this JsonNode? node, string key)
    {
        if (node is not JsonObject o || o[key] is not JsonValue v) return null;
        if (v.TryGetValue(out double d)) return d;
        if (v.TryGetValue(out long l)) return l;
        if (v.TryGetValue(out int i)) return i;
        if (v.TryGetValue(out string? s) && double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var p)) return p;
        return null;
    }

    public static bool? Bool(this JsonNode? node, string key)
        => node is JsonObject o && o[key] is JsonValue v && v.TryGetValue(out bool b) ? b : null;

    public static long Long(this JsonNode? node, string key) => (long)(Num(node, key) ?? 0);

    /// <summary>Parses tool arguments; returns an empty object for null/empty input and reports invalid JSON.</summary>
    public static JsonObject ParseArguments(string? raw, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(raw)) return new JsonObject();
        try
        {
            var node = JsonNode.Parse(raw);
            if (node is JsonObject o) return o;
            error = "Tool arguments must be a JSON object.";
            return new JsonObject();
        }
        catch (JsonException e)
        {
            error = "Invalid JSON arguments: " + e.Message;
            return new JsonObject();
        }
    }
}

public static class Ids
{
    public static string New(string prefix) => prefix + "_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
    public static string Secret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
}

/// <summary>An awaitable open/closed gate used to pause the agent loop.</summary>
public sealed class PauseGate
{
    private readonly object _lock = new();
    private TaskCompletionSource _open = NewOpen();

    private static TaskCompletionSource NewOpen()
    {
        var t = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        t.SetResult();
        return t;
    }

    public bool IsPaused { get { lock (_lock) return !_open.Task.IsCompleted; } }

    public void Pause()
    {
        lock (_lock)
            if (_open.Task.IsCompleted) _open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void Resume()
    {
        lock (_lock) _open.TrySetResult();
    }

    public Task WaitAsync(CancellationToken ct)
    {
        Task t;
        lock (_lock) t = _open.Task;
        return t.IsCompleted ? Task.CompletedTask : t.WaitAsync(ct);
    }
}

public static class TextUtil
{
    public static string Clip(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text.Length <= max ? text : text[..max] + "…";
    }

    /// <summary>Turns a model reply into something pleasant to hear: no markdown, no raw URLs.</summary>
    public static string ForSpeech(string text, int maxChars = 700)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var s = System.Text.RegularExpressions.Regex.Replace(text, @"```[\s\S]*?```", " ");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"https?://\S+", "the link");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"[*_#`>|~]+", "");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\[(.*?)\]\((.*?)\)", "$1");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
        if (s.Length <= maxChars) return s;
        // Cut at a sentence boundary when possible.
        var cut = s.LastIndexOfAny(['.', '!', '?'], maxChars);
        return cut > maxChars / 2 ? s[..(cut + 1)] : s[..maxChars] + "…";
    }
}
