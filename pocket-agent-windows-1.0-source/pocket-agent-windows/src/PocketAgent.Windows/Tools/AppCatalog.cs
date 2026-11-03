using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace PocketAgent.Windows.Tools;

internal sealed record InstalledApp(string Name, string Entry, bool IsAppsFolderId);

/// <summary>
/// Apps the assistant may open: a few built-ins, Start menu shortcuts (like the original) and the
/// shell's AppsFolder, which also lists Microsoft Store apps. Arbitrary paths are never accepted.
/// </summary>
internal static class AppCatalog
{
    private static List<InstalledApp>? _cache;
    private static DateTime _cachedAt;
    private static readonly object Lock = new();

    public static IReadOnlyList<InstalledApp> All()
    {
        lock (Lock)
        {
            if (_cache is not null && DateTime.UtcNow - _cachedAt < TimeSpan.FromMinutes(2)) return _cache;
            var apps = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);
            string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            void Add(string name, string entry, bool appsFolder = false)
            {
                if (!string.IsNullOrWhiteSpace(name) && !apps.ContainsKey(name)) apps[name] = new InstalledApp(name, entry, appsFolder);
            }
            Add("Notepad", Path.Combine(winDir, "System32", "notepad.exe"));
            Add("Calculator", Path.Combine(winDir, "System32", "calc.exe"));
            Add("Paint", Path.Combine(winDir, "System32", "mspaint.exe"));
            Add("File Explorer", Path.Combine(winDir, "explorer.exe"));
            Add("Settings", "ms-settings:");
            foreach (var dir in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                         Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                     })
            {
                try
                {
                    foreach (var shortcut in Directory.EnumerateFiles(dir, "*.lnk", SearchOption.AllDirectories))
                        Add(Path.GetFileNameWithoutExtension(shortcut), shortcut);
                }
                catch (UnauthorizedAccessException) { }
                catch (DirectoryNotFoundException) { }
                catch (IOException) { }
            }
            foreach (var (name, id) in AppsFolder()) Add(name, id, appsFolder: true);
            _cache = apps.Values.OrderBy(a => a.Name).ToList();
            _cachedAt = DateTime.UtcNow;
            return _cache;
        }
    }

    /// <summary>shell:AppsFolder through the Shell.Application COM object (desktop and Store apps).</summary>
    private static List<(string Name, string Id)> AppsFolder()
    {
        var list = new List<(string, string)>();
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type is null) return list;
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic folder = shell.NameSpace("shell:AppsFolder");
            if (folder is null) return list;
            // Indexed access instead of foreach: it doesn't depend on COM enumerator marshalling.
            dynamic items = folder.Items();
            int count = items.Count;
            for (int i = 0; i < count && i < 5000; i++)
            {
                dynamic item = items.Item(i);
                if (item is null) continue;
                string name = item.Name;
                string path = item.Path;
                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(path)) list.Add((name, path));
            }
        }
        catch (Exception e)
        {
            CrashLog.Info("AppsFolder listing failed: " + e.Message);
        }
        return list;
    }

    public static InstalledApp? Find(string name)
        => All().FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Close matches for a name that wasn't found exactly.</summary>
    public static IReadOnlyList<string> Suggest(string name, int count = 6)
    {
        var lower = name.ToLowerInvariant();
        return All()
            .Select(a => (a.Name, Score: Score(lower, a.Name.ToLowerInvariant())))
            .Where(x => x.Score < 100)
            .OrderBy(x => x.Score).ThenBy(x => x.Name.Length)
            .Take(count)
            .Select(x => x.Name)
            .ToList();
    }

    private static int Score(string query, string candidate)
    {
        if (candidate.Contains(query)) return candidate.StartsWith(query) ? 0 : 1;
        if (query.Contains(candidate) && candidate.Length >= 3) return 2;
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 0 && words.All(candidate.Contains)) return 3;
        int d = Levenshtein(query, candidate.Length > query.Length + 4 ? candidate[..(query.Length + 4)] : candidate);
        return d <= Math.Max(2, query.Length / 3) ? 10 + d : 100;
    }

    private static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    public static void Launch(InstalledApp app)
    {
        if (app.IsAppsFolderId)
        {
            // AUMIDs and known-folder paths both open through the AppsFolder shell namespace.
            var entry = app.Entry.Replace("\"", "");
            Process.Start(new ProcessStartInfo("explorer.exe", "\"shell:AppsFolder\\" + entry + "\"") { UseShellExecute = false });
        }
        else
        {
            Process.Start(new ProcessStartInfo(app.Entry) { UseShellExecute = true });
        }
        lock (Lock) _cachedAt = DateTime.MinValue;
    }
}
