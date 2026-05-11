using System.Text.RegularExpressions;

namespace ClickyWindows.Core;

/// <summary>
/// Parses action tags that Claude appends to its response.
///
/// Supported tags (case-insensitive, appear after spoken text, in any order/quantity):
///   [CLICK:x,y]              — left-click at screenshot pixel (x, y)
///   [CLICK:x,y:right]        — right-click at (x, y)
///   [DBLCLICK:x,y]           — double-click at (x, y)
///   [HOVER:x,y]              — move cursor without clicking (reveals hidden controls)
///   [SCROLL:x,y:down:3]      — scroll at (x, y); direction = up|down|left|right; amount = click-units
///   [DRAG:x1,y1:x2,y2]       — click-and-drag from (x1,y1) to (x2,y2)
///   [TYPE:text to inject]    — type text into focused window
///   [OPEN:app name]          — launch application by name
///   [NAVIGATE:https://url]   — navigate current browser tab to a URL
///   [KEYPRESS:combo]         — press a key combo (e.g. Win+Down, Alt+F4)
///   [SYSINFO:query]          — query OS state: battery, volume, time, date, wifi
///   [REMEMBER:fact]          — persist a user fact to long-term memory
///   [FORGET:keyword]         — remove facts containing keyword from memory
///   [WAIT:ms]                — pause for the given milliseconds before the next step
///   [DONE]                   — task is complete, stop the agent loop
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

    /// <summary>Move cursor without clicking — reveals hover-only UI controls.</summary>
    public sealed class Hover : ActionTag
    {
        public int X { get; init; }
        public int Y { get; init; }
    }

    /// <summary>Scroll the mouse wheel at a given screen position.</summary>
    public sealed class Scroll : ActionTag
    {
        public int    X         { get; init; }
        public int    Y         { get; init; }
        /// <summary>up | down | left | right</summary>
        public string Direction { get; init; } = "down";
        /// <summary>Number of wheel-click units (1 unit = 120 WHEEL_DELTA).</summary>
        public int    Amount    { get; init; } = 3;
    }

    /// <summary>Click-and-drag from (X1,Y1) to (X2,Y2).</summary>
    public sealed class Drag : ActionTag
    {
        public int X1 { get; init; }
        public int Y1 { get; init; }
        public int X2 { get; init; }
        public int Y2 { get; init; }
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

    /// <summary>Navigate the active browser tab to a URL (Ctrl+L → type → Enter).</summary>
    public sealed class Navigate : ActionTag
    {
        public string Url { get; init; } = "";
    }

    /// <summary>Query OS state (battery, volume, time, date, wifi) and speak the result.</summary>
    public sealed class SysInfo : ActionTag
    {
        public string Query { get; init; } = "";
    }

    /// <summary>Persist a user fact to long-term memory (AppSettings.UserFacts).</summary>
    public sealed class Remember : ActionTag
    {
        public string Fact { get; init; } = "";
    }

    /// <summary>Remove from memory any facts that contain the given keyword.</summary>
    public sealed class Forget : ActionTag
    {
        public string Keyword { get; init; } = "";
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
            @"(CLICK)\s*:\s*(\d+)\s*,\s*(\d+)(?:\s*:\s*(right))?" +             // 1-4:  CLICK
            @"|" +
            @"(DBLCLICK)\s*:\s*(\d+)\s*,\s*(\d+)" +                             // 5-7:  DBLCLICK
            @"|" +
            @"(HOVER)\s*:\s*(\d+)\s*,\s*(\d+)" +                                // 8-10: HOVER
            @"|" +
            @"(SCROLL)\s*:\s*(\d+)\s*,\s*(\d+)\s*:\s*(\w+)(?:\s*:\s*(\d+))?" + // 11-15: SCROLL (amount optional)
            @"|" +
            @"(DRAG)\s*:\s*(\d+)\s*,\s*(\d+)\s*:\s*(\d+)\s*,\s*(\d+)" +        // 16-20: DRAG
            @"|" +
            @"(TYPE)\s*:\s*([^\]]+)" +                                            // 21-22: TYPE
            @"|" +
            @"(OPEN)\s*:\s*([^\]]+)" +                                            // 23-24: OPEN
            @"|" +
            @"(KEYPRESS)\s*:\s*([^\]]+)" +                                        // 25-26: KEYPRESS
            @"|" +
            @"(WAIT)\s*:\s*(\d+)" +                                               // 27-28: WAIT
            @"|" +
            @"(NAVIGATE)\s*:\s*([^\]]+)" +                                        // 29-30: NAVIGATE
            @"|" +
            @"(SYSINFO)\s*:\s*([^\]]+)" +                                         // 31-32: SYSINFO
            @"|" +
            @"(REMEMBER)\s*:\s*([^\]]+)" +                                        // 33-34: REMEMBER
            @"|" +
            @"(FORGET)\s*:\s*([^\]]+)" +                                          // 35-36: FORGET
            @"|" +
            @"(DONE)" +                                                            // 37:   DONE
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
            else if (match.Groups[8].Success) // HOVER
            {
                actions.Add(new ActionTag.Hover
                {
                    X = int.Parse(match.Groups[9].Value),
                    Y = int.Parse(match.Groups[10].Value)
                });
            }
            else if (match.Groups[11].Success) // SCROLL
            {
                int amount = match.Groups[15].Success && int.TryParse(match.Groups[15].Value, out int a) ? a : 3;
                actions.Add(new ActionTag.Scroll
                {
                    X         = int.Parse(match.Groups[12].Value),
                    Y         = int.Parse(match.Groups[13].Value),
                    Direction = match.Groups[14].Value.Trim().ToLowerInvariant(),
                    Amount    = Math.Clamp(amount, 1, 20)
                });
            }
            else if (match.Groups[16].Success) // DRAG
            {
                actions.Add(new ActionTag.Drag
                {
                    X1 = int.Parse(match.Groups[17].Value),
                    Y1 = int.Parse(match.Groups[18].Value),
                    X2 = int.Parse(match.Groups[19].Value),
                    Y2 = int.Parse(match.Groups[20].Value)
                });
            }
            else if (match.Groups[21].Success) // TYPE
            {
                actions.Add(new ActionTag.Type { Text = match.Groups[22].Value.Trim() });
            }
            else if (match.Groups[23].Success) // OPEN
            {
                actions.Add(new ActionTag.Open { AppName = match.Groups[24].Value.Trim() });
            }
            else if (match.Groups[25].Success) // KEYPRESS
            {
                actions.Add(new ActionTag.KeyPress { Combo = match.Groups[26].Value.Trim() });
            }
            else if (match.Groups[27].Success) // WAIT
            {
                if (int.TryParse(match.Groups[28].Value, out int ms))
                    actions.Add(new ActionTag.Wait { Milliseconds = Math.Clamp(ms, 0, 30_000) });
            }
            else if (match.Groups[29].Success) // NAVIGATE
            {
                actions.Add(new ActionTag.Navigate { Url = match.Groups[30].Value.Trim() });
            }
            else if (match.Groups[31].Success) // SYSINFO
            {
                actions.Add(new ActionTag.SysInfo { Query = match.Groups[32].Value.Trim().ToLowerInvariant() });
            }
            else if (match.Groups[33].Success) // REMEMBER
            {
                actions.Add(new ActionTag.Remember { Fact = match.Groups[34].Value.Trim() });
            }
            else if (match.Groups[35].Success) // FORGET
            {
                actions.Add(new ActionTag.Forget { Keyword = match.Groups[36].Value.Trim() });
            }
            else if (match.Groups[37].Success) // DONE
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
