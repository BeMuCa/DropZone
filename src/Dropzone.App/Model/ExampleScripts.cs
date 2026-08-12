using System.IO;

namespace Dropzone.App.Model;

/// <summary>
/// Seeds the Scripts folder the first time so there is something to run — and so the parameter
/// convention is visible rather than described.
/// </summary>
public static class ExampleScripts
{
    public const string TimerFileName = "Timer.ps1";

    const string TimerScript = """
        # Dropzone example script.
        #
        # Run it from the Scripts tab, or from your phone by sending the LocalSend
        # text message:   run Timer 5
        #
        # The parameter is the number of minutes. Defaults to 5.

        param([int]$Minutes = 5)

        if ($Minutes -lt 1) { $Minutes = 1 }

        Start-Sleep -Seconds ($Minutes * 60)

        Add-Type -AssemblyName System.Windows.Forms
        $icon = New-Object System.Windows.Forms.NotifyIcon
        $icon.Icon = [System.Drawing.SystemIcons]::Information
        $icon.Visible = $true
        $icon.ShowBalloonTip(10000, "Timer finished", "$Minutes minute(s) are up.", 'Info')
        Start-Sleep -Seconds 10
        $icon.Dispose()
        """;

    public static void SeedIfEmpty(string scriptsFolder)
    {
        try
        {
            Directory.CreateDirectory(scriptsFolder);

            var timer = Path.Combine(scriptsFolder, TimerFileName);
            if (!File.Exists(timer) && Directory.GetFiles(scriptsFolder, "*.ps1").Length == 0)
                File.WriteAllText(timer, TimerScript);
        }
        catch (IOException)
        {
            // Seeding is a convenience; failing to write it must not stop startup.
        }
    }
}
