namespace ClickyWindows.Core;

/// <summary>
/// Thin helper that manages the user's long-term memory stored in
/// <see cref="AppSettings.UserFacts"/>. All mutations call <see cref="AppSettings.Save"/>
/// immediately so facts survive app restarts.
/// </summary>
public sealed class UserMemory
{
    private readonly AppSettings _settings;

    public UserMemory(AppSettings settings) => _settings = settings;

    /// <summary>All currently stored facts (read-only view).</summary>
    public IReadOnlyList<string> Facts => _settings.UserFacts;

    /// <summary>
    /// Adds a fact. If the list is at capacity, the oldest entry is evicted first.
    /// Duplicate facts (case-insensitive) are silently ignored.
    /// </summary>
    public void Add(string fact)
    {
        if (string.IsNullOrWhiteSpace(fact)) return;
        fact = fact.Trim();

        // Dedup — don't store the same fact twice
        if (_settings.UserFacts.Any(f => string.Equals(f, fact, StringComparison.OrdinalIgnoreCase)))
            return;

        if (_settings.UserFacts.Count >= AppConstants.MemoryMaxFacts)
            _settings.UserFacts.RemoveAt(0); // evict oldest

        _settings.UserFacts.Add(fact);
        _settings.Save();
        AppDebugLog.Write($"UserMemory: added fact \"{fact}\" (total={_settings.UserFacts.Count})");
    }

    /// <summary>
    /// Removes every fact whose text contains <paramref name="keyword"/> (case-insensitive).
    /// Returns the number of facts removed.
    /// </summary>
    public int RemoveByKeyword(string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return 0;
        int before = _settings.UserFacts.Count;
        _settings.UserFacts.RemoveAll(
            f => f.Contains(keyword.Trim(), StringComparison.OrdinalIgnoreCase));
        int removed = before - _settings.UserFacts.Count;
        if (removed > 0) _settings.Save();
        AppDebugLog.Write($"UserMemory: removed {removed} fact(s) matching \"{keyword}\"");
        return removed;
    }

    /// <summary>Removes all stored facts.</summary>
    public void Clear()
    {
        _settings.UserFacts.Clear();
        _settings.Save();
        AppDebugLog.Write("UserMemory: cleared all facts");
    }

    /// <summary>
    /// Formats facts as a compact system-prompt block.
    /// Returns an empty string when there are no facts so callers can skip injection cleanly.
    /// </summary>
    public string ToPromptBlock()
    {
        if (_settings.UserFacts.Count == 0) return "";
        var lines = string.Join("\n", _settings.UserFacts.Select(f => $"- {f}"));
        return $"""

        things you know about the user:
        {lines}
        """;
    }
}
