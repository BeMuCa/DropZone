using System.IO;
using System.Text.Json;

namespace Dropzone.App;

public sealed class Settings
{
    /// <summary>Everything the tool produces lives under this one folder.</summary>
    public string RootFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Dropzone");

    public string Alias { get; set; } = Environment.MachineName;

    public bool ReceiveOnStart { get; set; } = true;

    /// <summary>Master switch for running scripts asked for by another device.</summary>
    public bool AllowRemoteScripts { get; set; }

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

    static string ConfigDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dropzone");

    static string ConfigPath => Path.Combine(ConfigDirectory, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(ConfigPath)) ?? new Settings();
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
