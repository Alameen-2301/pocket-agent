namespace PocketAgent.Core.Agent;

/// <summary>
/// Keeps long (unlimited-step) runs within the model's context: old screenshots are replaced by a note,
/// old tool output is shortened, and as a last resort the middle of the conversation is dropped.
/// Pruning happens in batches so the cached prompt prefix stays stable between prunes.
/// </summary>
public static class ContextPruner
{
    public const string RemovedImageNote = "[Earlier screenshot removed to save space]";

    public static int CountImages(IEnumerable<ChatMessage> history)
        => history.Sum(m => m.Parts.Sum(p => p switch
        {
            ImagePart => 1,
            ToolResultPart r => r.Content.Count(c => c is ImagePart),
            _ => 0,
        }));

    /// <summary>When more than keep+slack images exist, drops the oldest until only <paramref name="keep"/> remain.</summary>
    public static bool PruneImages(List<ChatMessage> history, int keep, int slack = 2)
    {
        int total = CountImages(history);
        if (total <= keep + slack) return false;
        int toRemove = total - keep;
        foreach (var message in history)
        {
            for (int i = 0; i < message.Parts.Count && toRemove > 0; i++)
            {
                switch (message.Parts[i])
                {
                    case ImagePart:
                        message.Parts[i] = new TextPart(RemovedImageNote);
                        toRemove--;
                        break;
                    case ToolResultPart r:
                        for (int j = 0; j < r.Content.Count && toRemove > 0; j++)
                        {
                            if (r.Content[j] is ImagePart)
                            {
                                r.Content[j] = new TextPart(RemovedImageNote);
                                toRemove--;
                            }
                        }
                        break;
                }
            }
            if (toRemove == 0) break;
        }
        return true;
    }

    /// <summary>Shortens long tool outputs except for the most recent ones.</summary>
    public static bool TruncateOldToolText(List<ChatMessage> history, int keepRecent = 6, int maxChars = 1500)
    {
        var results = history.SelectMany(m => m.ToolResults).ToList();
        bool changed = false;
        for (int k = 0; k < results.Count - keepRecent; k++)
        {
            var r = results[k];
            for (int i = 0; i < r.Content.Count; i++)
            {
                if (r.Content[i] is TextPart t && t.Text.Length > maxChars)
                {
                    r.Content[i] = new TextPart(t.Text[..maxChars] + "\n…[older output shortened]");
                    changed = true;
                }
            }
        }
        return changed;
    }

    /// <summary>
    /// Emergency compaction after a context-length error: keeps the first request and the last
    /// <paramref name="keepTail"/> messages, starting the tail on an assistant turn so tool calls stay paired.
    /// </summary>
    public static bool Compact(List<ChatMessage> history, int keepTail = 8)
    {
        if (history.Count <= keepTail + 1) return false;
        int start = history.Count - keepTail;
        while (start < history.Count && history[start].Role != ChatRole.Assistant) start++;
        if (start >= history.Count - 1 || start <= 1) return false;
        int removed = start - 1;
        history.RemoveRange(1, removed);
        history[0].Parts.Add(new TextPart($"[Note: {removed} earlier messages were removed to save space. Look at the screen again before acting.]"));
        PruneImages(history, keep: 1, slack: 0);
        TruncateOldToolText(history, keepRecent: 2, maxChars: 800);
        return true;
    }
}
