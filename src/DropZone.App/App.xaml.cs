using System.Windows;
using System.Windows.Controls;
using DropZone.Core;
using H.NotifyIcon;

namespace DropZone.App;

public partial class App : Application
{
    TaskbarIcon? _tray;
    TransferService? _service;
    PopupWindow? _popup;
    IpcServer? _ipc;

    static Mutex? _singleInstance;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // A second copy would fight the first for port 53317, so hand over to the running one.
        _singleInstance = new Mutex(true, @"Local\DropZone.SingleInstance", out var isFirstInstance);
        if (!isFirstInstance)
        {
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "DropZone", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };

        var settings = Settings.Load();
        _service = new TransferService(settings);

        _tray = new TaskbarIcon
        {
            Icon = TrayIconFactory.Create(active: false),
            ToolTipText = "DropZone"
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
            // Never block startup with a modal box — the tray tooltip carries the bad news.
            _startupError = ex.Message;
        }

        // The MCP server drives this instance through here, so an agent works the same DropZone
        // the user sees rather than a second copy fighting it for the port.
        _ipc = new IpcServer(_service);
        _ipc.Start();

        SyncTrayIcon();
    }

    ContextMenu BuildMenu()
    {
        var open = new MenuItem { Header = "Open" };
        open.Click += (_, _) => ShowPopup();

        var toggle = new MenuItem { Header = "Toggle receiving" };
        toggle.Click += async (_, _) =>
        {
            if (_service is null) return;
            await _service.SetReceivingAsync(!_service.IsReceiving);
            SyncTrayIcon();
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
        SyncTrayIcon();
    }

    string? _startupError;

    public string? StartupMessage => _startupError ?? _service?.StartupWarning;

    public void SyncTrayIcon()
    {
        if (_tray is null || _service is null) return;

        _tray.Icon = TrayIconFactory.Create(_service.IsReceiving);
        _tray.ToolTipText = StartupMessage is { } problem
            ? $"DropZone — {problem}"
            : _service.IsReceiving
                ? $"DropZone — receiving as {_service.Settings.Alias}"
                : "DropZone — receiving off";
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        _ipc?.Dispose();
        if (_service is not null) await _service.DisposeAsync();
        _tray?.Dispose();
        base.OnExit(e);
    }
}
