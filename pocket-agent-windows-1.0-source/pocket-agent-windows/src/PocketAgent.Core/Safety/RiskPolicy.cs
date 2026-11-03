using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PocketAgent.Core.Safety;

/// <summary>
/// Decides whether a click or shortcut needs the user's confirmation. The check looks at the
/// label of the button, link or menu item under the pointer, so it is enforced by the app
/// rather than left to the model's instructions.
/// </summary>
public sealed class RiskPolicy
{
    /// <summary>English and German phrases that start or end the label of a risky control.</summary>
    public static readonly string[] DefaultPhrases =
    [
        "send", "send now", "send message", "send email",
        "delete", "delete all", "delete account", "remove", "erase", "discard", "permanently delete", "empty recycle bin",
        "pay", "pay now", "buy", "buy now", "purchase", "place order", "order now", "checkout", "check out", "complete purchase",
        "submit", "confirm", "confirm purchase", "confirm payment", "transfer", "send money",
        "publish", "post", "tweet", "share", "uninstall", "unsubscribe", "close account",
        // German
        "senden", "absenden", "jetzt senden", "nachricht senden", "e-mail senden",
        "löschen", "endgültig löschen", "entfernen", "verwerfen",
        "bezahlen", "jetzt bezahlen", "kaufen", "jetzt kaufen", "bestellen", "zahlungspflichtig bestellen",
        "kostenpflichtig bestellen", "jetzt bestellen", "zur kasse", "bestätigen", "überweisen", "abschicken",
        "veröffentlichen", "posten", "teilen", "deinstallieren", "abmelden vom newsletter", "konto schließen",
    ];

    private readonly List<string[]> _phrases;

    public RiskPolicy(IEnumerable<string>? phrases = null)
    {
        _phrases = (phrases ?? DefaultPhrases)
            .Select(Normalize)
            .Where(p => p.Length > 0)
            .Distinct()
            .Select(p => p.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .OrderByDescending(p => p.Length) // the most specific phrase wins ("jetzt kaufen" over "kaufen")
            .ToList();
    }

    public static RiskPolicy FromSetting(string? commaSeparated)
    {
        if (string.IsNullOrWhiteSpace(commaSeparated)) return new RiskPolicy();
        var list = commaSeparated.Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return list.Length == 0 ? new RiskPolicy() : new RiskPolicy(list);
    }

    public static string DefaultSetting => string.Join(", ", DefaultPhrases);

    /// <summary>
    /// Returns the matched phrase when the label of an actionable control (button, link, menu item)
    /// starts or ends with a risky phrase as whole words; otherwise null.
    /// </summary>
    public string? MatchLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;
        var normalized = Normalize(label);
        if (normalized.Length == 0 || normalized.Length > 80) return null; // long text is content, not a button label
        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var phrase in _phrases)
        {
            if (phrase.Length > words.Length) continue;
            if (StartsWith(words, phrase) || EndsWith(words, phrase)) return string.Join(' ', phrase);
        }
        return null;
    }

    /// <summary>Shortcuts that send or delete in common apps.</summary>
    public static string? MatchChord(KeyChord chord, string foregroundExe)
    {
        var exe = foregroundExe.ToLowerInvariant();
        bool mailOrChat = exe is "outlook.exe" or "olk.exe" or "thunderbird.exe" or "teams.exe" or "ms-teams.exe"
            or "slack.exe" or "discord.exe" or "whatsapp.exe" or "telegram.exe" or "signal.exe";
        if (chord.Ctrl && !chord.Alt && chord.Key == 0x0D) return "Ctrl+Enter usually sends";
        if (chord.Alt && !chord.Ctrl && chord.Key == 0x53 && mailOrChat) return "Alt+S sends in this app";
        if (chord.Key == 0x2E && exe == "explorer.exe") return chord.Shift ? "Shift+Delete permanently deletes files" : "Delete removes the selected files";
        if (chord.Key == 0x2E && chord.Shift) return "Shift+Delete permanently deletes";
        return null;
    }

    private static bool StartsWith(string[] words, string[] phrase)
    {
        for (int i = 0; i < phrase.Length; i++) if (words[i] != phrase[i]) return false;
        return true;
    }

    private static bool EndsWith(string[] words, string[] phrase)
    {
        int offset = words.Length - phrase.Length;
        for (int i = 0; i < phrase.Length; i++) if (words[offset + i] != phrase[i]) return false;
        return true;
    }

    private static readonly Regex Shortcut = new(@"\((ctrl|strg|alt|shift)[^)]*\)|\b(ctrl|strg)\s*\+\s*\S+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Lower-case words only: punctuation, icons and shortcut hints are dropped.</summary>
    internal static string Normalize(string text)
    {
        text = Shortcut.Replace(text, " ");
        var sb = new StringBuilder(text.Length);
        foreach (var c in text.ToLower(CultureInfo.InvariantCulture))
        {
            if (char.IsLetterOrDigit(c) || c == '-') sb.Append(c);
            else sb.Append(' ');
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
