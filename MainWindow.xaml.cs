using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;

namespace LanDrop;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<SelectedItem> _sendItems = [];
    private readonly ObservableCollection<ChatMessage> _messages = [];
    private CancellationTokenSource? _sendCts;
    private CancellationTokenSource? _receiveCts;
    private TcpListener? _listener;
    private string? _pairCode;
    private readonly List<SelectedItem> _pendingSendItems = [];
    private readonly HashSet<string> _sendSessionRoots = new(StringComparer.OrdinalIgnoreCase);
    private bool _sendSessionActive;
    private long _sendTotalBytes;
    private long _sendCompletedBytes;
    private long _sendActualBytes;
    private long _sendSpeedWindowBytes;
    private double _sendSmoothedBytesPerSecond;
    private readonly Stopwatch _sendElapsed = new();
    private readonly Stopwatch _sendSpeedWindow = new();
    private UpdateRelease? _availableUpdate;
    private CancellationTokenSource? _messageCts;
    private TcpListener? _messageListener;
    private TcpClient? _messageClient;
    private NetworkStream? _messageStream;
    private readonly SemaphoreSlim _messageWriteLock = new(1, 1);
    private string? _messageCode;

    public MainWindow()
    {
        InitializeComponent();
        SendItemsList.ItemsSource = _sendItems;
        MessageList.ItemsSource = _messages;
        LoadLastReceiverIp();
        DestinationBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "局域网接收");
        NetworkStateText.Text = TransferProtocol.GetLocalIPv4Addresses().Any()
            ? $"本机：{string.Join(" · ", TransferProtocol.GetLocalIPv4Addresses())}"
            : "未检测到局域网";
        Closed += (_, _) =>
        {
            _sendCts?.Cancel();
            _receiveCts?.Cancel();
            _listener?.Stop();
            _messageCts?.Cancel();
            _messageListener?.Stop();
            _messageClient?.Dispose();
        };
        Loaded += async (_, _) => await CheckForUpdateAsync(showErrors: false);
    }

    private void UpdateEmptyState() => SendEmptyState.Visibility = _sendItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Multiselect = true, Title = "选择要发送的文件" };
        if (dialog.ShowDialog(this) != true) return;
        foreach (string path in dialog.FileNames)
            AddSelectedItem(new SelectedItem(path, Path.GetFileName(path), "文件"));
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择要发送的文件夹", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        string name = new DirectoryInfo(dialog.FolderName).Name;
        AddSelectedItem(new SelectedItem(dialog.FolderName, name, "文件夹"));
    }

    private void AddSelectedItem(SelectedItem item)
    {
        if (_sendItems.Any(x => string.Equals(x.FullPath, item.FullPath, StringComparison.OrdinalIgnoreCase))) return;
        _sendItems.Add(item);
        if (_sendSessionActive)
        {
            _pendingSendItems.Add(item);
            ShareStatusText.Text = $"已加入追加队列 · {_pendingSendItems.Count:N0} 项";
        }
        UpdateEmptyState();
    }

    private void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_sendSessionActive)
        {
            MessageBox.Show(this, "传输期间不能移除已有项目，但可以继续添加文件或文件夹到追加队列。", "传输正在进行", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        foreach (SelectedItem item in SendItemsList.SelectedItems.Cast<SelectedItem>().ToList()) _sendItems.Remove(item);
        UpdateEmptyState();
    }

    private async void StartShare_Click(object sender, RoutedEventArgs e)
    {
        if (_sendCts is not null)
        {
            _sendCts.Cancel();
            return;
        }
        if (_receiveCts is not null)
        {
            MessageBox.Show(this, "请先停止接收端的等待或连接。", "正在接收", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_sendItems.Count == 0)
        {
            MessageBox.Show(this, "请先添加至少一个文件或文件夹。", "还没有内容", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!IPAddress.TryParse(SendTargetIpBox.Text.Trim(), out IPAddress? ip))
        {
            MessageBox.Show(this, "请输入接收电脑显示的 IP 地址。", "地址无效", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        string code = SendPairCodeBox.Text.Trim();
        if (code.Length != 6 || !code.All(char.IsDigit))
        {
            MessageBox.Show(this, "请输入接收电脑显示的六位配对码。", "需要配对码", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        List<TransferFile> manifest;
        _sendSessionActive = true;
        _pendingSendItems.Clear();
        _sendSessionRoots.Clear();
        List<SelectedItem> initialItems = _sendItems.ToList();
        try
        {
            ShareStatusText.Text = "正在整理文件清单…";
            StartShareButton.IsEnabled = false;
            manifest = await Task.Run(() => TransferProtocol.BuildManifest(initialItems, _sendSessionRoots));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"读取文件夹失败：{ex.Message}", "无法开始", MessageBoxButton.OK, MessageBoxImage.Error);
            ShareStatusText.Text = "准备就绪";
            StartShareButton.IsEnabled = true;
            _sendSessionActive = false;
            return;
        }

        if (manifest.Count == 0)
        {
            MessageBox.Show(this, "选择的目录中没有可发送的文件。", "没有文件", MessageBoxButton.OK, MessageBoxImage.Information);
            ShareStatusText.Text = "准备就绪";
            StartShareButton.IsEnabled = true;
            _sendSessionActive = false;
            return;
        }

        SaveLastReceiverIp(ip.ToString());
        _sendCts = new CancellationTokenSource();
        StartShareButton.IsEnabled = true;
        StartShareButton.Content = "取消发送";
        WaitForReceiverButton.IsEnabled = false;
        try
        {
            await SendFilesAsync(ip, code, manifest, _sendCts.Token);
        }
        catch (OperationCanceledException)
        {
            ShareStatusText.Text = "发送已暂停，可重新连接续传";
        }
        catch (Exception ex)
        {
            ShareStatusText.Text = "发送失败";
            MessageBox.Show(this, ex.Message, "发送失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _sendCts.Dispose();
            _sendCts = null;
            StartShareButton.Content = "连接并发送";
            StartShareButton.IsEnabled = true;
            WaitForReceiverButton.IsEnabled = true;
            _sendSessionActive = false;
        }
    }

    private async Task SendFilesAsync(IPAddress ip, string code, List<TransferFile> manifest, CancellationToken token)
    {
        ShareStatusText.Text = $"正在连接 {ip}…";
        using var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(ip, TransferProtocol.TransferPort, token);
        NetworkStream stream = client.GetStream();
        await TransferProtocol.WriteMessageAsync(stream, new WireMessage { Type = "hello", Code = code, ProtocolVersion = 3 }, token);
        WireMessage ready = await TransferProtocol.ReadMessageAsync(stream, token);
        if (ready.Type == "error") throw new InvalidOperationException(ready.Error ?? "接收端拒绝了连接。");
        if (ready.Type != "ready") throw new InvalidDataException("接收端响应无效。");

        await SendQueuedBatchesAsync(stream, manifest, ready.ProtocolVersion >= 3, token);
    }

    private async Task SendQueuedBatchesAsync(NetworkStream stream, List<TransferFile> firstBatch, bool supportsQueue, CancellationToken token)
    {
        ResetSenderProgress(firstBatch.Sum(x => x.Length));
        List<TransferFile> batch = firstBatch;
        while (true)
        {
            await SendManifestAsync(stream, batch, token);
            if (!supportsQueue) break;

            List<SelectedItem> pending = _pendingSendItems.ToList();
            _pendingSendItems.Clear();
            if (pending.Count == 0) break;
            ShareStatusText.Text = $"正在整理追加队列 · {pending.Count:N0} 项";
            batch = await Task.Run(() => TransferProtocol.BuildManifest(pending, _sendSessionRoots), token);
            _sendTotalBytes += batch.Sum(x => x.Length);
        }
        if (supportsQueue)
            await TransferProtocol.WriteMessageAsync(stream, new WireMessage { Type = "sessionDone", ProtocolVersion = 3 }, token);
        UpdateSenderProgress("全部批次发送完成", 0, 0);
        ShareStatusText.Text = supportsQueue || _pendingSendItems.Count == 0
            ? "全部发送完成"
            : $"本次接收端不支持追加队列 · {_pendingSendItems.Count:N0} 项留待下次";
    }

    private async Task SendManifestAsync(NetworkStream stream, List<TransferFile> manifest, CancellationToken token)
    {

        long total = manifest.Sum(f => f.Length);
        await TransferProtocol.WriteMessageAsync(stream, new WireMessage
        {
            Type = "manifest", FileCount = manifest.Count, TotalBytes = total, ProtocolVersion = 3
        }, token);

        byte[] buffer = new byte[1024 * 1024];
        for (int index = 0; index < manifest.Count; index++)
        {
            TransferFile file = manifest[index];
            ShareStatusText.Text = $"校验 {index + 1:N0}/{manifest.Count:N0} · {file.RelativePath}";
            string hash = await TransferProtocol.HashFileAsync(file.SourcePath, token);
            await TransferProtocol.WriteMessageAsync(stream, new WireMessage
            {
                Type = "file", Path = file.RelativePath, Length = file.Length,
                LastWriteUtcTicks = file.LastWriteUtcTicks, Hash = hash
            }, token);
            WireMessage resume = await TransferProtocol.ReadMessageAsync(stream, token);
            if (resume.Type != "resume") throw new InvalidDataException("接收端没有返回续传位置。");
            long offset = Math.Clamp(resume.Offset, 0, file.Length);
            _sendCompletedBytes += offset;
            _sendSpeedWindowBytes = 0;
            _sendSpeedWindow.Restart();
            ShareStatusText.Text = $"发送 {index + 1:N0}/{manifest.Count:N0} · {file.RelativePath}";
            UpdateSenderProgress(file.RelativePath, index + 1, manifest.Count);
            await using var source = new FileStream(file.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan);
            source.Position = offset;
            int read;
            while ((read = await source.ReadAsync(buffer, token)) > 0)
            {
                await stream.WriteAsync(buffer.AsMemory(0, read), token);
                _sendCompletedBytes += read;
                _sendActualBytes += read;
                _sendSpeedWindowBytes += read;
                UpdateSenderProgress(file.RelativePath, index + 1, manifest.Count);
            }
        }
        await TransferProtocol.WriteMessageAsync(stream, new WireMessage { Type = "done", ProtocolVersion = 3 }, token);
    }

    private void ResetSenderProgress(long totalBytes)
    {
        _sendTotalBytes = totalBytes;
        _sendCompletedBytes = 0;
        _sendActualBytes = 0;
        _sendSpeedWindowBytes = 0;
        _sendSmoothedBytesPerSecond = 0;
        _sendElapsed.Restart();
        _sendSpeedWindow.Restart();
        SenderProgress.Value = 0;
        SenderProgressDetailText.Text = $"0% · 0 B / {FormatBytes(totalBytes)}";
        SenderSpeedText.Text = "计算速度中…";
        SenderEtaText.Text = "预计剩余时间：计算中…";
    }

    private void UpdateSenderProgress(string path, int index, int count)
    {
        double windowSeconds = _sendSpeedWindow.Elapsed.TotalSeconds;
        if (windowSeconds >= 0.5)
        {
            double instant = _sendSpeedWindowBytes / windowSeconds;
            _sendSmoothedBytesPerSecond = _sendSmoothedBytesPerSecond <= 0
                ? instant
                : _sendSmoothedBytesPerSecond * 0.75 + instant * 0.25;
            _sendSpeedWindowBytes = 0;
            _sendSpeedWindow.Restart();
        }

        double percent = _sendTotalBytes == 0 ? 100 : Math.Clamp(_sendCompletedBytes * 100d / _sendTotalBytes, 0, 100);
        SenderProgress.Value = percent;
        SenderProgressDetailText.Text = $"{percent:F1}% · {FormatBytes(_sendCompletedBytes)} / {FormatBytes(_sendTotalBytes)} · 已用 {FormatDuration(_sendElapsed.Elapsed)}";
        SenderCurrentFileText.Text = index > 0 ? $"{index:N0}/{count:N0} · {path}" : path;
        if (_sendSmoothedBytesPerSecond <= 0)
        {
            SenderSpeedText.Text = "计算速度中…";
            SenderEtaText.Text = "预计剩余时间：计算中…";
            return;
        }

        double averageBytesPerSecond = _sendActualBytes / Math.Max(0.001, _sendElapsed.Elapsed.TotalSeconds);
        SenderSpeedText.Text = $"实时 {FormatSpeed(_sendSmoothedBytesPerSecond)} · 平均 {FormatSpeed(averageBytesPerSecond)}";
        double etaSpeed = averageBytesPerSecond > 0 ? averageBytesPerSecond : _sendSmoothedBytesPerSecond;
        double remainingSeconds = Math.Max(0, _sendTotalBytes - _sendCompletedBytes) / etaSpeed;
        TimeSpan eta = TimeSpan.FromSeconds(Math.Min(remainingSeconds, TimeSpan.FromDays(30).TotalSeconds));
        SenderEtaText.Text = $"预计剩余 {FormatDuration(eta)} · {DateTime.Now.Add(eta):HH:mm} 完成";
    }

    private static string FormatDuration(TimeSpan value)
    {
        if (value.TotalHours >= 24) return $"{(int)value.TotalDays}天 {value.Hours}小时";
        if (value.TotalHours >= 1) return $"{(int)value.TotalHours}小时 {value.Minutes}分";
        if (value.TotalMinutes >= 1) return $"{(int)value.TotalMinutes}分 {value.Seconds}秒";
        return $"{Math.Max(0, value.Seconds)}秒";
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate is null)
        {
            await CheckForUpdateAsync(showErrors: true);
            if (_availableUpdate is null) return;
        }

        MessageBoxResult answer = MessageBox.Show(this,
            $"发现 {_availableUpdate.Tag}，下载并自动安装吗？\n\n更新时程序会退出并重新启动，请勿在传输过程中更新。",
            "发现新版本", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        if (_sendCts is not null || _receiveCts is not null)
        {
            MessageBox.Show(this, "请等待当前传输结束后再更新。", "传输正在进行", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        CheckUpdateButton.IsEnabled = false;
        try
        {
            var progress = new Progress<string>(text => UpdateStatusText.Text = text);
            await UpdateService.DownloadAndInstallAsync(_availableUpdate, progress, CancellationToken.None);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = "更新失败";
            CheckUpdateButton.IsEnabled = true;
            MessageBox.Show(this, ex.Message, "无法更新", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task CheckForUpdateAsync(bool showErrors)
    {
        CheckUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = $"当前 v{UpdateService.CurrentVersion.ToString(3)} · 正在检查…";
        try
        {
            _availableUpdate = await UpdateService.CheckAsync(CancellationToken.None);
            if (_availableUpdate is null)
            {
                UpdateStatusText.Text = $"当前 v{UpdateService.CurrentVersion.ToString(3)} · 已是最新版";
                if (showErrors) MessageBox.Show(this, "当前已经是最新版。", "检查更新", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                UpdateStatusText.Text = $"发现 {_availableUpdate.Tag}";
                CheckUpdateButton.Content = "下载更新";
            }
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = $"当前 v{UpdateService.CurrentVersion.ToString(3)} · 暂时无法检查";
            if (showErrors) MessageBox.Show(this, ex.Message, "检查更新失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { CheckUpdateButton.IsEnabled = true; }
    }

    private async void MessageListen_Click(object sender, RoutedEventArgs e)
    {
        if (_messageCts is not null)
        {
            StopMessageSession("消息连接已断开");
            return;
        }

        _messageCode = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
        _messageCts = new CancellationTokenSource();
        CancellationToken token = _messageCts.Token;
        MessageLocalCodeText.Text = $"本机配对码 {_messageCode}";
        MessageStatusText.Text = $"正在等待对方连接 · TCP {TransferProtocol.MessagePort}";
        SetMessageControls(active: true, connected: false);
        try
        {
            _messageListener = new TcpListener(IPAddress.Any, TransferProtocol.MessagePort);
            _messageListener.Start();
            using TcpClient client = await _messageListener.AcceptTcpClientAsync(token);
            _messageListener.Stop();
            _messageListener = null;
            client.NoDelay = true;
            NetworkStream stream = client.GetStream();
            WireMessage hello = await TransferProtocol.ReadMessageAsync(stream, token);
            if (hello.Type != "messageHello" || hello.Code != _messageCode)
            {
                await TransferProtocol.WriteMessageAsync(stream,
                    new WireMessage { Type = "error", Error = "消息配对码不正确。" }, token);
                throw new InvalidOperationException("连接方提供的消息配对码不正确。");
            }
            await TransferProtocol.WriteMessageAsync(stream,
                new WireMessage { Type = "messageReady", Sender = Environment.MachineName }, token);
            await RunMessageSessionAsync(client, hello.Sender, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            MessageStatusText.Text = "消息连接失败";
            MessageBox.Show(this, ex.Message, "消息连接失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { StopMessageSession(MessageStatusText.Text == "消息连接失败" ? "消息连接失败" : "消息连接已断开"); }
    }

    private async void MessageConnect_Click(object sender, RoutedEventArgs e)
    {
        if (_messageCts is not null)
        {
            StopMessageSession("消息连接已断开");
            return;
        }
        if (!IPAddress.TryParse(MessagePeerIpBox.Text.Trim(), out IPAddress? ip))
        {
            MessageBox.Show(this, "请输入正确的对方电脑 IP。", "地址无效", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        string code = MessagePeerCodeBox.Text.Trim();
        if (code.Length != 6 || !code.All(char.IsDigit))
        {
            MessageBox.Show(this, "请输入对方显示的六位消息配对码。", "需要配对码", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _messageCts = new CancellationTokenSource();
        CancellationToken token = _messageCts.Token;
        MessageStatusText.Text = $"正在连接 {ip}…";
        MessageLocalCodeText.Text = "";
        SetMessageControls(active: true, connected: false);
        try
        {
            var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(ip, TransferProtocol.MessagePort, token);
            NetworkStream stream = client.GetStream();
            await TransferProtocol.WriteMessageAsync(stream, new WireMessage
            {
                Type = "messageHello", Code = code, Sender = Environment.MachineName
            }, token);
            WireMessage ready = await TransferProtocol.ReadMessageAsync(stream, token);
            if (ready.Type == "error") throw new InvalidOperationException(ready.Error ?? "对方拒绝了消息连接。");
            if (ready.Type != "messageReady") throw new InvalidDataException("对方返回了无效的消息响应。");
            await RunMessageSessionAsync(client, ready.Sender, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            MessageStatusText.Text = "消息连接失败";
            MessageBox.Show(this, ex.Message, "消息连接失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { StopMessageSession(MessageStatusText.Text == "消息连接失败" ? "消息连接失败" : "消息连接已断开"); }
    }

    private async Task RunMessageSessionAsync(TcpClient client, string? peerName, CancellationToken token)
    {
        _messageClient = client;
        _messageStream = client.GetStream();
        string peerAddress = ((IPEndPoint?)client.Client.RemoteEndPoint)?.Address.ToString() ?? "对方";
        MessagePeerIpBox.Text = peerAddress;
        MessageStatusText.Text = $"已连接 · {peerName ?? peerAddress}";
        SetMessageControls(active: true, connected: true);
        _messages.Add(new ChatMessage("系统", $"已与 {peerName ?? peerAddress} 建立消息连接。", DateTime.Now, false));

        while (!token.IsCancellationRequested)
        {
            WireMessage message = await TransferProtocol.ReadMessageAsync(_messageStream, token);
            if (message.Type != "chat" || string.IsNullOrEmpty(message.Text)) continue;
            _messages.Add(new ChatMessage(message.Sender ?? peerName ?? "对方", message.Text, DateTime.Now, false));
            MessageList.ScrollIntoView(_messages[^1]);
        }
    }

    private async void SendMessage_Click(object sender, RoutedEventArgs e)
    {
        string content = MessageComposerBox.Text;
        if (string.IsNullOrWhiteSpace(content) || _messageStream is null || _messageCts is null) return;
        if (content.Length > 65_536)
        {
            MessageBox.Show(this, "单条消息最多 65,536 个字符。", "消息过长", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await _messageWriteLock.WaitAsync();
        try
        {
            await TransferProtocol.WriteMessageAsync(_messageStream, new WireMessage
            {
                Type = "chat", Text = content, Sender = Environment.MachineName
            }, _messageCts.Token);
            _messages.Add(new ChatMessage("我", content, DateTime.Now, true));
            MessageComposerBox.Clear();
            MessageList.ScrollIntoView(_messages[^1]);
            MessageComposerBox.Focus();
        }
        catch (Exception ex)
        {
            MessageStatusText.Text = "消息发送失败";
            MessageBox.Show(this, ex.Message, "消息发送失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _messageWriteLock.Release(); }
    }

    private void PasteMessage_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                string pasted = Clipboard.GetText();
                int caret = MessageComposerBox.CaretIndex;
                MessageComposerBox.Text = MessageComposerBox.Text.Insert(caret, pasted);
                MessageComposerBox.CaretIndex = caret + pasted.Length;
                MessageComposerBox.Focus();
            }
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "无法读取剪贴板", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void CopyMessage_Click(object sender, RoutedEventArgs e)
    {
        if (MessageList.SelectedItem is not ChatMessage message)
        {
            MessageBox.Show(this, "请先在消息记录中选中一条消息。", "尚未选择", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            Clipboard.SetText(message.Text);
            MessageStatusText.Text = "已复制选中消息";
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "无法写入剪贴板", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void StopMessageSession(string status)
    {
        _messageCts?.Cancel();
        _messageListener?.Stop();
        _messageClient?.Dispose();
        _messageCts?.Dispose();
        _messageCts = null;
        _messageListener = null;
        _messageClient = null;
        _messageStream = null;
        MessageStatusText.Text = status;
        MessageLocalCodeText.Text = "";
        SetMessageControls(active: false, connected: false);
    }

    private void SetMessageControls(bool active, bool connected)
    {
        MessageListenButton.Content = active ? "断开消息连接" : "开始等待消息";
        MessageConnectButton.Content = active ? "断开" : "连接";
        MessageListenButton.IsEnabled = true;
        MessageConnectButton.IsEnabled = true;
        MessagePeerIpBox.IsEnabled = !active;
        MessagePeerCodeBox.IsEnabled = !active;
        SendMessageButton.IsEnabled = connected;
    }

    private async void WaitForReceiver_Click(object sender, RoutedEventArgs e)
    {
        if (_sendCts is not null)
        {
            _sendCts.Cancel();
            _listener?.Stop();
            return;
        }
        if (_receiveCts is not null)
        {
            MessageBox.Show(this, "请先停止接收端的等待或连接。", "端口正在使用", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_sendItems.Count == 0)
        {
            MessageBox.Show(this, "请先添加至少一个文件或文件夹。", "还没有内容", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        List<TransferFile> manifest;
        _sendSessionActive = true;
        _pendingSendItems.Clear();
        _sendSessionRoots.Clear();
        List<SelectedItem> initialItems = _sendItems.ToList();
        try
        {
            ShareStatusText.Text = "正在整理文件清单…";
            WaitForReceiverButton.IsEnabled = false;
            manifest = await Task.Run(() => TransferProtocol.BuildManifest(initialItems, _sendSessionRoots));
            if (manifest.Count == 0) throw new InvalidOperationException("选择的目录中没有可发送的文件。");
        }
        catch (Exception ex)
        {
            ShareStatusText.Text = "准备就绪";
            WaitForReceiverButton.IsEnabled = true;
            _sendSessionActive = false;
            MessageBox.Show(this, ex.Message, "无法开始", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _pairCode = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
        _sendCts = new CancellationTokenSource();
        CancellationToken token = _sendCts.Token;
        WaitForReceiverButton.IsEnabled = true;
        WaitForReceiverButton.Content = "停止等待";
        StartShareButton.IsEnabled = false;
        ShareStatusText.Text = $"等待接收端连接 · 配对码 {_pairCode}";
        try
        {
            _listener = new TcpListener(IPAddress.Any, TransferProtocol.TransferPort);
            _listener.Start();
            _ = Task.Run(() => TransferProtocol.RunDiscoveryResponderAsync(token), token);
            using TcpClient client = await _listener.AcceptTcpClientAsync(token);
            _listener.Stop();
            _listener = null;
            client.NoDelay = true;
            NetworkStream stream = client.GetStream();
            WireMessage hello = await TransferProtocol.ReadMessageAsync(stream, token);
            if (hello.Type != "hello" || hello.Code != _pairCode)
            {
                await TransferProtocol.WriteMessageAsync(stream, new WireMessage { Type = "error", Error = "配对码不正确。" }, token);
                throw new InvalidOperationException("连接方提供的配对码不正确。");
            }
            // 1.x 接收端的 hello 没有协议版本，因此仍只发送一个批次。
            await SendQueuedBatchesAsync(stream, manifest, hello.ProtocolVersion >= 3, token);
        }
        catch (OperationCanceledException) { ShareStatusText.Text = "已停止等待"; }
        catch (Exception ex)
        {
            ShareStatusText.Text = "发送失败";
            MessageBox.Show(this, ex.Message, "发送失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _listener?.Stop();
            _listener = null;
            _sendCts.Dispose();
            _sendCts = null;
            WaitForReceiverButton.Content = "等待接收端连接";
            WaitForReceiverButton.IsEnabled = true;
            StartShareButton.IsEnabled = true;
            _sendSessionActive = false;
        }
    }

    private async void Discover_Click(object sender, RoutedEventArgs e)
    {
        ((Button)sender).IsEnabled = false;
        ShareStatusText.Text = "正在搜索接收电脑…";
        try
        {
            string? ip = await TransferProtocol.DiscoverAsync(CancellationToken.None);
            if (ip is null)
                ShareStatusText.Text = "未自动发现；跨网段请手动输入接收电脑 IP";
            else
            {
                SendTargetIpBox.Text = ip;
                ShareStatusText.Text = $"已找到接收电脑 {ip}";
            }
        }
        catch (Exception ex) { ShareStatusText.Text = ex.Message; }
        finally { ((Button)sender).IsEnabled = true; }
    }

    private void ChooseDestination_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择接收文件的保存位置", InitialDirectory = DestinationBox.Text };
        if (dialog.ShowDialog(this) == true) DestinationBox.Text = dialog.FolderName;
    }

    private async void Receive_Click(object sender, RoutedEventArgs e)
    {
        if (_receiveCts is not null)
        {
            _receiveCts.Cancel();
            _listener?.Stop();
            return;
        }
        if (_sendCts is not null)
        {
            MessageBox.Show(this, "请先停止发送端的等待或连接。", "正在发送", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (string.IsNullOrWhiteSpace(DestinationBox.Text)) return;

        _pairCode = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
        _receiveCts = new CancellationTokenSource();
        CancellationToken token = _receiveCts.Token;
        ReceiveButton.Content = "停止等待";
        ConnectSenderButton.IsEnabled = false;
        ReceiveStatusText.Text = "正在等待发送电脑主动连接";
        ReceivePairCodeText.Text = $"配对码 {_pairCode}";
        CurrentFileText.Text = "在发送电脑输入本机 IP 和上方配对码。";
        TransferProgress.Value = 0;
        ProgressDetailText.Text = "0%";
        SpeedText.Text = "";
        ReceiveEtaText.Text = "";

        try
        {
            Directory.CreateDirectory(DestinationBox.Text);
            _listener = new TcpListener(IPAddress.Any, TransferProtocol.TransferPort);
            _listener.Start();
            _ = Task.Run(() => TransferProtocol.RunDiscoveryResponderAsync(token), token);
            using TcpClient client = await _listener.AcceptTcpClientAsync(token);
            _listener.Stop();
            _listener = null;
            await ReceiveFilesAsync(client, _pairCode, DestinationBox.Text, token);
        }
        catch (OperationCanceledException)
        {
            ReceiveStatusText.Text = "接收已暂停";
            CurrentFileText.Text = "再次开始等待并使用新配对码，即可从未完成的位置继续。";
        }
        catch (Exception ex)
        {
            ReceiveStatusText.Text = "接收失败";
            CurrentFileText.Text = ex.Message;
            MessageBox.Show(this, ex.Message, "接收失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _listener?.Stop();
            _listener = null;
            _receiveCts.Dispose();
            _receiveCts = null;
            ReceiveButton.Content = "开始等待发送";
            ConnectSenderButton.IsEnabled = true;
        }
    }

    private async void ConnectSender_Click(object sender, RoutedEventArgs e)
    {
        if (_receiveCts is not null)
        {
            _receiveCts.Cancel();
            _listener?.Stop();
            return;
        }
        if (_sendCts is not null)
        {
            MessageBox.Show(this, "请先停止发送端的等待或连接。", "正在发送", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!IPAddress.TryParse(LegacyServerIpBox.Text.Trim(), out IPAddress? ip))
        {
            MessageBox.Show(this, "请输入发送电脑的 IP 地址。", "地址无效", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        string code = LegacyPairCodeBox.Text.Trim();
        if (code.Length != 6 || !code.All(char.IsDigit))
        {
            MessageBox.Show(this, "请输入发送电脑显示的六位配对码。", "需要配对码", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (string.IsNullOrWhiteSpace(DestinationBox.Text)) return;

        _receiveCts = new CancellationTokenSource();
        CancellationToken token = _receiveCts.Token;
        ConnectSenderButton.Content = "取消连接";
        ReceiveButton.IsEnabled = false;
        ReceiveStatusText.Text = $"正在主动连接 {ip}…";
        ReceivePairCodeText.Text = "旧连接方向";
        TransferProgress.Value = 0;
        ProgressDetailText.Text = "0%";
        SpeedText.Text = "";
        ReceiveEtaText.Text = "";
        try
        {
            Directory.CreateDirectory(DestinationBox.Text);
            using var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(ip, TransferProtocol.TransferPort, token);
            NetworkStream stream = client.GetStream();
            await TransferProtocol.WriteMessageAsync(stream, new WireMessage { Type = "hello", Code = code, ProtocolVersion = 3 }, token);
            WireMessage manifest = await TransferProtocol.ReadMessageAsync(stream, token);
            if (manifest.Type == "error") throw new InvalidOperationException(manifest.Error ?? "发送端拒绝了连接。");
            if (manifest.Type != "manifest") throw new InvalidDataException("发送端响应无效。");
            await ReceiveBatchSequenceAsync(stream, DestinationBox.Text, manifest, token);
        }
        catch (OperationCanceledException)
        {
            ReceiveStatusText.Text = "接收已暂停";
            CurrentFileText.Text = "再次使用相同保存位置即可续传。";
        }
        catch (Exception ex)
        {
            ReceiveStatusText.Text = "接收失败";
            CurrentFileText.Text = ex.Message;
            MessageBox.Show(this, ex.Message, "接收失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _receiveCts.Dispose();
            _receiveCts = null;
            ConnectSenderButton.Content = "主动连接发送端";
            ConnectSenderButton.IsEnabled = true;
            ReceiveButton.IsEnabled = true;
        }
    }

    private async Task ReceiveFilesAsync(TcpClient client, string code, string destination, CancellationToken token)
    {
        client.NoDelay = true;
        NetworkStream stream = client.GetStream();
        WireMessage hello = await TransferProtocol.ReadMessageAsync(stream, token);
        if (hello.Type != "hello" || hello.Code != code)
        {
            await TransferProtocol.WriteMessageAsync(stream, new WireMessage { Type = "error", Error = "配对码不正确。" }, token);
            throw new InvalidOperationException("连接方提供的配对码不正确。");
        }
        await TransferProtocol.WriteMessageAsync(stream, new WireMessage { Type = "ready", ProtocolVersion = 3 }, token);
        WireMessage manifest = await TransferProtocol.ReadMessageAsync(stream, token);
        if (manifest.Type != "manifest") throw new InvalidDataException("发送端没有提供有效的文件清单。");

        await ReceiveBatchSequenceAsync(stream, destination, manifest, token);
    }

    private async Task ReceiveBatchSequenceAsync(NetworkStream stream, string destination, WireMessage firstManifest, CancellationToken token)
    {
        WireMessage manifest = firstManifest;
        while (true)
        {
            await ReceivePayloadAsync(stream, destination, manifest, token);
            if (manifest.ProtocolVersion < 3) return;
            WireMessage next = await TransferProtocol.ReadMessageAsync(stream, token);
            if (next.Type == "sessionDone") return;
            if (next.Type != "manifest") throw new InvalidDataException("发送端提供了无效的追加批次。");
            manifest = next;
            ReceiveStatusText.Text = $"正在接收追加批次 · {manifest.FileCount:N0} 个文件";
        }
    }

    private async Task ReceivePayloadAsync(NetworkStream stream, string destination, WireMessage manifest, CancellationToken token)
    {

        long completedBeforeSession = 0;
        long sessionBytes = 0;
        long speedWindowBytes = 0;
        double currentBytesPerSecond = 0;
        var speedWindow = Stopwatch.StartNew();
        byte[] buffer = new byte[1024 * 1024];
        ReceiveStatusText.Text = $"正在接收 {manifest.FileCount:N0} 个文件";

        for (int index = 0; index < manifest.FileCount; index++)
        {
            WireMessage header = await TransferProtocol.ReadMessageAsync(stream, token);
            if (header.Type != "file" || string.IsNullOrWhiteSpace(header.Path))
                throw new InvalidDataException("收到无效的文件信息。");

            string finalPath = TransferProtocol.SafeDestinationPath(destination, header.Path);
            string partialPath = finalPath + ".landrop.part";
            Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

            if (File.Exists(finalPath) && new FileInfo(finalPath).Length == header.Length)
            {
                string existingHash = await TransferProtocol.HashFileAsync(finalPath, token);
                if (string.Equals(existingHash, header.Hash, StringComparison.OrdinalIgnoreCase))
                {
                    await TransferProtocol.WriteMessageAsync(stream, new WireMessage { Type = "resume", Offset = header.Length }, token);
                    completedBeforeSession += header.Length;
                    UpdateProgress(manifest.TotalBytes, completedBeforeSession + sessionBytes, currentBytesPerSecond, header.Path, index + 1, manifest.FileCount);
                    continue;
                }
            }

            long offset = File.Exists(partialPath) ? Math.Min(new FileInfo(partialPath).Length, header.Length) : 0;
            completedBeforeSession += offset;
            await TransferProtocol.WriteMessageAsync(stream, new WireMessage { Type = "resume", Offset = offset }, token);
            speedWindow.Restart();
            speedWindowBytes = 0;

            await using (var target = new FileStream(partialPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None,
                buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                target.SetLength(offset);
                target.Position = offset;
                long remaining = header.Length - offset;
                while (remaining > 0)
                {
                    int wanted = (int)Math.Min(buffer.Length, remaining);
                    int read = await stream.ReadAsync(buffer.AsMemory(0, wanted), token);
                    if (read == 0) throw new EndOfStreamException("发送电脑提前断开连接。");
                    await target.WriteAsync(buffer.AsMemory(0, read), token);
                    remaining -= read;
                    sessionBytes += read;
                    speedWindowBytes += read;
                    double elapsedSeconds = speedWindow.Elapsed.TotalSeconds;
                    if (elapsedSeconds >= 0.5)
                    {
                        currentBytesPerSecond = speedWindowBytes / elapsedSeconds;
                        speedWindow.Restart();
                        speedWindowBytes = 0;
                    }
                    else if (elapsedSeconds >= 0.05)
                    {
                        currentBytesPerSecond = speedWindowBytes / elapsedSeconds;
                    }
                    UpdateProgress(manifest.TotalBytes, completedBeforeSession + sessionBytes, currentBytesPerSecond, header.Path, index + 1, manifest.FileCount);
                }
            }

            string receivedHash = await TransferProtocol.HashFileAsync(partialPath, token);
            if (!string.Equals(receivedHash, header.Hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"文件校验失败：{header.Path}。重新开始接收会再次尝试。");

            File.Move(partialPath, finalPath, true);
            File.SetLastWriteTimeUtc(finalPath, new DateTime(header.LastWriteUtcTicks, DateTimeKind.Utc));
        }

        WireMessage done = await TransferProtocol.ReadMessageAsync(stream, token);
        if (done.Type != "done") throw new InvalidDataException("传输没有正常结束。");
        TransferProgress.Value = 100;
        ProgressDetailText.Text = "100%";
        ReceiveStatusText.Text = "接收完成";
        CurrentFileText.Text = $"文件已保存到 {destination}";
        SpeedText.Text = currentBytesPerSecond > 0 ? $"完成 · {FormatSpeed(currentBytesPerSecond)}" : "完成";
        ReceiveEtaText.Text = "预计剩余 0秒";
    }

    private void UpdateProgress(long total, long completed, double bytesPerSecond, string path, int index, int count)
    {
        double percent = total == 0 ? 100 : Math.Clamp(completed * 100d / total, 0, 100);
        TransferProgress.Value = percent;
        ProgressDetailText.Text = $"{percent:F1}% · {FormatBytes(completed)} / {FormatBytes(total)}";
        SpeedText.Text = bytesPerSecond > 0 ? FormatSpeed(bytesPerSecond) : "计算速度中…";
        if (bytesPerSecond > 0)
        {
            double remainingSeconds = Math.Max(0, total - completed) / bytesPerSecond;
            TimeSpan eta = TimeSpan.FromSeconds(Math.Min(remainingSeconds, TimeSpan.FromDays(30).TotalSeconds));
            ReceiveEtaText.Text = $"预计剩余 {FormatDuration(eta)} · {DateTime.Now.Add(eta):HH:mm} 完成";
        }
        else ReceiveEtaText.Text = "预计剩余时间：计算中…";
        CurrentFileText.Text = $"{index:N0}/{count:N0} · {path}";
    }

    private static string SettingsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LanDrop");

    private void LoadLastReceiverIp()
    {
        try
        {
            string path = Path.Combine(SettingsDirectory, "last-receiver.txt");
            if (!File.Exists(path)) return;
            string value = File.ReadAllText(path).Trim();
            if (IPAddress.TryParse(value, out _))
            {
                SendTargetIpBox.Text = value;
                MessagePeerIpBox.Text = value;
            }
        }
        catch { }
    }

    private static void SaveLastReceiverIp(string ip)
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            File.WriteAllText(Path.Combine(SettingsDirectory, "last-receiver.txt"), ip);
        }
        catch { }
    }

    private static string FormatSpeed(double bytesPerSecond) => $"{FormatBytes((long)bytesPerSecond)}/s";
    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:F1} {units[unit]}";
    }
}
