using System.Text.Json;
using System.Text.Json.Nodes;
using PocketAgent.Core.Agent;
using PocketAgent.Core.Safety;
using PocketAgent.Core.Settings;
using PocketAgent.Core.Tools;
using PocketAgent.Core.Voice;

namespace PocketAgent.Core.Tests;

public sealed class FakeProtector : ISecretProtector
{
    public string Protect(string plain) => "enc:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(new string(plain.Reverse().ToArray())));
    public string Unprotect(string value) => new(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(value[4..])).Reverse().ToArray());
}

public static class TempDir
{
    public static string New()
    {
        var d = Path.Combine(Path.GetTempPath(), "pa-tests", Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(d);
        return d;
    }
}

public static class SafetyTests
{
    [Test]
    public static void KeyChordParsesCommonShortcuts()
    {
        Assert.True(KeyChord.TryParse("Ctrl+Shift+T", out var c, out _), "parse");
        Assert.Equal("ctrl+shift+t", c.Normalized);
        Assert.Equal((ushort)0x54, c.Key);
        Assert.True(c.Ctrl && c.Shift && !c.Alt, "modifiers");
        Assert.True(KeyChord.TryParse("shift+ctrl+t", out var d, out _), "order");
        Assert.Equal("ctrl+shift+t", d.Normalized);
        Assert.True(KeyChord.TryParse("enter", out var e, out _), "enter");
        Assert.Equal((ushort)0x0D, e.Key);
        Assert.True(KeyChord.TryParse("PgDn", out var f, out _), "pgdn");
        Assert.True(KeyChord.IsExtended(f.Key), "page down is extended");
        Assert.True(KeyChord.TryParse("win", out var g, out _), "bare win");
        Assert.Equal((ushort)0x5B, g.Key);
        Assert.True(KeyChord.TryParse("ctrl++", out var h, out _), "ctrl++");
        Assert.Equal("ctrl+plus", h.Normalized);
        Assert.True(KeyChord.TryParse("alt+f4", out var i, out _), "alt+f4");
        Assert.Equal((ushort)0x73, i.Key);
        Assert.False(KeyChord.TryParse("ctrl+banana", out _, out var err), "unknown key");
        Assert.Contains("Unknown key", err);
        Assert.False(KeyChord.TryParse("foo+a", out _, out _), "unknown modifier");
        Assert.Equal("Ctrl+Shift+T", KeyChord.Pretty("ctrl+shift+t"));
        Assert.Equal("Page Down", KeyChord.Pretty("pagedown"));
        Assert.Equal("F5", KeyChord.Pretty("f5"));
    }

    [Test]
    public static void KeyChordBlocksDangerousChords()
    {
        foreach (var bad in new[] { "win+r", "win+x", "win+l", "ctrl+alt+delete", "ctrl+shift+esc" })
        {
            Assert.True(KeyChord.TryParse(bad, out var c, out _), bad);
            Assert.True(c.IsBlocked(out _), bad + " blocked");
        }
        Assert.True(KeyChord.TryParse("ctrl+c", out var ok, out _), "ctrl+c");
        Assert.False(ok.IsBlocked(out _), "ctrl+c allowed");
    }

    [Test]
    public static void RiskPolicyMatchesButtonLabelsNotContent()
    {
        var p = new RiskPolicy();
        Assert.Equal("send", p.MatchLabel("Send"));
        Assert.Equal("send", p.MatchLabel("Send (Ctrl+Enter)"));
        Assert.Equal("delete account", p.MatchLabel("Delete account"), "longest phrase");
        Assert.Equal("delete", p.MatchLabel("Delete draft"), "starts with");
        Assert.Equal("jetzt kaufen", p.MatchLabel("Jetzt kaufen"));
        Assert.Equal("senden", p.MatchLabel("Antwort senden"), "German verb at end");
        Assert.Equal("place order", p.MatchLabel("Place order"));
        Assert.Equal(null, p.MatchLabel("Sender"), "Sender is not send");
        Assert.Equal(null, p.MatchLabel("Sort order"), "sort order is not an order");
        Assert.Equal(null, p.MatchLabel("Posteingang"), "inbox");
        Assert.Equal(null, p.MatchLabel("Sign in"), "sign in");
        Assert.Equal(null, p.MatchLabel(new string('x', 30) + " send " + new string('y', 70)), "long text is content");
        var custom = RiskPolicy.FromSetting("archive, mark as spam");
        Assert.Equal("mark as spam", custom.MatchLabel("Mark as spam"));
        Assert.Equal(null, custom.MatchLabel("Send"), "custom list replaces defaults");
    }

