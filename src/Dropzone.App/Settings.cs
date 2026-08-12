using System.IO;
using System.Text.Json;

namespace Dropzone.App;

public sealed class Settings
{
    public string DownloadFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Dropzone");

    public string PhotoFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "iPhone");

    public string Alias { get; set; } = $"{Environment.MachineName}";

    public bool ReceiveOnStart { get; set; } = true;

    static string DirectoryPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dropzone");

    static string FilePath => Path.Combine(DirectoryPath, "settings.json");

    public static string LedgerPath => Path.Combine(DirectoryPath, "imported.txt");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (Exception)
        {
            // A corrupt settings file should not stop the app starting.
        }

        return new Settings();
    }

    public void Save()
    {
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
