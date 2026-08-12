using System.Text.Json.Serialization;

namespace LanDrop;

public sealed record SelectedItem(string FullPath, string DisplayName, string Kind);

public sealed record TransferFile(string SourcePath, string RelativePath, long Length, long LastWriteUtcTicks);

public sealed class WireMessage
{
    public int ProtocolVersion { get; set; }
    public string Type { get; set; } = "";
    public string? Code { get; set; }
    public string? Error { get; set; }
    public string? Path { get; set; }
    public string? Hash { get; set; }
    public long Length { get; set; }
    public long Offset { get; set; }
    public long LastWriteUtcTicks { get; set; }
    public long TotalBytes { get; set; }
    public int FileCount { get; set; }
}

public sealed class DiscoveryPacket
{
    public string App { get; set; } = "LanDrop";
    public string Name { get; set; } = Environment.MachineName;
    public int Port { get; set; }
}