    [Test]
    public static void RiskPolicyFlagsSendAndDeleteShortcuts()
    {
        KeyChord.TryParse("ctrl+enter", out var send, out _);
        Assert.True(RiskPolicy.MatchChord(send, "chrome.exe") is not null, "ctrl+enter");
        KeyChord.TryParse("delete", out var del, out _);
        Assert.True(RiskPolicy.MatchChord(del, "explorer.exe") is not null, "delete in explorer");
        Assert.True(RiskPolicy.MatchChord(del, "notepad.exe") is null, "delete in notepad is fine");
        KeyChord.TryParse("alt+s", out var alts, out _);
        Assert.True(RiskPolicy.MatchChord(alts, "OUTLOOK.EXE") is not null, "alt+s in outlook");
    }

    [Test]
    public static void ApprovalStoreKeepsOriginalFileFormatAndBlocks()
    {
        var dir = TempDir.New();
        // A file written by the original app: a plain JSON array of keys.
        File.WriteAllText(Path.Combine(dir, "approvals.json"), "[\"exe:c:\\\\program files\\\\app\\\\app.exe\"]");
        var store = new ApprovalStore(dir);
        Assert.True(store.IsAllowed(@"exe:c:\program files\app\app.exe"), "imported always-allow");
        Assert.True(store.IsBlocked(@"C:\Program Files\KeePass\KeePass.exe"), "default blocked by file name");

        var chrome = ApprovalStore.AppKey(@"C:\Chrome\chrome.exe");
        store.Allow(chrome, always: false);
        Assert.True(store.IsAllowed(chrome), "session allow");
        store.ClearSession();
        Assert.False(store.IsAllowed(chrome), "session cleared");

        store.Allow(chrome, always: true);
        var reloaded = new ApprovalStore(dir);
        Assert.True(reloaded.IsAllowed(chrome), "always persisted");
        var saved = JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(dir, "approvals.json")))!;
        Assert.True(saved.Contains(chrome), "still a string array");

        reloaded.Block(chrome);
        Assert.False(reloaded.IsAllowed(chrome), "blocking removes approval");
        Assert.True(reloaded.IsBlocked(@"C:\Chrome\chrome.exe"), "blocked by path");
        reloaded.Block("Spotify");
        Assert.True(reloaded.IsBlocked(@"D:\Apps\spotify.exe"), "blocked by name");
        reloaded.Unblock("spotify.exe");
        Assert.False(reloaded.IsBlocked(@"D:\Apps\spotify.exe"), "unblocked");
        Assert.Equal("chrome.exe", ApprovalStore.Describe(chrome).Label);
        Assert.Equal("Launch Notepad", ApprovalStore.Describe(@"launch:c:\x\Notepad.lnk").Label);
    }

    [Test]
    public static void StepTitlesNeverIncludeTypedText()
    {
        var args = JsonNode.Parse("{\"text\":\"my secret note\",\"submit\":true}")!.AsObject();
        var title = StepTitles.Describe("windows_type", args);
        Assert.Equal("Type 14 characters", title);
        Assert.Equal("Click element 12", StepTitles.Describe("windows_tap", JsonNode.Parse("{\"element\":12}")!.AsObject()));
        Assert.Equal("Press Ctrl+S", StepTitles.Describe("windows_key", JsonNode.Parse("{\"keys\":\"ctrl+s\"}")!.AsObject()));
        Assert.Equal("Open example.com", StepTitles.Describe("windows_open_url", JsonNode.Parse("{\"url\":\"https://example.com/a\"}")!.AsObject()));
    }

    [Test]
    public static void ToolCatalogKeepsOriginalToolsAndAddsNewOnes()
    {
        var names = ToolCatalog.All.Select(t => t.Name).ToHashSet();
        foreach (var original in new[] { "windows_status", "windows_get_screen", "windows_screenshot", "windows_tap", "windows_long_press",
                     "windows_type", "windows_scroll", "windows_swipe", "windows_press", "windows_open_app", "windows_wait" })
            Assert.True(names.Contains(original), original);
        foreach (var added in new[] { "windows_double_click", "windows_right_click", "windows_hover", "windows_key",
                     "windows_open_url", "windows_list_windows", "windows_focus_window", "windows_read_text" })
            Assert.True(names.Contains(added), added);
        var catalog = JsonNode.Parse(ToolCatalog.McpCatalogJson())!.AsArray();
        Assert.Equal(ToolCatalog.All.Count, catalog.Count);
        Assert.Equal(true, catalog.First(t => t!["name"]!.GetValue<string>() == "windows_status")!["annotations"]!["readOnlyHint"]!.GetValue<bool>());
        var type = catalog.First(t => t!["name"]!.GetValue<string>() == "windows_type")!;
        Assert.Equal("text", type["inputSchema"]!["required"]![0]!.GetValue<string>());
    }
}

