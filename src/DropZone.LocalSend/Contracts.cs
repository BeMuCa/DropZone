using System.Text.Json;
using System.Text.Json.Serialization;

namespace DropZone.LocalSend;

public static class LocalSendJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

/// <summary>Protocol constants from the LocalSend v2 spec.</summary>
public static class LocalSendProtocol
{
    public const string MulticastAddress = "224.0.0.167";
    public const int DefaultPort = 53317;
    public const string Version = "2.0";

    public const string RegisterPath = "/api/localsend/v2/register";
    public const string PrepareUploadPath = "/api/localsend/v2/prepare-upload";
    public const string UploadPath = "/api/localsend/v2/upload";
    public const string CancelPath = "/api/localsend/v2/cancel";
}

public sealed class DeviceInfo
{
    public string Alias { get; set; } = "";
    public string Version { get; set; } = LocalSendProtocol.Version;
    public string? DeviceModel { get; set; }
    public string DeviceType { get; set; } = "desktop";
    public string Fingerprint { get; set; } = "";
    public int? Port { get; set; }
    public string? Protocol { get; set; }
    public bool Download { get; set; }

    /// <summary>Only present on multicast announcements; omitted from register bodies.</summary>
    public bool? Announce { get; set; }
}

public sealed class FileDto
{
    public string Id { get; set; } = "";
    public string FileName { get; set; } = "";
    public long Size { get; set; }
    public string? FileType { get; set; }
    public string? Sha256 { get; set; }
    public string? Preview { get; set; }
    public FileMetadata? Metadata { get; set; }
}

public sealed class FileMetadata
{
    public DateTime? Modified { get; set; }
    public DateTime? Accessed { get; set; }
}

public sealed class PrepareUploadRequest
{
    public DeviceInfo Info { get; set; } = new();
    public Dictionary<string, FileDto> Files { get; set; } = [];
}

public sealed class PrepareUploadResponse
{
    public string SessionId { get; set; } = "";
    public Dictionary<string, string> Files { get; set; } = [];
}
