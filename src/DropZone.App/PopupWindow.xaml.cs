using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DropZone.App.Model;
using DropZone.LocalSend;
using DropZone.Mtp;
using Microsoft.Win32;

namespace DropZone.App;

public partial class PopupWindow : Window
{
    readonly TransferService _service;

    readonly ObservableCollection<PeerRow> _peers = [];
    readonly ObservableCollection<string> _outgoing = [];
    readonly List<string> _outgoingPaths = [];
    readonly ObservableCollection<HistoryRow> _sent = [];
    readonly ObservableCollection<HistoryRow> _received = [];
    readonly ObservableCollection<MediaRow> _photos = [];
    readonly ObservableCollection<MediaRow> _videos = [];
    readonly ObservableCollection<ScriptRow> _scripts = [];
    readonly ObservableCollection<InterpreterRow> _interpreters = [];

    readonly System.Collections.Concurrent.ConcurrentQueue<MtpItem> _incoming = new();
    CancellationTokenSource? _phoneCts;
    DispatcherTimer? _flushTimer;
    bool _phoneBusy;
    bool _suppressToggleEvents;

    public PopupWindow(TransferService service)
    {
        InitializeComponent();
        _service = service;

        RecipientBox.ItemsSource = _peers;
        OutgoingList.ItemsSource = _outgoing;
        SentList.ItemsSource = _sent;
        ReceivedList.ItemsSource = _received;
        PhotoList.ItemsSource = _photos;
        VideoList.ItemsSource = _videos;
        ScriptList.ItemsSource = _scripts;
        InterpreterList.ItemsSource = _interpreters;

        _service.PeersChanged += () => Dispatcher.Invoke(RefreshPeers);
        _service.HistoryChanged += () => Dispatcher.Invoke(RefreshHistory);
        _service.ScriptInvoked += i => Dispatcher.Invoke(() =>
            StatusLine.Text = i.Allowed
                ? $"{i.Sender} started {i.ScriptName}"
                : $"{i.Sender} tried {i.ScriptName} — {i.Detail}");

        Deactivated += (_, _) => { if (PinButton.IsChecked != true) Hide(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && PinButton.IsChecked != true) Hide(); };

        Drop += OnDrop;
        DragEnter += (_, e) => SetDropHighlight(e, true);
        DragOver += (_, e) => SetDropHighlight(e, true);
        DragLeave += (_, _) => SetDropHighlight(null, false);
    }

    // ---------- lifecycle ----------

    public void ShowInCorner()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 12;
        Top = area.Bottom - Height - 12;

