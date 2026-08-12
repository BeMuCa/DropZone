using System.Management;

namespace Dropzone.Mtp;

/// <summary>
/// WPD only enumerates a phone that is unlocked and trusted, so "no device" from MediaDevices
/// is ambiguous: absent, or locked? Windows still lists a locked phone as a PnP device, and that
/// is what tells the two apart — the difference between "plug your phone in" and "unlock it".
/// </summary>
public static class PnpPhoneDetector
{
    public static bool IsPhysicallyAttached()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name FROM Win32_PnPEntity WHERE PNPClass = 'WPD'");

            foreach (var device in searcher.Get())
            {
                var name = device["Name"]?.ToString() ?? "";
                if (name.Contains("iPhone", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("iPad", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch (ManagementException)
        {
            // WMI unavailable — fall back to "not attached" rather than crashing the UI.
        }

        return false;
    }
}
