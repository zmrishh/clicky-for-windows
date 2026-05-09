using System.Text.RegularExpressions;

namespace ClickyWindows.Core;

/// <summary>
/// Parses action tags that Claude appends to its response.
///
/// Supported tags (case-insensitive, appear after spoken text, in any order/quantity):
///   [CLICK:x,y]            — left-click at screenshot pixel (x, y)
///   [CLICK:x,y:right]      — right-click at (x, y)
///   [TYPE:text to inject]  — type text into focused window
///   [OPEN:app name]        — launch application by name
///   [WAIT:ms]              — pause for the given milliseconds before the next step
///
/// Multiple tags are returned in document order so callers can execute them as a sequence.
/// All tags are stripped from the spoken text before it is passed to TTS.
/// </summary>
public sealed class ActionParseResult
{
    public string          SpokenText { get; init; } = "";
    public List<ActionTag> Actions    { get; init; } = [];

    /// <summary>True when there is at least one executable action tag.</summary>
    public bool HasActions => Actions.Count > 0;

    /// <summary>Backwards-compat helper: first action or null.</summary>
    public ActionTag? Action => Actions.Count > 0 ? Actions[0] : null;
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

    /// <summary>Pause for <see cref="Milliseconds"/> before the next step.</summary>
    public sealed class Wait : ActionTag
    {
        public int Milliseconds { get; init; }
    }
}

public static class ActionTagParser
{
    // Each pattern captures its tag content so we can reconstruct position in the string.
    private static readonly Regex AnyTagRegex = new(
        @"\[(?:" +
            @"(CLICK)\s*:\s*(\d+)\s*,\s*(\d+)(?:\s*:\s*(right))?" +     // group 1-4
            @"|" +
            @"(TYPE)\s*:\s*([^\]]+)" +                                    // group 5-6
            @"|" +
            @"(OPEN)\s*:\s*([^\]]+)" +                                    // group 7-8
            @"|" +
            @"(WAIT)\s*:\s*(\d+)" +                                       // group 9-10
        @")\]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Parses all action tags from Claude's response text.
    /// Returns the spoken text (tags stripped) and all actions in document order.
    /// </summary>
    public static ActionParseResult Parse(string fullText)
    {
        if (string.IsNullOrWhiteSpace(fullText))
            return new ActionParseResult { SpokenText = fullText };

        var actions = new List<ActionTag>();
        var stripped = AnyTagRegex.Replace(fullText, match =>
        {
            if (match.Groups[1].Success) // CLICK
            {
                actions.Add(new ActionTag.Click
                {
                    X          = int.Parse(match.Groups[2].Value),
                    Y          = int.Parse(match.Groups[3].Value),
                    RightClick = match.Groups[4].Success
                });
            }
            else if (match.Groups[5].Success) // TYPE
            {
                actions.Add(new ActionTag.Type { Text = match.Groups[6].Value.Trim() });
            }
            else if (match.Groups[7].Success) // OPEN
            {
                actions.Add(new ActionTag.Open { AppName = match.Groups[8].Value.Trim() });
            }
            else if (match.Groups[9].Success) // WAIT
            {
                if (int.TryParse(match.Groups[10].Value, out int ms))
                    actions.Add(new ActionTag.Wait { Milliseconds = Math.Clamp(ms, 0, 30_000) });
            }
            return ""; // remove the tag from the text
        });

        return new ActionParseResult
        {
            SpokenText = stripped.Trim(),
            Actions    = actions
        };
    }
}