        RefreshAll();
        Show();
        Activate();
        _ = _service.ScanAsync();
    }

    void RefreshAll()
    {
        RefreshSwitches();
        RefreshPeers();
        RefreshHistory();
        RefreshScripts();
        RefreshPhoneStatus();
        StatusLine.Text = (Application.Current as App)?.StartupMessage
                          ?? DescribeNetwork();
    }

    string DescribeNetwork()
    {
        var addresses = _service.ListeningOn;
        return addresses.Count == 0
            ? "Not listening on any network — check your connection"
            : $"On {string.Join(", ", addresses)}";
    }

    void RefreshSwitches()
    {
        _suppressToggleEvents = true;
        ReceiveSwitch.IsChecked = _service.IsReceiving;
        RemoteScriptsSwitch.IsChecked = _service.Settings.AllowRemoteScripts;
        _suppressToggleEvents = false;

        ReceivingHint.Text = _service.IsReceiving
            ? $"Visible as \"{_service.Settings.Alias}\" on this network"
            : "Other devices cannot send to this PC";
    }

    // ---------- peers ----------

    void RefreshPeers()
    {
        var selected = (RecipientBox.SelectedItem as PeerRow)?.Peer.Fingerprint;

        _peers.Clear();
        foreach (var p in _service.Peers) _peers.Add(new PeerRow(p));

        RecipientBox.SelectedItem = _peers.FirstOrDefault(r => r.Peer.Fingerprint == selected) ?? _peers.FirstOrDefault();
    }

    void Scan_Click(object sender, RoutedEventArgs e)
    {
        StatusLine.Text = "Scanning the network…";
        _ = _service.ScanAsync();
    }

    // ---------- history ----------

    void RefreshHistory()
    {
        _sent.Clear();
        foreach (var e in _service.History.By(TransferDirection.Sent)) _sent.Add(new HistoryRow(e));

        _received.Clear();
        foreach (var e in _service.History.By(TransferDirection.Received)) _received.Add(new HistoryRow(e));

        NoSentText.Visibility = _sent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoReceivedText.Visibility = _received.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void HistoryRow_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not HistoryRow row) return;

        var entry = row.Entry;
        var target = entry.Folder is not null && Directory.Exists(entry.Folder)
            ? entry.Folder
            : entry.Files.Select(f => f.Path).FirstOrDefault(File.Exists);

        if (target is null)
        {
            StatusLine.Text = "Those files are no longer on disk";
            return;
        }

        Reveal(target);
    }

    static void Reveal(string path)
    {
        var arguments = Directory.Exists(path) ? $"\"{path}\"" : $"/select,\"{path}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
    }

    // ---------- sending ----------

    void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Multiselect = true, Title = "Choose files to send" };
        if (dialog.ShowDialog(this) != true) return;

        AddOutgoing(dialog.FileNames);
    }

    void AddOutgoing(IEnumerable<string> paths)
    {
        foreach (var path in paths.Where(File.Exists))
        {
            if (_outgoingPaths.Contains(path, StringComparer.OrdinalIgnoreCase)) continue;
            _outgoingPaths.Add(path);
            _outgoing.Add(Path.GetFileName(path));
        }

        DropText.Text = _outgoingPaths.Count == 0
            ? "Drop files here, or use Add files"
            : $"{_outgoingPaths.Count} file(s) ready";
    }

    void ClearFiles_Click(object sender, RoutedEventArgs e)
    {
        _outgoingPaths.Clear();
        _outgoing.Clear();
        DropText.Text = "Drop files here, or use Add files";
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
            : Color.FromRgb(0x4A, 0x4E, 0x54));
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        SetDropHighlight(null, false);

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;

        Tabs.SelectedIndex = 0;
        AddOutgoing(paths);

        var folders = paths.Where(Directory.Exists).ToList();
        if (folders.Count > 0)
            StatusLine.Text = $"Skipped {folders.Count} folder(s) — only files can be sent";
    }

    async void Send_Click(object sender, RoutedEventArgs e)
    {
        if (RecipientBox.SelectedItem is not PeerRow target)
        {
            StatusLine.Text = "Pick a device first — press Scan if the list is empty";
            return;
        }

        if (_outgoingPaths.Count == 0)
        {
            StatusLine.Text = "Add files first";
            return;
        }

        SendButton.IsEnabled = false;
        try
        {
            var files = _outgoingPaths.ToList();
            await _service.SendAsync(target.Peer, files,
                new Progress<SendProgress>(p => StatusLine.Text = $"Sending {p.FileName} ({p.FileIndex}/{p.FileCount})"));

            StatusLine.Text = $"Sent {files.Count} file(s) to {target.Peer.Alias}";
            ClearFiles_Click(sender, e);
        }
        catch (Exception ex)
        {
            StatusLine.Text = ex.Message;
        }
        finally
        {
            SendButton.IsEnabled = true;
        }
    }

    async void SendText_Click(object sender, RoutedEventArgs e)
    {
        if (RecipientBox.SelectedItem is not PeerRow target)
        {
            StatusLine.Text = "Pick a device first";
            return;
        }

        var text = MessageBox_Text();
        if (string.IsNullOrWhiteSpace(text)) return;

        SendTextButton.IsEnabled = false;
        try
        {
            await _service.SendTextAsync(target.Peer, text);
            StatusLine.Text = $"Message sent to {target.Peer.Alias}";
            MessageBox.Clear();
        }
        catch (Exception ex)
        {
            StatusLine.Text = ex.Message;
        }
        finally
        {
            SendTextButton.IsEnabled = true;
        }
    }

    string MessageBox_Text() => MessageBox.Text;

    // ---------- receiving ----------

    void ReceiveSwitch_Changed(object sender, RoutedEventArgs e) => ApplyReceiveToggle(ReceiveSwitch.IsChecked == true);

    async void ApplyReceiveToggle(bool wanted)
    {
        if (_suppressToggleEvents) return;

        try
        {
            await _service.SetReceivingAsync(wanted);
        }
        catch (Exception ex)
        {
            StatusLine.Text = ex.Message;
        }

        RefreshSwitches();
        App.Current.Dispatcher.Invoke(() => (Application.Current as App)?.SyncTrayIcon());
    }

    // ---------- phone ----------

    async void RefreshPhoneStatus()
    {
        // The phone tolerates one conversation at a time, and a status check disconnects when
        // it finishes — which would pull the rug out from under a running scan or import.
        if (_phoneBusy) return;

        PhoneStatusText.Text = "Checking…";
        var status = await Task.Run(_service.PhoneStatus);

        if (_phoneBusy) return;

        PhoneStatusText.Text = status.Describe();
        PhoneScanButton.IsEnabled = status is { Connected: true, Unlocked: true };
    }

    /// <summary>
    /// Rows arrive from a background thread thousands at a time; marshalling each one
    /// individually would spend the whole scan in the dispatcher queue, so they are buffered
    /// and flushed a few times a second.
    /// </summary>
    void StartMediaFlushTimer()
    {
        _flushTimer ??= new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };

        _flushTimer.Tick -= FlushMediaBuffer;
        _flushTimer.Tick += FlushMediaBuffer;
        _flushTimer.Start();
    }

    void FlushMediaBuffer(object? sender, EventArgs e)
    {
        var flushed = 0;
        while (flushed < 500 && _incoming.TryDequeue(out var item))
        {
            var row = new MediaRow(item);
            if (MediaClassifier.Classify(item.Name) == MediaKind.Video) _videos.Add(row);
            else _photos.Add(row);
            flushed++;
        }

        if (flushed == 0) return;

        PhotosTab.Header = $"Photos ({_photos.Count})";
        VideosTab.Header = $"Videos ({_videos.Count})";
        ImportButton.IsEnabled = _photos.Count + _videos.Count > 0;
    }

    async void PhoneScan_Click(object sender, RoutedEventArgs e)
    {
        if (_phoneBusy)
        {
            _phoneCts?.Cancel();
            return;
        }

        _phoneCts = new CancellationTokenSource();
        _phoneBusy = true;

        PhoneScanButton.Content = "Stop";
        PhoneProgress.Visibility = Visibility.Visible;
        PhoneProgress.IsIndeterminate = false;
        PhoneProgress.Value = 0;

        _photos.Clear();
        _videos.Clear();
        _incoming.Clear();
        StartMediaFlushTimer();

        try
        {
            await _service.ScanPhoneAsync(
                _incoming.Enqueue,
                new Progress<ScanProgress>(p =>
                {
                    PhoneStatusText.Text = p.FoldersTotal == 0
                        ? p.Stage
                        : $"{p.Stage} — {p.FilesFound} found ({p.FoldersDone}/{p.FoldersTotal})";
                    PhoneProgress.Value = p.Fraction * 100;
                }),
                _phoneCts.Token);

            PhoneStatusText.Text = $"{_photos.Count + _incoming.Count} files found";
        }
        catch (OperationCanceledException)
        {
            PhoneStatusText.Text = "Scan stopped";
        }
        catch (Exception ex)
        {
            PhoneStatusText.Text = ex.Message;
        }
        finally
        {
            _phoneBusy = false;
            FlushMediaBuffer(null, EventArgs.Empty);
            _flushTimer?.Stop();

            PhoneProgress.Visibility = Visibility.Collapsed;
            PhoneScanButton.Content = "Scan";
            PhoneScanButton.IsEnabled = true;
            PhoneStatusText.Text = $"{_photos.Count} photos, {_videos.Count} videos";
        }
    }

    IEnumerable<MediaRow> AllMedia() => _photos.Concat(_videos);

    void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in AllMedia()) row.Selected = true;
    }

    void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in AllMedia()) row.Selected = false;
    }

    void OpenPhotos_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_service.Settings.PhotoFolder);
        Reveal(_service.Settings.PhotoFolder);
    }

    async void Import_Click(object sender, RoutedEventArgs e)
    {
        var chosen = AllMedia().Where(r => r.Selected).Select(r => r.Item).ToList();
        if (chosen.Count == 0)
        {
            PhoneStatusText.Text = "Tick some files first, or press All";
            return;
        }

        if (_phoneBusy)
        {
            PhoneStatusText.Text = "Wait for the scan to finish, or press Stop";
            return;
        }

        _phoneCts = new CancellationTokenSource();
        _phoneBusy = true;

        ImportButton.IsEnabled = false;
        PhoneScanButton.IsEnabled = false;
        PhoneProgress.Visibility = Visibility.Visible;
        PhoneProgress.IsIndeterminate = false;

        try
        {
            var result = await _service.ImportPhoneAsync(
                chosen,
                new Progress<ImportProgress>(p =>
                {
                    PhoneStatusText.Text = p.Stage;
                    PhoneProgress.Value = p.Fraction * 100;
                }),
                _phoneCts.Token);

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
            _phoneBusy = false;
            PhoneProgress.Visibility = Visibility.Collapsed;
            ImportButton.IsEnabled = true;
            PhoneScanButton.IsEnabled = true;
        }
    }

    // ---------- scripts ----------

    void RefreshScripts()
    {
        _scripts.Clear();
        foreach (var s in _service.Scripts.All()) _scripts.Add(new ScriptRow(s, _service.Scripts));
        NoScriptsText.Visibility = _scripts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (_interpreters.Count == 0)
        {
            foreach (var (extension, command) in _service.Settings.Interpreters.OrderBy(p => p.Key))
                _interpreters.Add(new InterpreterRow(extension, command, OnInterpreterChanged));
        }
    }

    void OnInterpreterChanged(string extension, string command)
    {
        _service.Settings.Interpreters[extension] = command;
        _service.Settings.Save();

        // The registry shares this dictionary instance, so rebuilding the rows is enough to
        // show the new command line.
        RefreshScripts();
        StatusLine.Text = $"{extension} now runs with: {command}";
    }

    void ReloadScripts_Click(object sender, RoutedEventArgs e)
    {
        _service.Scripts.Reload();
        RefreshScripts();
    }

    void OpenScripts_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_service.Settings.ScriptsFolder);
        Reveal(_service.Settings.ScriptsFolder);
    }

    void RunScript_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ScriptRow row) return;

        var arguments = string.IsNullOrWhiteSpace(row.Arguments) ? null : row.Arguments.Trim();

        try
        {
            _service.Scripts.Run(row.Info, arguments);
            StatusLine.Text = arguments is null
                ? $"Started {row.Info.DisplayName}"
                : $"Started {row.Info.DisplayName} {arguments}";
        }
        catch (Exception ex)
        {
            StatusLine.Text = ex.Message;
        }
    }

    void EditScript_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ScriptInfo info) return;

        try
        {
            ScriptRegistry.OpenForEditing(info);
        }
        catch (Exception ex)
        {
            StatusLine.Text = ex.Message;
        }
    }

    void RemoteScript_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: ScriptInfo info } box) return;

        _service.Scripts.SetRemoteEnabled(info.Name, box.IsChecked == true);

        if (box.IsChecked == true && !_service.Settings.AllowRemoteScripts)
            StatusLine.Text = "Also switch on Remote start above";
    }

    void RemoteScripts_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressToggleEvents) return;

        _service.Settings.AllowRemoteScripts = RemoteScriptsSwitch.IsChecked == true;
        _service.Settings.Save();
    }

    // ---------- chrome ----------

    void OpenRoot_Click(object sender, RoutedEventArgs e)
    {
        _service.Settings.EnsureFolders();
        Reveal(_service.Settings.RootFolder);
    }

    void Close_Click(object sender, RoutedEventArgs e) => Hide();
}
