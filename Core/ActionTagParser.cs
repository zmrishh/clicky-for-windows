using System.Text.RegularExpressions;

namespace ClickyWindows.Core;

/// <summary>
/// Parses action tags that Claude appends to its response.
///
/// Supported tags (case-insensitive, appear after spoken text):
///   [CLICK:x,y]            — left-click at screenshot pixel (x, y)
///   [CLICK:x,y:right]      — right-click at (x, y)
///   [TYPE:text to inject]  — type text into focused window
///   [OPEN:app name]        — launch application by name
///
/// Tags are stripped before the spoken text is passed to TTS.
/// At most one action tag per response is executed.
/// </summary>
public sealed class ActionParseResult
{
    public string    SpokenText  { get; init; } = "";
    public ActionTag? Action     { get; init; }
}

public abstract class ActionTag
{
    public sealed class Click : ActionTag
    {
        public int  X          { get; init; }
        public int  Y          { get; init; }
        public bool RightClick { get; init; }
    }

    public sealed class Type : ActionTag
    {
        public string Text { get; init; } = "";
    }

    public sealed class Open : ActionTag
    {
        public string AppName { get; init; } = "";
    }
}

public static class ActionTagParser
{
    // Matches [CLICK:123,456], [CLICK:123,456:right]
    private static readonly Regex ClickRegex = new(
        @"\[CLICK\s*:\s*(\d+)\s*,\s*(\d+)(?:\s*:\s*(right))?\]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Matches [TYPE:some text here]
    private static readonly Regex TypeRegex = new(
        @"\[TYPE\s*:\s*([^\]]+)\]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Matches [OPEN:notepad] or [OPEN:visual studio code]
    private static readonly Regex OpenRegex = new(
        @"\[OPEN\s*:\s*([^\]]+)\]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Parses action tags from Claude's full response text.
    /// Returns the spoken text (tags stripped) and the first action found, if any.
    /// </summary>
    public static ActionParseResult Parse(string fullText)
    {
        if (string.IsNullOrWhiteSpace(fullText))
            return new ActionParseResult { SpokenText = fullText };

        ActionTag? action = null;
        string stripped = fullText;

        // Try CLICK
        var clickMatch = ClickRegex.Match(stripped);
        if (clickMatch.Success)
        {
            action  = new ActionTag.Click
            {
                X          = int.Parse(clickMatch.Groups[1].Value),
                Y          = int.Parse(clickMatch.Groups[2].Value),
                RightClick = clickMatch.Groups[3].Success
            };
            stripped = stripped.Remove(clickMatch.Index, clickMatch.Length).Trim();
        }

        // Try TYPE
        if (action == null)
        {
            var typeMatch = TypeRegex.Match(stripped);
            if (typeMatch.Success)
            {
                action  = new ActionTag.Type { Text = typeMatch.Groups[1].Value.Trim() };
                stripped = stripped.Remove(typeMatch.Index, typeMatch.Length).Trim();
            }
        }

        // Try OPEN
        if (action == null)
        {
            var openMatch = OpenRegex.Match(stripped);
            if (openMatch.Success)
            {
                action  = new ActionTag.Open { AppName = openMatch.Groups[1].Value.Trim() };
                stripped = stripped.Remove(openMatch.Index, openMatch.Length).Trim();
            }
        }

        return new ActionParseResult
        {
            SpokenText = stripped.Trim(),
            Action     = action
        };
    }
}
