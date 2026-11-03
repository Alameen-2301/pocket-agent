namespace PocketAgent.Core.Safety;

/// <summary>
/// A keyboard shortcut such as "ctrl+shift+t", parsed into Windows virtual-key codes.
/// Only a known set of keys is accepted, and chords that open a command prompt or lock the PC are refused.
/// </summary>
public sealed class KeyChord
{
    public const ushort VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B;

    private KeyChord(IReadOnlyList<ushort> modifiers, ushort key, string normalized)
    {
        Modifiers = modifiers;
        Key = key;
        Normalized = normalized;
    }

    public IReadOnlyList<ushort> Modifiers { get; }
    public ushort Key { get; }
    public string Normalized { get; }
    public bool Ctrl => Modifiers.Contains(VK_CONTROL);
    public bool Shift => Modifiers.Contains(VK_SHIFT);
    public bool Alt => Modifiers.Contains(VK_MENU);
    public bool Win => Modifiers.Contains(VK_LWIN);
    public string KeyName => Normalized.Split('+')[^1];

    private static readonly Dictionary<string, ushort> ModifierNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = VK_CONTROL, ["control"] = VK_CONTROL, ["strg"] = VK_CONTROL,
        ["shift"] = VK_SHIFT,
        ["alt"] = VK_MENU, ["option"] = VK_MENU,
        ["win"] = VK_LWIN, ["windows"] = VK_LWIN, ["meta"] = VK_LWIN, ["super"] = VK_LWIN, ["cmd"] = VK_LWIN,
    };

    private static readonly Dictionary<string, (ushort Vk, string Name)> KeyNames = BuildKeys();

    private static Dictionary<string, (ushort, string)> BuildKeys()
    {
        var k = new Dictionary<string, (ushort, string)>(StringComparer.OrdinalIgnoreCase);
        void Add(ushort vk, string name, params string[] aliases)
        {
            k[name] = (vk, name);
            foreach (var a in aliases) k[a] = (vk, name);
        }
        Add(0x08, "backspace", "bksp", "back_space");
        Add(0x09, "tab");
        Add(0x0D, "enter", "return");
        Add(0x13, "pause", "break");
        Add(0x1B, "esc", "escape");
        Add(0x20, "space", "spacebar");
        Add(0x21, "pageup", "pgup", "page_up", "prior");
        Add(0x22, "pagedown", "pgdn", "page_down", "next");
        Add(0x23, "end");
        Add(0x24, "home");
        Add(0x25, "left", "arrowleft", "left_arrow");
        Add(0x26, "up", "arrowup", "up_arrow");
        Add(0x27, "right", "arrowright", "right_arrow");
        Add(0x28, "down", "arrowdown", "down_arrow");
        Add(0x2D, "insert", "ins");
        Add(0x2E, "delete", "del");
        Add(0x5D, "menu", "apps", "contextmenu", "context_menu");
        Add(0xBB, "plus", "=", "equals");
        Add(0xBC, "comma", ",");
        Add(0xBD, "minus", "-", "hyphen");
        Add(0xBE, "period", ".", "dot");
        for (int i = 0; i < 26; i++) Add((ushort)(0x41 + i), ((char)('a' + i)).ToString());
        for (int i = 0; i < 10; i++) Add((ushort)(0x30 + i), i.ToString());
        for (int i = 1; i <= 24; i++) Add((ushort)(0x70 + i - 1), "f" + i);
        return k;
    }

    /// <summary>Keys that need KEYEVENTF_EXTENDEDKEY when sent with SendInput.</summary>
    public static bool IsExtended(ushort vk) => vk is 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28
        or 0x2D or 0x2E or 0x5B or 0x5C or 0x5D or 0x6F or 0x90 or 0xA3 or 0xA5;

    public static bool TryParse(string? text, out KeyChord chord, out string error)
    {
        chord = null!;
        error = "";
        if (string.IsNullOrWhiteSpace(text)) { error = "keys is required, e.g. enter or ctrl+s."; return false; }
        var raw = text.Trim().ToLowerInvariant().Replace(" ", "");
        // "ctrl++" means ctrl+plus.
        if (raw.EndsWith("++")) raw = raw[..^2] + "+plus";
        var tokens = raw.Split('+', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) { error = "No key given."; return false; }
        if (tokens.Length > 4) { error = "Use at most three modifiers and one key."; return false; }

        var mods = new List<ushort>();
        var names = new List<string>();
        for (int i = 0; i < tokens.Length - 1; i++)
        {
            if (!ModifierNames.TryGetValue(tokens[i], out var vk)) { error = $"'{tokens[i]}' is not a modifier (use ctrl, shift, alt or win)."; return false; }
            if (mods.Contains(vk)) continue;
            mods.Add(vk);
        }
        // Stable, readable order: ctrl, alt, shift, win.
        mods.Sort((a, b) => Order(a).CompareTo(Order(b)));
        foreach (var m in mods) names.Add(NameOf(m));

        var last = tokens[^1];
        ushort key;
        string keyName;
        if (ModifierNames.TryGetValue(last, out var lone) && tokens.Length == 1 && lone == VK_LWIN)
        {
            // A bare Windows key opens Start.
            key = VK_LWIN; keyName = "win";
            mods.Clear(); names.Clear();
        }
        else if (KeyNames.TryGetValue(last, out var k))
        {
            key = k.Vk; keyName = k.Name;
        }
        else
        {
            error = $"Unknown key '{last}'. Use names like enter, esc, tab, space, backspace, delete, up, down, left, right, home, end, pageup, pagedown, f1-f24, a-z or 0-9.";
            return false;
        }
        names.Add(keyName);
        chord = new KeyChord(mods, key, string.Join("+", names));
        return true;
    }

    private static int Order(ushort vk) => vk switch { VK_CONTROL => 0, VK_MENU => 1, VK_SHIFT => 2, _ => 3 };
    private static string NameOf(ushort vk) => vk switch { VK_CONTROL => "ctrl", VK_MENU => "alt", VK_SHIFT => "shift", _ => "win" };

    /// <summary>Chords that could run arbitrary programs or lock the user out are never sent.</summary>
    public bool IsBlocked(out string reason)
    {
        reason = "";
        if (Win && Key == 0x52) { reason = "Win+R opens the Run dialog, which can start any program, so it is blocked."; return true; }
        if (Win && Key == 0x58) { reason = "Win+X opens the power-user menu, so it is blocked."; return true; }
        if (Win && Key == 0x4C) { reason = "Win+L would lock the PC, so it is blocked."; return true; }
        if (Ctrl && Alt && Key == 0x2E) { reason = "Ctrl+Alt+Delete cannot be sent."; return true; }
        if (Ctrl && Shift && Key == 0x1B) { reason = "Task Manager (Ctrl+Shift+Esc) is blocked."; return true; }
        return false;
    }

    /// <summary>"ctrl+shift+t" → "Ctrl+Shift+T" for display.</summary>
    public static string Pretty(string keys)
    {
        if (!TryParse(keys, out var chord, out _)) return keys;
        return string.Join("+", chord.Normalized.Split('+').Select(p => p.Length <= 1 ? p.ToUpperInvariant() : p switch
        {
            "ctrl" => "Ctrl", "alt" => "Alt", "shift" => "Shift", "win" => "Win", "esc" => "Esc",
            "pageup" => "Page Up", "pagedown" => "Page Down",
            _ when p.StartsWith('f') && p.Length <= 3 && char.IsDigit(p[^1]) => p.ToUpperInvariant(),
            _ => char.ToUpperInvariant(p[0]) + p[1..],
        }));
    }
}
