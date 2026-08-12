using System.Windows;
using System.Windows.Controls;
using H.NotifyIcon;

namespace Dropzone.App;

public partial class App : Application
{
    TaskbarIcon? _tray;
    TransferService? _service;
    PopupWindow? _popup;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "Dropzone", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };

        var settings = Settings.Load();
        _service = new TransferService(settings);

        _tray = new TaskbarIcon
        {
            Icon = TrayIconFactory.Create(active: false),
            ToolTipText = "Dropzone"
        };
        _tray.TrayLeftMouseUp += (_, _) => TogglePopup();
        _tray.ContextMenu = BuildMenu();
        _tray.ForceCreate();

        try
        {
            await _service.StartAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not start receiving: {ex.Message}", "Dropzone");
        }

        UpdateTrayIcon();
    }

    ContextMenu BuildMenu()
    {
        var open = new MenuItem { Header = "Open" };
        open.Click += (_, _) => ShowPopup();

        var toggle = new MenuItem { Header = "Receiving" };
        toggle.Click += async (_, _) =>
        {
            if (_service is null) return;
            await _service.SetReceivingAsync(!_service.IsReceiving);
            UpdateTrayIcon();
        };

        var quit = new MenuItem { Header = "Quit" };
        quit.Click += (_, _) => Shutdown();

        var menu = new ContextMenu();
        menu.Items.Add(open);
        menu.Items.Add(toggle);
        menu.Items.Add(new Separator());
        menu.Items.Add(quit);
        return menu;
    }

    void TogglePopup()
    {
        if (_popup is { IsVisible: true })
            _popup.Hide();
        else
            ShowPopup();
    }

    void ShowPopup()
    {
        if (_service is null) return;

        _popup ??= new PopupWindow(_service);
        _popup.ShowInCorner();
        UpdateTrayIcon();
    }

    void UpdateTrayIcon()
    {
        if (_tray is null || _service is null) return;

        _tray.Icon = TrayIconFactory.Create(_service.IsReceiving);
        _tray.ToolTipText = _service.IsReceiving
            ? $"Dropzone — receiving as {_service.Settings.Alias}"
            : "Dropzone — receiving off";
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_service is not null) await _service.DisposeAsync();
        _tray?.Dispose();
        base.OnExit(e);
    }
}
