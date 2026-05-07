namespace ClickyWindows.Core;

internal static class AppDebugLog
{
    public static void Write(string line)
    {
        try
        {
            IoDirectory.CreateDirectory(AppConstants.SettingsDirectory);
            var path = IoPath.Combine(AppConstants.SettingsDirectory, "clicky-debug.log");
            IoFile.AppendAllText(path, $"{DateTime.UtcNow:O} {line}{Environment.NewLine}");
        }
        catch
        {
            /* best-effort */
        }
    }
}