public static class SettingsTests
{
    [Test]
    public static void PatchMergesNestedValuesAndIgnoresSecrets()
    {
        var dir = TempDir.New();
        var store = new SettingsStore(dir, new FakeProtector());
        store.Update(s => s.ProviderConfigFor("openai").ApiKeyProtected = store.Protect("sk-test-1234"));
        var patch = JsonNode.Parse("""
            {"providerId":"openai","maxSteps":0,"autoStopMinutes":0,"port":9000,
             "providers":{"openai":{"model":"gpt-6-luna","apiKeyProtected":"evil"},"gemini":{"model":"gemini-3.8-flash"}},
             "speech":{"voice":"nova"},"unknownTopLevel":5,"secretProtected":"x","talkMode":"weird"}
            """)!.AsObject();
        store.ApplyPatch(patch);
        var s = store.Current;
        Assert.Equal("openai", s.ProviderId);
        Assert.Equal(0, s.AutoStopMinutes, "until I stop it");
        Assert.Equal(9000, s.Port);
        Assert.Equal("gpt-6-luna", s.Providers["openai"].Model);
        Assert.Equal("sk-test-1234", store.Reveal(s.Providers["openai"].ApiKeyProtected), "key untouched by patch");
        Assert.Equal("gemini-3.8-flash", s.Providers["gemini"].Model, "new provider entry");
        Assert.Equal("nova", s.Speech.Voice);
        Assert.Equal("gpt-4o-mini-tts", s.Speech.Model, "sibling kept");
        Assert.Equal("", s.SecretProtected);
        Assert.Equal("hold", s.TalkMode, "normalised");
        var reloaded = new SettingsStore(dir, new FakeProtector());
        Assert.Equal(9000, reloaded.Current.Port, "persisted");
        Assert.DoesNotContain("sk-test-1234", File.ReadAllText(reloaded.FilePath), "key encrypted at rest");
    }

    [Test]
    public static void BrokenSettingsFileIsReplacedWithDefaults()
    {
        var dir = TempDir.New();
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{ not json");
        var store = new SettingsStore(dir, new FakeProtector());
        Assert.Equal(20, store.Current.AutoStopMinutes);
        Assert.True(File.Exists(Path.Combine(dir, "settings.json.broken")), "backup kept");
    }
}

public static class VoiceTests
{
    [Test]
    public static void WavRoundTripAndStreamingHeaders()
    {
        var pcm = new byte[3200];
        for (int i = 0; i < pcm.Length; i++) pcm[i] = (byte)(i % 251);
        var wav = Wav.FromPcm16(pcm, 16000);
        Assert.True(Wav.TryRead(wav, out var info, out var data), "read");
        Assert.Equal(16000, info.SampleRate);
        Assert.Equal(1, info.Channels);
        Assert.Equal(3200, data.Count);
        Assert.Equal(0.1, Math.Round(Wav.DurationSeconds(wav), 3));
        // Streaming TTS output often has 0xFFFFFFFF sizes.
        BitConverter.GetBytes(uint.MaxValue).CopyTo(wav, 4);
        BitConverter.GetBytes(uint.MaxValue).CopyTo(wav, 40);
        Assert.True(Wav.TryRead(wav, out _, out var data2), "streaming header");
        Assert.Equal(3200, data2.Count);
        Assert.False(Wav.TryRead(new byte[] { 1, 2, 3 }, out _, out _), "garbage");
    }

    private static short[] Tone(int ms, double amplitude, int rate = 16000)
    {
        var s = new short[rate * ms / 1000];
        for (int i = 0; i < s.Length; i++) s[i] = (short)(Math.Sin(i * 2 * Math.PI * 220 / rate) * amplitude * 32767);
        return s;
    }

    private static short[] Noise(int ms, double amplitude, int rate = 16000)
    {
        var r = new Random(1);
        var s = new short[rate * ms / 1000];
        for (int i = 0; i < s.Length; i++) s[i] = (short)((r.NextDouble() * 2 - 1) * amplitude * 32767);
        return s;
    }

