using System.Text.RegularExpressions;

namespace ClickyWindows.Core;

/// <summary>
/// Result of parsing a [POINT:x,y:label:screenN] or [POINT:none] tag from Claude's response.
/// Mirrors macOS CompanionManager.PointingParseResult with the exact same regex pattern.
/// </summary>
public sealed class PointingParseResult
{
    /// <summary>
    /// The response text with the [POINT:...] tag stripped — this is what gets spoken via TTS.
    /// </summary>
    public required string SpokenText { get; init; }

    /// <summary>
    /// Screenshot-space pixel coordinate where Claude wants to point, or null for [POINT:none]
    /// or when no tag was found.
    /// </summary>
    public required (double X, double Y)? Coordinate { get; init; }

    /// <summary>Short label for the element, e.g. "save button", or "none", or null.</summary>
    public required string? ElementLabel { get; init; }

    /// <summary>
    /// 1-based screen number from Claude's tag (e.g. :screen2 → 2), or null to default to
    /// whichever screen the cursor is on.
    /// </summary>
    public required int? ScreenNumber { get; init; }

    // Exact same pattern as the Swift implementation.
    private static readonly Regex PointTagRegex = new(
        @"\[POINT:(?:none|(\d+)\s*,\s*(\d+)(?::([^\]:\s][^\]:]*?))?(?::screen(\d+))?)\]\s*$",
        RegexOptions.Compiled | RegexOptions.RightToLeft
    );

    /// <summary>
    /// Parses a [POINT:x,y:label:screenN] or [POINT:none] tag from the end of Claude's
    /// response. Returns the spoken text (tag removed) and the optional coordinate + label +
    /// screen number.
    /// </summary>
    public static PointingParseResult Parse(string responseText)
    {
        var match = PointTagRegex.Match(responseText);

        if (!match.Success)
        {
            return new PointingParseResult
            {
                SpokenText = responseText,
                Coordinate = null,
                ElementLabel = null,
                ScreenNumber = null
            };
        }

        // Remove the tag from spoken text
        var spokenText = responseText[..match.Index].TrimEnd();

        // Check for [POINT:none] — groups 1 and 2 won't have values
        if (!match.Groups[1].Success || !match.Groups[2].Success)
        {
            return new PointingParseResult
            {
                SpokenText = spokenText,
                Coordinate = null,
                ElementLabel = "none",
                ScreenNumber = null
            };
        }

        if (!double.TryParse(match.Groups[1].Value, out var x) ||
            !double.TryParse(match.Groups[2].Value, out var y))
        {
            return new PointingParseResult
            {
                SpokenText = spokenText,
                Coordinate = null,
                ElementLabel = null,
                ScreenNumber = null
            };
        }

        string? elementLabel = match.Groups[3].Success
            ? match.Groups[3].Value.Trim()
            : null;

        int? screenNumber = match.Groups[4].Success && int.TryParse(match.Groups[4].Value, out var sn)
            ? sn
            : null;

        return new PointingParseResult
        {
            SpokenText = spokenText,
            Coordinate = (x, y),
            ElementLabel = elementLabel,
            ScreenNumber = screenNumber
        };
    }
}
