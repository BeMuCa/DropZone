using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Dropzone.LocalSend;
using Dropzone.Mtp;

namespace Dropzone.App;

public partial class PopupWindow : Window
{
    readonly TransferService _service;
    readonly ObservableCollection<PeerRow> _peers = [];
    CancellationTokenSource? _importCts;

    public sealed record PeerRow(string Alias, string Address);

    public PopupWindow(TransferService service)
    {
        InitializeComponent();
        _service = service;

        PeerList.ItemsSource = _peers;
        AliasText.Text = _service.Settings.Alias;

        _service.PeersChanged += OnPeersChanged;
        _service.FileReceived += OnFileReceived;

        Deactivated += (_, _) => Hide();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Hide(); };

        Drop += OnDrop;
        DragEnter += (_, e) => SetDropHighlight(e, true);
        DragOver += (_, e) => SetDropHighlight(e, true);
        DragLeave += (_, _) => SetDropHighlight(null, false);

        RefreshUi();
    }

    public void ShowInCorner()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 12;
        Top = area.Bottom - Height - 12;

        RefreshUi();
        RefreshPhoneStatusAsync();
        Show();
        Activate();
        _ = _service.ScanAsync();
    }

    void RefreshUi()
    {
        var on = _service.IsReceiving;

        ToggleButton.Content = on ? "Turn off" : "Turn on";
        ReceivingHint.Text = on
            ? $"Visible as \"{_service.Settings.Alias}\" · saving to {Shorten(_service.Settings.DownloadFolder)}"
            : "Others cannot send to this PC right now";

        StatusDot.Fill = new SolidColorBrush(on
            ? Color.FromRgb(0x58, 0xA6, 0xFF)
            : Color.FromRgb(0x6E, 0x72, 0x77));

        NoPeersText.Visibility = _peers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    static string Shorten(string path) =>
        path.Length <= 34 ? path : "…" + path[^33..];

    void OnPeersChanged()
    {
        Dispatcher.Invoke(() =>
        {
            _peers.Clear();
            foreach (var p in _service.Peers)
                _peers.Add(new PeerRow(p.Alias, p.Address.ToString()));
            RefreshUi();
        });
    }

    void OnFileReceived(ReceivedFile file)
    {
        Dispatcher.Invoke(() =>
        {
            SendStatus.Text = $"Received {file.File.FileName}";
        });
    }

    async void RefreshPhoneStatusAsync()
    {
        PhoneStatusText.Text = "Checking…";
        ImportButton.IsEnabled = false;

        var status = await Task.Run(_service.PhoneStatus);

        PhoneStatusText.Text = status.Connected && status.Unlocked
            ? $"{status.Describe()} · saving to {Shorten(_service.Settings.PhotoFolder)}"
            : status.Describe();

        ImportButton.IsEnabled = status is { Connected: true, Unlocked: true };
    }

    void Toggle_Click(object sender, RoutedEventArgs e)
    {
        _ = ToggleAsync();
    }

    async Task ToggleAsync()
    {
        ToggleButton.IsEnabled = false;
        try
        {
            await _service.SetReceivingAsync(!_service.IsReceiving);
        }
        catch (Exception ex)
        {
            SendStatus.Text = ex.Message;
        }
        finally
        {
            ToggleButton.IsEnabled = true;
            RefreshUi();
        }
    }

    void Refresh_Click(object sender, RoutedEventArgs e)
    {
        SendStatus.Text = "Scanning…";
        _ = _service.ScanAsync();
    }

    void SetDropHighlight(DragEventArgs? e, bool active)
    {
        if (e is not null)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        DropArea.BorderBrush = new SolidColorBrush(active
            ? Color.FromRgb(0x58, 0xA6, 0xFF)
            : Color.FromRgb(0x44, 0x48, 0x4E));

        DropText.Text = active ? "Release to choose a device" : "Drop files here to send";
    }

    async void OnDrop(object sender, DragEventArgs e)
    {
        SetDropHighlight(null, false);

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;

        var files = paths.Where(File.Exists).ToList();
        if (files.Count == 0)
        {
            SendStatus.Text = "Folders cannot be sent — drop files.";
            return;
        }

        var peers = _service.Peers;
        if (peers.Count == 0)
        {
            SendStatus.Text = "No devices found. Press Scan first.";
            return;
        }

        var target = peers[0];
        SendStatus.Text = $"Sending {files.Count} file(s) to {target.Alias}…";

        try
        {
            await _service.SendAsync(target, files,
                new Progress<SendProgress>(p => SendStatus.Text = $"{p.FileName} ({p.FileIndex}/{p.FileCount})"));

            SendStatus.Text = $"Sent {files.Count} file(s) to {target.Alias}";
        }
        catch (Exception ex)
        {
            SendStatus.Text = ex.Message;
        }
    }

    void Import_Click(object sender, RoutedEventArgs e)
    {
        _ = RunImportAsync();
    }

    async Task RunImportAsync()
    {
        _importCts = new CancellationTokenSource();
        ImportButton.IsEnabled = false;
        CancelImportButton.Visibility = Visibility.Visible;
        ImportProgress.Visibility = Visibility.Visible;
        ImportProgress.Value = 0;

        try
        {
            var result = await _service.ImportPhoneAsync(
                new Progress<ImportProgress>(p =>
                {
                    PhoneStatusText.Text = p.Stage;
                    ImportProgress.IsIndeterminate = p.BytesTotal == 0;
                    ImportProgress.Value = p.Fraction * 100;
                }),
                _importCts.Token);

            PhoneStatusText.Text = result.Failed == 0
                ? $"Imported {result.Copied}, skipped {result.Skipped}"
                : $"Imported {result.Copied}, skipped {result.Skipped}, failed {result.Failed}";
        }
        catch (OperationCanceledException)
        {
            PhoneStatusText.Text = "Import cancelled";
        }
        catch (Exception ex)
        {
            PhoneStatusText.Text = ex.Message;
        }
        finally
        {
            ImportProgress.Visibility = Visibility.Collapsed;
            CancelImportButton.Visibility = Visibility.Collapsed;
            ImportButton.IsEnabled = true;
            _importCts?.Dispose();
            _importCts = null;
        }
    }

    void CancelImport_Click(object sender, RoutedEventArgs e) => _importCts?.Cancel();

    void Folders_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_service.Settings.DownloadFolder);
        Process.Start(new ProcessStartInfo("explorer.exe", _service.Settings.DownloadFolder) { UseShellExecute = true });
    }

    void Quit_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();
}
