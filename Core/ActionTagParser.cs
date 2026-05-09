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
///   [DONE]                 — task is complete, stop the agent loop
///
/// Multiple tags are returned in document order so callers can execute them as a sequence.
/// All tags are stripped from the spoken text before it is passed to TTS.
/// </summary>
public sealed class ActionParseResult
{
    public string          SpokenText { get; init; } = "";
    public List<ActionTag> Actions    { get; init; } = [];

    /// <summary>True when Claude signalled the task is fully complete.</summary>
    public bool IsDone { get; init; }

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

    public sealed class DoubleClick : ActionTag
    {
        public int X { get; init; }
        public int Y { get; init; }
    }

    public sealed class Type : ActionTag
    {
        public string Text { get; init; } = "";
    }

    public sealed class Open : ActionTag
    {
        public string AppName { get; init; } = "";
    }

    /// <summary>Sends a keyboard shortcut, e.g. "Win+Down", "Alt+F4", "Ctrl+W".</summary>
    public sealed class KeyPress : ActionTag
    {
        public string Combo { get; init; } = "";
    }

    /// <summary>Pause for <see cref="Milliseconds"/> before the next step.</summary>
    public sealed class Wait : ActionTag
    {
        public int Milliseconds { get; init; }
    }
}

public static class ActionTagParser
{
    private static readonly Regex AnyTagRegex = new(
        @"\[(?:" +
            @"(CLICK)\s*:\s*(\d+)\s*,\s*(\d+)(?:\s*:\s*(right))?" +    // 1-4: CLICK
            @"|" +
            @"(DBLCLICK)\s*:\s*(\d+)\s*,\s*(\d+)" +                    // 5-7: DBLCLICK
            @"|" +
            @"(TYPE)\s*:\s*([^\]]+)" +                                   // 8-9: TYPE
            @"|" +
            @"(OPEN)\s*:\s*([^\]]+)" +                                   // 10-11: OPEN
            @"|" +
            @"(KEYPRESS)\s*:\s*([^\]]+)" +                              // 12-13: KEYPRESS
            @"|" +
            @"(WAIT)\s*:\s*(\d+)" +                                      // 14-15: WAIT
            @"|" +
            @"(DONE)" +                                                   // 16: DONE
        @")\]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static ActionParseResult Parse(string fullText)
    {
        if (string.IsNullOrWhiteSpace(fullText))
            return new ActionParseResult { SpokenText = fullText };

        var actions = new List<ActionTag>();
        bool isDone = false;

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
            else if (match.Groups[5].Success) // DBLCLICK
            {
                actions.Add(new ActionTag.DoubleClick
                {
                    X = int.Parse(match.Groups[6].Value),
                    Y = int.Parse(match.Groups[7].Value)
                });
            }
            else if (match.Groups[8].Success) // TYPE
            {
                actions.Add(new ActionTag.Type { Text = match.Groups[9].Value.Trim() });
            }
            else if (match.Groups[10].Success) // OPEN
            {
                actions.Add(new ActionTag.Open { AppName = match.Groups[11].Value.Trim() });
            }
            else if (match.Groups[12].Success) // KEYPRESS
            {
                actions.Add(new ActionTag.KeyPress { Combo = match.Groups[13].Value.Trim() });
            }
            else if (match.Groups[14].Success) // WAIT
            {
                if (int.TryParse(match.Groups[15].Value, out int ms))
                    actions.Add(new ActionTag.Wait { Milliseconds = Math.Clamp(ms, 0, 30_000) });
            }
            else if (match.Groups[16].Success) // DONE
            {
                isDone = true;
            }
            return "";
        });

        return new ActionParseResult
        {
            SpokenText = stripped.Trim(),
            Actions    = actions,
            IsDone     = isDone
        };
    }
}
