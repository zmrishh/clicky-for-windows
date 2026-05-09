namespace ClickyWindows.Core;

internal static class StringExtensions
{
    internal static string Truncate(this string value, int maxLength)
    {
        if (value.Length <= maxLength) return value;
        return value[..maxLength] + "…";
    }
}
