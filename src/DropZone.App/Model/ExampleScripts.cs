using System.IO;

namespace DropZone.App.Model;

/// <summary>
/// Seeds the Scripts folder the first time so there is something to run — and so the calling
/// convention is visible rather than described.
/// </summary>
public static class ExampleScripts
{
    public const string TimerFileName = "Timer.ps1";
    public const string CommandsFileName = "Commands.ps1";

    const string TimerScript = """
        # DropZone example script.
        #
        # Run it from the Scripts tab, or from your phone by sending the LocalSend
        # text message:   Timer 5
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

    const string CommandsScript = """
        # DropZone example script: shows every script in this folder.
        #
        # From your phone, send the message  help  instead - DropZone answers that one
        # itself and texts the list back to you. This script shows the same list on the PC.

        $folder = $PSScriptRoot
        $config = Join-Path $folder 'scripts.json'

        $remote = @{}
        if (Test-Path $config) {
            try { $remote = Get-Content $config -Raw | ConvertFrom-Json -AsHashtable } catch { }
        }

        $lines = Get-ChildItem $folder -File |
            Where-Object { $_.Extension -in '.ps1', '.py', '.bat', '.cmd', '.js', '.sh' } |
            ForEach-Object {
                $mark = if ($remote[$_.Name]) { '[remote]' } else { '[local] ' }
                "$mark $($_.BaseName)"
            }

        $text = if ($lines) { $lines -join "`n" } else { 'No scripts found.' }

        Add-Type -AssemblyName System.Windows.Forms
        $icon = New-Object System.Windows.Forms.NotifyIcon
        $icon.Icon = [System.Drawing.SystemIcons]::Information
        $icon.Visible = $true
        $icon.ShowBalloonTip(15000, "DropZone commands", $text, 'Info')
        Start-Sleep -Seconds 15
        $icon.Dispose()
        """;

    public static void SeedIfEmpty(string scriptsFolder)
    {
        try
        {
            Directory.CreateDirectory(scriptsFolder);

            // Only seed into a folder with no scripts yet, so deleting an example keeps it deleted.
            if (Directory.EnumerateFiles(scriptsFolder)
                    .Any(f => !Path.GetFileName(f).Equals("scripts.json", StringComparison.OrdinalIgnoreCase)))
                return;

            File.WriteAllText(Path.Combine(scriptsFolder, TimerFileName), TimerScript);
            File.WriteAllText(Path.Combine(scriptsFolder, CommandsFileName), CommandsScript);
        }
        catch (IOException)
        {
            // Seeding is a convenience; failing to write it must not stop startup.
        }
    }
}
