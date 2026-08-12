using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Dropzone.App.Model;

public sealed record ScriptInfo(string Name, string Path, string Extension, bool RemoteEnabled)
{
    public string DisplayName => System.IO.Path.GetFileNameWithoutExtension(Name);
}

/// <summary>
/// Scripts live in the Scripts/ folder. Remote invocation is opt-in per script and additionally
/// gated by a master switch — a device on the LAN can only ever start something explicitly ticked.
/// </summary>
public sealed class ScriptRegistry(string scriptsFolder, string configPath)
{
    static readonly string[] Extensions = [".ps1", ".bat", ".cmd"];

    Dictionary<string, bool> _remoteEnabled = Read(configPath);

    static Dictionary<string, bool> Read(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(path))
                       ?? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // Fall through to defaults — everything off.
        }

        return new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<ScriptInfo> All()
    {
        if (!Directory.Exists(scriptsFolder)) return [];

        return Directory.EnumerateFiles(scriptsFolder)
            .Where(p => Extensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
            .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
            .Select(p => new ScriptInfo(
                Path.GetFileName(p), p, Path.GetExtension(p),
                _remoteEnabled.TryGetValue(Path.GetFileName(p), out var on) && on))
            .ToList();
    }

    public void SetRemoteEnabled(string scriptFileName, bool enabled)
    {
        _remoteEnabled[scriptFileName] = enabled;
        Save();
    }

    void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            File.WriteAllText(configPath,
                JsonSerializer.Serialize(_remoteEnabled, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException)
        {
            // Non-fatal: the toggle simply will not persist.
        }
    }

    public void Reload() => _remoteEnabled = Read(configPath);

    /// <summary>Finds a script that a remote device is allowed to start. Returns null when not permitted.</summary>
    public ScriptInfo? FindRemotelyInvocable(string name) =>
        All().FirstOrDefault(s =>
            s.RemoteEnabled &&
            (s.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase) ||
             s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));

    public static Process? Run(ScriptInfo script, string? arguments = null)
    {
        var info = script.Extension.ToLowerInvariant() switch
        {
            ".ps1" => new ProcessStartInfo("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -File \"{script.Path}\" {arguments}".TrimEnd()),
            _ => new ProcessStartInfo("cmd.exe", $"/c \"{script.Path}\" {arguments}".TrimEnd())
        };

        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.WorkingDirectory = Path.GetDirectoryName(script.Path)!;

        return Process.Start(info);
    }

    public static void OpenForEditing(ScriptInfo script) =>
        Process.Start(new ProcessStartInfo(script.Path) { UseShellExecute = true });
}
