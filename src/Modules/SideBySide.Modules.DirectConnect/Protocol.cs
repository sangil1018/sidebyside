using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SideBySide.Modules.DirectConnect;

/// <summary>
/// AgentZi Direct Connect(wifi-direct Flutter 앱)와 같은 와이어 형식. 값을 바꾸면 Android/Windows 독립 앱과 통신할 수 없다.
/// <list type="bullet">
/// <item>탐색: UDP 58732, UTF-8 JSON <c>{"type":"ping","id","name","platform"}</c> 2초마다 브로드캐스트, 받기 켜진 기기만 보낸 주소·포트로 <c>{"type":"pong",...,"tcpPort"}</c>. 8초간 응답 없으면 사라짐.</item>
/// <item>전송: TCP(기본 58733). 파일마다 4바이트 빅엔디언 헤더 길이 + JSON <c>{"fileName","fileSize","senderName"}</c> + 원본 바이트, 받는 쪽이 1바이트 <c>0x01</c> ACK. 연결은 여러 파일에 재사용.</item>
/// </list>
/// </summary>
public static class Protocol
{
    public const int DiscoveryPort = 58732;
    public const int TransferPort = 58733;
    public static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan PeerTimeout = TimeSpan.FromSeconds(8);
    public static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(15);
    public const byte Ack = 1;

    /// <summary>헤더 최대 크기 (정상 헤더는 수백 바이트). 큰 값으로 메모리를 소모시키는 공격 차단.</summary>
    public const int MaxHeaderLength = 64 * 1024;

    /// <summary>UDP 메시지 최대 크기.</summary>
    public const int MaxDatagram = 4096;

    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public sealed class DiscoveryMessage
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("platform")]
        public string? Platform { get; set; }

        [JsonPropertyName("tcpPort")]
        public int? TcpPort { get; set; }
    }

    public sealed class FileHeader
    {
        [JsonPropertyName("fileName")]
        public string? FileName { get; set; }

        [JsonPropertyName("fileSize")]
        public long FileSize { get; set; }

        [JsonPropertyName("senderName")]
        public string? SenderName { get; set; }
    }

    public static byte[] Ping(string id, string name) => JsonSerializer.SerializeToUtf8Bytes(new DiscoveryMessage { Type = "ping", Id = id, Name = name, Platform = "windows" }, Json);

    public static byte[] Pong(string id, string name, int tcpPort) => JsonSerializer.SerializeToUtf8Bytes(new DiscoveryMessage { Type = "pong", Id = id, Name = name, Platform = "windows", TcpPort = tcpPort }, Json);

    public static DiscoveryMessage? ParseDiscovery(ReadOnlySpan<byte> data)
    {
        if (data.Length is 0 or > MaxDatagram) return null;
        try
        {
            return JsonSerializer.Deserialize<DiscoveryMessage>(data, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>헤더 프레임 (길이 4바이트 + JSON).</summary>
    public static byte[] EncodeHeader(string fileName, long fileSize, string senderName)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new FileHeader { FileName = fileName, FileSize = fileSize, SenderName = senderName }, Json);
        var frame = new byte[4 + json.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)json.Length);
        json.CopyTo(frame, 4);
        return frame;
    }

    public static FileHeader? ParseHeader(ReadOnlySpan<byte> json)
    {
        try
        {
            var header = JsonSerializer.Deserialize<FileHeader>(json, Json);
            return header is { FileName.Length: > 0, FileSize: >= 0 } ? header : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>같은 네트워크(사설·링크 로컬) 주소만 허용. 인터넷 주소에서 오는 탐색·전송은 받지 않는다.</summary>
    public static bool IsLocalNetwork(IPAddress address, bool allowLoopback)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        if (IPAddress.IsLoopback(address)) return allowLoopback;
        var b = address.GetAddressBytes();
        return b[0] == 10
               || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
               || (b[0] == 192 && b[1] == 168)
               || (b[0] == 169 && b[1] == 254);
    }

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// 보낸 쪽이 준 파일 이름을 저장해도 안전한 이름으로: 경로 부분·제어 문자·금지 문자·ADS(:) 제거, 끝의 점/공백 제거,
    /// 예약 장치 이름 회피, 길이 제한. 정상적인 파일 이름(Android가 보내는 이름)은 바뀌지 않는다.
    /// </summary>
    public static string SanitizeFileName(string? name)
    {
        name ??= string.Empty;
        var last = name.Replace('\\', '/');
        var slash = last.LastIndexOf('/');
        if (slash >= 0) last = last[(slash + 1)..];

        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(last.Length);
        foreach (var c in last)
        {
            if (c < 32 || c == ':' || Array.IndexOf(invalid, c) >= 0) sb.Append('_');
            else sb.Append(c);
        }

        var clean = sb.ToString().Trim().TrimEnd('.', ' ');
        if (clean.Length == 0 || clean is "." or "..") clean = "file";

        var stem = Path.GetFileNameWithoutExtension(clean);
        if (Reserved.Contains(stem) || Reserved.Contains(clean)) clean = "_" + clean;

        if (clean.Length > 180)
        {
            var ext = Path.GetExtension(clean);
            if (ext.Length > 20) ext = string.Empty;
            clean = clean[..(180 - ext.Length)] + ext;
        }

        return clean;
    }

    /// <summary>이름이 겹치면 원본 앱과 같은 <c>이름 (n).확장자</c>.</summary>
    public static string UniquePath(string folder, string fileName)
    {
        var candidate = Path.Combine(folder, fileName);
        if (!File.Exists(candidate)) return candidate;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 1; ; i++)
        {
            candidate = Path.Combine(folder, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    /// <summary>경로가 폴더 안에 있는지 (정규화 후 비교).</summary>
    public static bool IsInside(string folder, string path)
    {
        var root = Path.GetFullPath(folder).TrimEnd('\\') + "\\";
        return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}
