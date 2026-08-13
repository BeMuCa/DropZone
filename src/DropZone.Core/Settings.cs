using System.IO;
using System.Text.Json;

namespace DropZone.Core;

public sealed class Settings
{
    /// <summary>Everything the tool produces lives under this one folder.</summary>
    public string RootFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "DropZone");

    public string Alias { get; set; } = Environment.MachineName;

    public bool ReceiveOnStart { get; set; } = true;

    /// <summary>Master switch for running scripts asked for by another device.</summary>
    public bool AllowRemoteScripts { get; set; }

    /// <summary>
    /// Extension to command-line prefix. DropZone appends the quoted script path and any
    /// arguments, so ".py": "py -3" runs  py -3 "C:\...\Timer.py" 5.
    /// </summary>
    public Dictionary<string, string> Interpreters { get; set; } = DefaultInterpreters.Create();

    public string ReceivedFolder => Path.Combine(RootFolder, "Received");
    public string SentFolder => Path.Combine(RootFolder, "Sent");
    public string PhotoFolder => Path.Combine(RootFolder, "iPhone");
    public string ScriptsFolder => Path.Combine(RootFolder, "Scripts");

    public string HistoryPath => Path.Combine(RootFolder, "history.json");
    public string ScriptConfigPath => Path.Combine(ScriptsFolder, "scripts.json");
    public string LedgerPath => Path.Combine(RootFolder, "imported.txt");

    public void EnsureFolders()
    {
        foreach (var folder in new[] { RootFolder, ReceivedFolder, SentFolder, PhotoFolder, ScriptsFolder })
            Directory.CreateDirectory(folder);
    }

    /// <summary>
    /// Where settings.json lives. Tests must point this at a temp folder: constructing a service
    /// writes the settings back, so a test run with throwaway settings would otherwise overwrite
    /// the real ones and move the whole installation to a temp directory.
    /// </summary>
    public static string? ConfigDirectoryOverride { get; set; }

    static string ConfigDirectory =>
        ConfigDirectoryOverride ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DropZone");

    static string ConfigPath => Path.Combine(ConfigDirectory, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(ConfigPath)) ?? new Settings();

                // Settings written before a new extension was supported would otherwise never
                // learn about it, so fill gaps rather than replacing what the user edited.
                foreach (var (extension, command) in DefaultInterpreters.Create())
                    loaded.Interpreters.TryAdd(extension, command);

                return loaded;
            }
        }
        catch (Exception)
        {
            // A corrupt settings file should not stop the app starting.
        }

        return new Settings();
    }

    public void Save()
    {
        Directory.CreateDirectory(ConfigDirectory);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