    [Test]
    public static void VadFindsEndOfSpeech()
    {
        var vad = new VoiceActivityDetector(16000, trailingSilenceMs: 600);
        var events = new List<VadEvent>();
        foreach (var chunk in new[] { Noise(400, 0.002), Tone(800, 0.3), Noise(1000, 0.002) })
            for (int i = 0; i < chunk.Length; i += 480)
                if (vad.Process(chunk.AsSpan(i, Math.Min(480, chunk.Length - i))) is var e and not VadEvent.None) events.Add(e);
        Assert.Equal(VadEvent.SpeechStarted, events.FirstOrDefault());
        Assert.Equal(VadEvent.Ended, events.LastOrDefault());
    }

    [Test]
    public static void VadGivesUpWhenNobodySpeaks()
    {
        var vad = new VoiceActivityDetector(16000, noSpeechTimeoutMs: 1000);
        VadEvent last = VadEvent.None;
        var quiet = Noise(1500, 0.002);
        for (int i = 0; i < quiet.Length; i += 320)
            if (vad.Process(quiet.AsSpan(i, 320)) is var e and not VadEvent.None) last = e;
        Assert.Equal(VadEvent.NoSpeech, last);
    }

    [Test]
    public static void VoiceCommandsOnlyMatchShortControlPhrases()
    {
        Assert.Equal(VoiceCommand.Stop, VoiceCommands.Parse("Stop!"));
        Assert.Equal(VoiceCommand.Stop, VoiceCommands.Parse("please stop"));
        Assert.Equal(VoiceCommand.Stop, VoiceCommands.Parse("Stopp"));
        Assert.Equal(VoiceCommand.Pause, VoiceCommands.Parse("hold on"));
        Assert.Equal(VoiceCommand.Resume, VoiceCommands.Parse("Okay, continue."), "okay prefix is 3 words");
        Assert.Equal(VoiceCommand.Resume, VoiceCommands.Parse("mach weiter"));
        Assert.Equal(VoiceCommand.None, VoiceCommands.Parse("stop the music in spotify please"));
        Assert.Equal(VoiceCommand.None, VoiceCommands.Parse("open notepad"));
    }

    [Test]
    public static void SpeechTextIsCleaned()
    {
        var s = Util.TextUtil.ForSpeech("**Done!** I opened [the page](https://example.com/x) and saved `notes.txt`.\n\n# Next");
        Assert.DoesNotContain("*", s);
        Assert.DoesNotContain("https://", s);
        Assert.Contains("the page", s);
    }
}

public static class PrunerTests
{
    private static ChatMessage Shot(string id) => new(ChatRole.User,
        [new ToolResultPart(id, "windows_screenshot", [new TextPart("shot"), new ImagePart("image/jpeg", "AAAA")], false)]);

    private static ChatMessage Call(string id) => new(ChatRole.Assistant,
        [new ToolCallPart(id, "windows_screenshot", new JsonObject())]);

    [Test]
    public static void ImagesArePrunedInBatches()
    {
        var h = new List<ChatMessage> { ChatMessage.UserText("task") };
        for (int i = 0; i < 5; i++) { h.Add(Call("c" + i)); h.Add(Shot("c" + i)); }
        Assert.False(ContextPruner.PruneImages(h, keep: 3, slack: 2), "5 images within keep+slack");
        h.Add(Call("c5")); h.Add(Shot("c5"));
        Assert.True(ContextPruner.PruneImages(h, keep: 3, slack: 2), "6 images triggers");
        Assert.Equal(3, ContextPruner.CountImages(h));
        // The newest images survive.
        Assert.True(h[^1].ToolResults.First().Content.Any(c => c is ImagePart), "latest kept");
        Assert.True(h[2].ToolResults.First().Content.Any(c => c is TextPart t && t.Text == ContextPruner.RemovedImageNote), "oldest replaced");
    }

    [Test]
    public static void CompactionKeepsToolPairsValid()
    {
        var h = new List<ChatMessage> { ChatMessage.UserText("task") };
        for (int i = 0; i < 20; i++) { h.Add(Call("c" + i)); h.Add(Shot("c" + i)); }
        Assert.True(ContextPruner.Compact(h, keepTail: 6), "compacted");
        Assert.Equal(ChatRole.User, h[0].Role);
        Assert.Equal(ChatRole.Assistant, h[1].Role);
        for (int i = 1; i < h.Count; i += 2)
        {
            var call = h[i].ToolCalls.Single();
            Assert.Equal(call.Id, h[i + 1].ToolResults.Single().ToolCallId, "pair " + i);
        }
        Assert.Contains("earlier messages were removed", h[0].Text);
    }
}
