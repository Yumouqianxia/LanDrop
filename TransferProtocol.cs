using System.Buffers;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LanDrop;

public static class TransferProtocol
{
    public const int DiscoveryPort = 49550;
    public const int TransferPort = 49551;
    public const int MessagePort = 49552;
    private const string DiscoveryQuery = "LANDROP_DISCOVER_V1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task WriteMessageAsync(Stream stream, WireMessage message, CancellationToken token)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        byte[] length = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(payload.Length));
        await stream.WriteAsync(length, token);
        await stream.WriteAsync(payload, token);
        await stream.FlushAsync(token);
    }

    public static async Task<WireMessage> ReadMessageAsync(Stream stream, CancellationToken token)
    {
        byte[] lengthBytes = new byte[4];
        await stream.ReadExactlyAsync(lengthBytes, token);
        int length = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(lengthBytes));
        if (length is <= 0 or > 1_048_576) throw new InvalidDataException("收到无效的控制消息。");
        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, token);
        return JsonSerializer.Deserialize<WireMessage>(payload, JsonOptions)
               ?? throw new InvalidDataException("无法读取控制消息。");
    }

    public static List<TransferFile> BuildManifest(IEnumerable<SelectedItem> items, HashSet<string>? sessionRoots = null)
    {
        var result = new List<TransferFile>();
        var usedRoots = sessionRoots ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (SelectedItem item in items)
        {
            if (File.Exists(item.FullPath))
            {
                string root = MakeUniqueRoot(Path.GetFileName(item.FullPath), usedRoots);
                var info = new FileInfo(item.FullPath);
                result.Add(new TransferFile(info.FullName, root, info.Length, info.LastWriteTimeUtc.Ticks));
                continue;
            }

            if (!Directory.Exists(item.FullPath)) continue;
            string baseName = MakeUniqueRoot(new DirectoryInfo(item.FullPath).Name, usedRoots);
            foreach (string file in Directory.EnumerateFiles(item.FullPath, "*", SearchOption.AllDirectories))
            {
                var info = new FileInfo(file);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                string relative = Path.Combine(baseName, Path.GetRelativePath(item.FullPath, file));
                result.Add(new TransferFile(file, relative, info.Length, info.LastWriteTimeUtc.Ticks));
            }
        }
        return result;
    }

    private static string MakeUniqueRoot(string requested, HashSet<string> used)
    {
        string safe = string.Concat(requested.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        if (string.IsNullOrWhiteSpace(safe)) safe = "文件";
        string value = safe;
        int suffix = 2;
        while (!used.Add(value)) value = $"{safe} ({suffix++})";
        return value;
    }

    public static IEnumerable<string> GetLocalIPv4Addresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                        n.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
            .Select(a => a.Address.ToString())
            .Distinct();

    public static async Task<string?> DiscoverAsync(CancellationToken token)
    {
        byte[] query = Encoding.UTF8.GetBytes(DiscoveryQuery);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        var clients = new List<UdpClient>();
        try
        {
            foreach ((IPAddress address, IPAddress mask) in GetActiveIPv4Bindings())
            {
                try
                {
                    var udp = new UdpClient(new IPEndPoint(address, 0)) { EnableBroadcast = true };
                    clients.Add(udp);
                    await udp.SendAsync(query, new IPEndPoint(GetBroadcastAddress(address, mask), DiscoveryPort), timeout.Token);
                }
                catch (SocketException) { }
            }

            if (clients.Count == 0)
            {
                var fallback = new UdpClient(0) { EnableBroadcast = true };
                clients.Add(fallback);
                await fallback.SendAsync(query, new IPEndPoint(IPAddress.Broadcast, DiscoveryPort), timeout.Token);
            }

            var pending = clients.Select(c => ReceiveDiscoveryAsync(c, timeout.Token)).ToList();
            while (pending.Count > 0)
            {
                Task<string?> finished = await Task.WhenAny(pending);
                pending.Remove(finished);
                string? found = await finished;
                if (found is not null) return found;
            }
            return null;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            foreach (UdpClient client in clients) client.Dispose();
        }
    }

    private static async Task<string?> ReceiveDiscoveryAsync(UdpClient udp, CancellationToken token)
    {
        try
        {
            UdpReceiveResult result = await udp.ReceiveAsync(token);
            DiscoveryPacket? packet = JsonSerializer.Deserialize<DiscoveryPacket>(result.Buffer, JsonOptions);
            return packet?.App == "LanDrop" ? result.RemoteEndPoint.Address.ToString() : null;
        }
        catch (SocketException)
        {
            // Windows 会把某个网卡收到的 ICMP“端口不可达”转换成 UDP
            // WSAECONNRESET。多网卡搜索时应忽略该网卡，继续等待其他网卡响应。
            return null;
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }

    private static IEnumerable<(IPAddress Address, IPAddress Mask)> GetActiveIPv4Bindings() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                        n.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork &&
                        a.IPv4Mask is not null && !IPAddress.IsLoopback(a.Address))
            .Select(a => (a.Address, a.IPv4Mask));

    private static IPAddress GetBroadcastAddress(IPAddress address, IPAddress mask)
    {
        byte[] ipBytes = address.GetAddressBytes();
        byte[] maskBytes = mask.GetAddressBytes();
        byte[] broadcast = new byte[4];
        for (int i = 0; i < 4; i++) broadcast[i] = (byte)(ipBytes[i] | ~maskBytes[i]);
        return new IPAddress(broadcast);
    }

    public static async Task RunDiscoveryResponderAsync(CancellationToken token)
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, DiscoveryPort));
        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try { result = await udp.ReceiveAsync(token); }
            catch (OperationCanceledException) { break; }
            if (Encoding.UTF8.GetString(result.Buffer) != DiscoveryQuery) continue;
            byte[] response = JsonSerializer.SerializeToUtf8Bytes(
                new DiscoveryPacket { Port = TransferPort }, JsonOptions);
            await udp.SendAsync(response, result.RemoteEndPoint, token);
        }
    }

    public static async Task<string> HashFileAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(stream, token);
        return Convert.ToHexString(hash);
    }

    public static string SafeDestinationPath(string destinationRoot, string relativePath)
    {
        string root = Path.GetFullPath(destinationRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("发送端提供了不安全的文件路径。");
        return full;
    }
}
