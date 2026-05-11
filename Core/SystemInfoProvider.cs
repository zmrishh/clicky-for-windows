using System.Net.NetworkInformation;
using NAudio.CoreAudioApi;

namespace ClickyWindows.Core;

/// <summary>
/// Queries OS-level system state and returns a short human-readable string
/// suitable for text-to-speech. All methods are synchronous and fast (&lt;5 ms).
/// </summary>
public static class SystemInfoProvider
{
    /// <summary>
    /// Resolves a query keyword to a spoken result string.
    /// Returns an empty string if the query is unrecognised — Claude handles the gap gracefully.
    /// </summary>
    public static string Query(string key) => key.Trim().ToLowerInvariant() switch
    {
        "battery"                         => GetBattery(),
        "volume"                          => GetVolume(),
        "time"    or "clock"              => GetTime(),
        "date"    or "today"              => GetDate(),
        "wifi"    or "network" or "internet" => GetWifi(),
        _                                 => ""
    };

    // ── Individual queries ────────────────────────────────────────────────────

    private static string GetBattery()
    {
        try
        {
            var status = System.Windows.Forms.SystemInformation.PowerStatus;
            float pct  = status.BatteryLifePercent;

            if (pct < 0 || pct > 1)
                return "battery status unavailable";

            int percent = (int)(pct * 100);
            string chargeState = status.PowerLineStatus switch
            {
                System.Windows.Forms.PowerLineStatus.Online => "charging",
                _                                           => "on battery"
            };
            return $"battery is at {percent} percent, {chargeState}";
        }
        catch (Exception ex)
        {
            AppDebugLog.Write($"SystemInfoProvider.GetBattery failed: {ex.Message}");
            return "battery status unavailable";
        }
    }

    private static string GetVolume()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device     = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            int vol = (int)(device.AudioEndpointVolume.MasterVolumeLevelScalar * 100);
            bool muted = device.AudioEndpointVolume.Mute;
            return muted ? "volume is muted" : $"volume is at {vol} percent";
        }
        catch (Exception ex)
        {
            AppDebugLog.Write($"SystemInfoProvider.GetVolume failed: {ex.Message}");
            return "volume level unavailable";
        }
    }

    private static string GetTime()
        => DateTime.Now.ToString("h:mm tt").ToLowerInvariant(); // "4:23 pm"

    private static string GetDate()
        => DateTime.Now.ToString("dddd, MMMM d"); // "Monday, May 11"

    private static string GetWifi()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback
                                            or NetworkInterfaceType.Tunnel) continue;

                // Prefer wireless, but accept wired if nothing else
                bool isWireless = ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
                string type     = isWireless ? "wifi" : "ethernet";
                return $"connected via {type}, network name: {ni.Name}";
            }
            return "not connected to a network";
        }
        catch (Exception ex)
        {
            AppDebugLog.Write($"SystemInfoProvider.GetWifi failed: {ex.Message}");
            return "network status unavailable";
        }
    }
}
