using System.Text.Json;
using PocketAgent.Core.Util;

namespace PocketAgent.Core.Safety;

public sealed record ApprovalEntry(string Key, string Label, string Detail);

/// <summary>
/// Per-app approvals. "Always allow" keys are stored in approvals.json in the same format as the
/// original app (a JSON array of keys), so existing approvals carry over. A never-allow list in
/// blocked.json overrides everything.
/// </summary>
public sealed class ApprovalStore
{
    public const string WebsitesKey = "url:open";

    public static readonly string[] DefaultBlocked =
    [
        "keepass.exe", "keepassxc.exe", "1password.exe", "bitwarden.exe", "dashlane.exe",
        "enpass.exe", "nordpass.exe", "roboform.exe", "protonpass.exe", "lastpass.exe",
    ];

    private readonly object _lock = new();
    private readonly HashSet<string> _session = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _always = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _blocked = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _alwaysFile;
    private readonly string _blockedFile;
    private readonly Action<string>? _log;

    public event Action? Changed;

    public ApprovalStore(string folder, Action<string>? log = null)
    {
        _log = log;
        Directory.CreateDirectory(folder);
        _alwaysFile = Path.Combine(folder, "approvals.json");
        _blockedFile = Path.Combine(folder, "blocked.json");
        foreach (var key in ReadList(_alwaysFile) ?? []) _always.Add(key);
        var blocked = ReadList(_blockedFile);
        if (blocked is null)
        {
            foreach (var b in DefaultBlocked) _blocked.Add(b);
            Save(_blockedFile, _blocked);
        }
        else foreach (var b in blocked) _blocked.Add(b);
    }

    public static string AppKey(string exePath) => "exe:" + exePath.ToLowerInvariant();
    public static string LaunchKey(string entry) => "launch:" + entry.ToLowerInvariant();

    public bool IsAllowed(string key)
    {
        lock (_lock) return _session.Contains(key) || _always.Contains(key);
    }

    public void Allow(string key, bool always)
    {
        lock (_lock)
        {
            if (always)
            {
                _always.Add(key);
                _session.Remove(key);
                Save(_alwaysFile, _always);
            }
            else _session.Add(key);
        }
        Changed?.Invoke();
    }

    public void Revoke(string key)
    {
        bool changed;
        lock (_lock)
        {
            changed = _session.Remove(key) | _always.Remove(key);
            if (changed) Save(_alwaysFile, _always);
        }
        if (changed) Changed?.Invoke();
    }

    public void ClearSession()
    {
        lock (_lock) _session.Clear();
        Changed?.Invoke();
    }

    public bool IsBlocked(string exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return false;
        var file = Path.GetFileName(exePath.Replace('\\', '/'));
        lock (_lock) return _blocked.Contains(exePath) || _blocked.Contains(file);
    }

    /// <summary>Blocks by full path (contains a slash) or by file name ("app.exe" or "app").</summary>
    public string Block(string nameOrPath)
    {
        var entry = NormalizeBlockEntry(nameOrPath);
        if (entry.Length == 0) return entry;
        lock (_lock)
        {
            _blocked.Add(entry);
            // Blocking wins over earlier approvals for the same program.
            foreach (var key in _always.Where(k => KeyMatches(k, entry)).ToList()) _always.Remove(key);
            foreach (var key in _session.Where(k => KeyMatches(k, entry)).ToList()) _session.Remove(key);
            Save(_blockedFile, _blocked);
            Save(_alwaysFile, _always);
        }
        Changed?.Invoke();
        return entry;
    }

    public void Unblock(string entry)
    {
        bool changed;
        lock (_lock)
        {
            changed = _blocked.Remove(entry);
            if (changed) Save(_blockedFile, _blocked);
        }
        if (changed) Changed?.Invoke();
    }

    public IReadOnlyList<ApprovalEntry> SessionEntries { get { lock (_lock) return _session.OrderBy(x => x).Select(Describe).ToList(); } }
    public IReadOnlyList<ApprovalEntry> AlwaysEntries { get { lock (_lock) return _always.OrderBy(x => x).Select(Describe).ToList(); } }
    public IReadOnlyList<ApprovalEntry> BlockedEntries
    {
        get
        {
            lock (_lock)
                return _blocked.OrderBy(x => x).Select(b => new ApprovalEntry(b, Path.GetFileName(b.Replace('\\', '/')),
                    b.Contains('\\') || b.Contains('/') ? b : "Any program with this file name")).ToList();
        }
    }

    public static ApprovalEntry Describe(string key)
    {
        if (key.Equals(WebsitesKey, StringComparison.OrdinalIgnoreCase))
            return new ApprovalEntry(key, "Open websites", "In your default browser");
        if (key.StartsWith("exe:", StringComparison.OrdinalIgnoreCase))
        {
            var path = key[4..];
            return new ApprovalEntry(key, Path.GetFileName(path.Replace('\\', '/')), path);
        }
        if (key.StartsWith("launch:", StringComparison.OrdinalIgnoreCase))
        {
            var entry = key[7..];
            var name = entry.Contains('!') ? entry.Split('!')[0].Split('_')[0] : Path.GetFileNameWithoutExtension(entry.Replace('\\', '/'));
            return new ApprovalEntry(key, "Launch " + name, entry);
        }
        return new ApprovalEntry(key, key, "");
    }

    private static string NormalizeBlockEntry(string nameOrPath)
    {
        var s = nameOrPath.Trim().Trim('"').ToLowerInvariant();
        if (s.StartsWith("exe:")) s = s[4..];
        if (s.Length == 0) return "";
        if (s.Contains('\\') || s.Contains('/')) return s;
        return s.EndsWith(".exe") ? s : s + ".exe";
    }

    private static bool KeyMatches(string key, string blockEntry)
    {
        if (!key.StartsWith("exe:", StringComparison.OrdinalIgnoreCase)) return false;
        var path = key[4..];
        return path.Equals(blockEntry, StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(path.Replace('\\', '/')).Equals(blockEntry, StringComparison.OrdinalIgnoreCase);
    }

    private string[]? ReadList(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            return JsonSerializer.Deserialize<string[]>(File.ReadAllText(file)) ?? [];
        }
        catch (Exception e)
        {
            _log?.Invoke($"Could not read {Path.GetFileName(file)}: {e.Message}");
            return [];
        }
    }

    private void Save(string file, IEnumerable<string> values)
    {
        try { AtomicFile.WriteAllText(file, JsonSerializer.Serialize(values.OrderBy(x => x).ToArray())); }
        catch (Exception e) { _log?.Invoke($"Could not save {Path.GetFileName(file)}: {e.Message}"); }
    }
}
