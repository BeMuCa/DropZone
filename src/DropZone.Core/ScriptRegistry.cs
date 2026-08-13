using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace DropZone.Core;

public sealed record ScriptInfo(string Name, string Path, string Extension, bool RemoteEnabled)
{
    public string DisplayName => System.IO.Path.GetFileNameWithoutExtension(Name);

    /// <summary>What to type on the phone to start this. Shown in the UI so nobody has to guess.</summary>
    public string HowToCall => DisplayName;
}

/// <summary>
/// How a script file is launched, per extension. Editable so a machine with `python` but no
/// `py` launcher, or a preference for pwsh over powershell, is a settings change not a rebuild.
/// </summary>
public static class DefaultInterpreters
{
    public static Dictionary<string, string> Create() => new(StringComparer.OrdinalIgnoreCase)
    {
        [".ps1"] = "powershell -NoProfile -ExecutionPolicy Bypass -File",
        [".py"] = "py -3",
        [".bat"] = "cmd /c",
        [".cmd"] = "cmd /c",
        [".js"] = "node",
        [".sh"] = "bash"
    };
}

/// <summary>
/// Scripts live in the Scripts/ folder. Remote invocation is opt-in per script and additionally
/// gated by a master switch — a device on the LAN can only ever start something explicitly ticked.
/// </summary>
public sealed class ScriptRegistry(
    string scriptsFolder,
    string configPath,
    IReadOnlyDictionary<string, string>? interpreters = null)
{
    readonly IReadOnlyDictionary<string, string> _interpreters = interpreters ?? DefaultInterpreters.Create();

    Dictionary<string, bool> _remoteEnabled = Read(configPath);

    public IReadOnlyDictionary<string, string> Interpreters => _interpreters;

    /// <summary>Extensions we know how to launch — driven by the interpreter map, not hardcoded.</summary>
    public IReadOnlyCollection<string> KnownExtensions => _interpreters.Keys.ToList();

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
            .Where(p => _interpreters.ContainsKey(Path.GetExtension(p)))
            .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
            .Select(p => new ScriptInfo(
                Path.GetFileName(p), p, Path.GetExtension(p),
                _remoteEnabled.TryGetValue(Path.GetFileName(p), out var on) && on))
            .ToList();
    }

    /// <summary>
    /// Writes a new script into the folder. The name is treated as untrusted — it decides a path
    /// on disk — so it must be a bare file name with an extension we know how to launch. A new
    /// script is never callable from another device: that stays a decision made in the UI.
    /// </summary>
    public ScriptInfo Create(string name, string content)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A script needs a name.", nameof(name));

        if (name != Path.GetFileName(name) || name.Contains("..") ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException($"\"{name}\" is not a plain file name.", nameof(name));

        var extension = Path.GetExtension(name);
        if (!_interpreters.ContainsKey(extension))
            throw new ArgumentException(
                $"Nothing is configured to run \"{extension}\" files. Known: {string.Join(", ", KnownExtensions)}",
                nameof(name));

        var path = Path.Combine(scriptsFolder, name);
        if (File.Exists(path)) throw new IOException($"{name} already exists.");

        Directory.CreateDirectory(scriptsFolder);
        File.WriteAllText(path, content);

        return new ScriptInfo(name, path, extension, RemoteEnabled: false);
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

    /// <summary>The command line this script would be launched with, for display and for running.</summary>
    public string CommandLineFor(ScriptInfo script, string? arguments = null)
    {
        var prefix = _interpreters.TryGetValue(script.Extension, out var found) ? found : "";
        var tail = string.IsNullOrWhiteSpace(arguments) ? "" : " " + arguments.Trim();
        return $"{prefix} \"{script.Path}\"{tail}".TrimStart();
    }

    public Process? Run(ScriptInfo script, string? arguments = null)
    {
        if (!_interpreters.TryGetValue(script.Extension, out var prefix) || string.IsNullOrWhiteSpace(prefix))
            throw new InvalidOperationException(
                $"No interpreter configured for {script.Extension}. Add one in the Scripts tab.");

        var parts = prefix.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var executable = parts[0];
        var leadingArgs = parts.Length > 1 ? parts[1] + " " : "";
        var tail = string.IsNullOrWhiteSpace(arguments) ? "" : " " + arguments.Trim();

        var info = new ProcessStartInfo(executable, $"{leadingArgs}\"{script.Path}\"{tail}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(script.Path)!
        };

        return Process.Start(info);
    }

    public static void OpenForEditing(ScriptInfo script) =>
        Process.Start(new ProcessStartInfo(script.Path) { UseShellExecute = true });
}
