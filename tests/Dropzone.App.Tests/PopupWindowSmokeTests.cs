using System.IO;

namespace Dropzone.App.Tests;

/// <summary>
/// Constructs the real window on an STA thread. XAML is parsed at construction time, so this
/// catches malformed markup, missing resource keys and mistyped event handler names — none of
/// which the compiler reports.
/// </summary>
public class PopupWindowSmokeTests
{
    static T OnStaThread<T>(Func<T> action)
    {
        T result = default!;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try { result = action(); }
            catch (Exception ex) { failure = ex; }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(60));

        if (failure is not null) throw new Exception($"STA thread threw: {failure}", failure);
        return result;
    }

    static Settings TempSettings(string root) => new()
    {
        RootFolder = root,
        Alias = "smoke-test",
        ReceiveOnStart = false
    };

    [Fact]
    public void Window_constructs_without_xaml_errors()
    {
        var root = Directory.CreateTempSubdirectory("dropzone-smoke-").FullName;

        try
        {
            var title = OnStaThread(() =>
            {
                var service = new TransferService(TempSettings(root));
                var window = new PopupWindow(service);
                var text = window.Title;
                window.Close();
                return text;
            });

            Assert.Equal("Dropzone", title);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Service_creates_the_folder_layout()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dropzone-layout-{Guid.NewGuid():N}");

        try
        {
            var settings = TempSettings(root);
            _ = OnStaThread(() => new TransferService(settings));

            Assert.True(Directory.Exists(settings.ReceivedFolder));
            Assert.True(Directory.Exists(settings.SentFolder));
            Assert.True(Directory.Exists(settings.PhotoFolder));
            Assert.True(Directory.Exists(settings.ScriptsFolder));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}
