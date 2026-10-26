using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Principal;
using Microsoft.Win32;
using SideBySide.Core.Localization;

namespace SideBySide.Modules.LanShare;

/// <summary>공유 권한. 원본 도구의 FULL(권한 변경까지 허용)은 두지 않는다.</summary>
public enum SharePermission
{
    Read,
    Change,
}

/// <summary>실행할 명령 한 단계. 셸을 거치지 않고 System32의 exe를 인자 목록으로 직접 실행한다.</summary>
public sealed record CommandStep(string Description, string Program, IReadOnlyList<string> Arguments, bool IgnoreFailure = false);

/// <summary>이더넷 어댑터 정보.</summary>
public sealed record AdapterInfo(string Id, string Name, string Description, int IPv4Index, bool IsUp, IReadOnlyList<string> Addresses)
{
    /// <summary>LAN 공유 역할로 설정돼 있으면 1/2.</summary>
    public int? Role => Addresses.Contains(LanShareCore.Pc1Ip) ? 1 : Addresses.Contains(LanShareCore.Pc2Ip) ? 2 : null;

    public string Display => Role is { } r ? L.F("{0} (PC{1}로 설정됨)", Name, r) : Name;

    public override string ToString() => Display;
}

/// <summary>되돌릴 때 쓰는 이전 IP 설정 (적용 직전에 일반 권한으로 읽어 둔다).</summary>
public sealed class SavedIpConfig
{
    public bool Dhcp { get; set; } = true;

    public string? Address { get; set; }

    public string? Mask { get; set; }

    public string? Gateway { get; set; }

    public List<string> Dns { get; set; } = [];

    public bool DnsDhcp { get; set; } = true;
}

/// <summary>
/// lan_share_setup 이식 (Windows). 독립 앱과 같은 IP·서브넷·DNS·공유 이름·기본 폴더·설명을 써서
/// 한쪽 PC는 독립 앱, 다른 쪽은 SideBySide로 설정해도 서로 연결된다.
/// </summary>
public static class LanShareCore
{
    public const string Pc1Ip = "192.168.1.1";
    public const string Pc2Ip = "192.168.1.2";
    public const string SubnetMask = "255.255.255.0";
    public const string Dns = "8.8.8.8";
    public const string ShareName = "LAN_Share";
    public const string DefaultFolder = @"C:\LAN_Share";
    public const string Remark = "LAN Direct Share";

    /// <summary>"파일 및 프린터 공유" 규칙 그룹 (언어와 무관한 간접 이름 — 원본의 영어 이름은 한국어 Windows에서 실패).</summary>
    public const string FileSharingGroup = "@FirewallAPI.dll,-28502";

    /// <summary>"네트워크 검색" 규칙 그룹.</summary>
    public const string NetworkDiscoveryGroup = "@FirewallAPI.dll,-32752";

    private static readonly string[] SkipWords = ["wi-fi", "wifi", "wireless", "loopback", "bluetooth", "vethernet", "vpn", "tunnel", "virtual", "hyper-v", "wan miniport"];

    public static string IpFor(int role) => role == 1 ? Pc1Ip : Pc2Ip;

    public static string PeerIpFor(int role) => role == 1 ? Pc2Ip : Pc1Ip;

    public static string PeerSharePath(int role) => $@"\\{PeerIpFor(role)}\{ShareName}";

    /// <summary>유선 이더넷 어댑터 (원본과 같은 제외 규칙: Wi-Fi·루프백·블루투스·vEthernet·VPN·터널).</summary>
    public static List<AdapterInfo> Adapters()
    {
        var result = new List<AdapterInfo>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit))
                continue;
            var text = (nic.Name + " " + nic.Description).ToLowerInvariant();
            if (SkipWords.Any(text.Contains)) continue;

            int index;
            try
            {
                index = nic.GetIPProperties().GetIPv4Properties()?.Index ?? -1;
            }
            catch (NetworkInformationException)
            {
                continue;
            }

            if (index < 0) continue;
            var addresses = nic.GetIPProperties().UnicastAddresses
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.Address.ToString())
                .ToList();
            result.Add(new AdapterInfo(nic.Id, nic.Name, nic.Description, index, nic.OperationalStatus == OperationalStatus.Up, addresses));
        }

        return result.OrderByDescending(a => a.IsUp).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>현재 IP 설정 (되돌리기용).</summary>
    public static SavedIpConfig ReadIpConfig(string adapterId)
    {
        var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Id == adapterId);
        var config = new SavedIpConfig();
        if (nic is null) return config;
        var props = nic.GetIPProperties();
        var v4 = props.GetIPv4Properties();
        config.Dhcp = v4?.IsDhcpEnabled ?? true;
        var unicast = props.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
        config.Address = unicast?.Address.ToString();
        config.Mask = unicast?.IPv4Mask?.ToString();
        config.Gateway = props.GatewayAddresses.FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString();
        config.Dns = props.DnsAddresses.Where(d => d.AddressFamily == AddressFamily.InterNetwork).Select(d => d.ToString()).Take(2).ToList();

        // DNS가 DHCP에서 왔는지는 API로 알 수 없어 IP와 같게 본다
        config.DnsDhcp = config.Dhcp;
        return config;
    }

    /// <summary>LAN_Share 공유 경로 (없으면 null). 공유 설정은 레지스트리에서 읽어 관리자 권한이 필요 없다.</summary>
    public static string? ExistingSharePath()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\LanmanServer\Shares");
            if (key?.GetValue(ShareName) is not string[] values) return null;
            return values.FirstOrDefault(v => v.StartsWith("Path=", StringComparison.OrdinalIgnoreCase))?["Path=".Length..];
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>모든 사용자 그룹(Everyone)의 현재 언어 이름 (독일어 Windows의 "Jeder" 등).</summary>
    public static string EveryoneName()
    {
        try
        {
            return new SecurityIdentifier(WellKnownSidType.WorldSid, null).Translate(typeof(NTAccount)).Value;
        }
        catch (Exception ex) when (ex is IdentityNotMappedException or SystemException)
        {
            return "Everyone";
        }
    }

    /// <summary>공유해도 되는 폴더인지: 고정 드라이브의 절대 경로이고, 드라이브 루트·Windows·Program Files·ProgramData·사용자 폴더 루트가 아님.</summary>
    public static string? ValidateShareFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return L.T("공유할 폴더를 지정하세요.");
        string full;
        try
        {
            full = Path.GetFullPath(folder).TrimEnd('\\');
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return L.T("폴더 경로가 올바르지 않습니다.");
        }

        if (!Path.IsPathFullyQualified(folder) || full.StartsWith(@"\\", StringComparison.Ordinal)) return L.T("이 PC의 로컬 폴더만 공유할 수 있습니다.");
        var root = Path.GetPathRoot(full)!;
        try
        {
            if (new DriveInfo(root).DriveType != DriveType.Fixed) return L.T("고정 드라이브의 폴더만 공유할 수 있습니다.");
        }
        catch (ArgumentException)
        {
            return L.T("폴더 경로가 올바르지 않습니다.");
        }

        if (full.Length <= 3) return L.T("드라이브 전체는 공유할 수 없습니다. 하위 폴더를 고르세요.");
        string[] protectedRoots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        ];
        foreach (var p in protectedRoots.Where(p => p.Length > 0))
        {
            if (full.Equals(p, StringComparison.OrdinalIgnoreCase) || full.StartsWith(p + "\\", StringComparison.OrdinalIgnoreCase))
                return L.T("시스템 폴더는 공유할 수 없습니다.");
        }

        var users = Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (users is not null && (full.Equals(users, StringComparison.OrdinalIgnoreCase) || string.Equals(Path.GetDirectoryName(full), users, StringComparison.OrdinalIgnoreCase)))
            return L.T("사용자 폴더 전체는 공유할 수 없습니다. 하위 폴더를 고르세요.");

        return null;
    }

    private static string Netsh => Path.Combine(Environment.SystemDirectory, "netsh.exe");

    private static string Net => Path.Combine(Environment.SystemDirectory, "net.exe");

    /// <summary>적용 명령 (독립 앱의 win_apply_cmds와 같은 결과). 어댑터는 이름 대신 인덱스로 지정해 공백·특수문자 문제가 없다.</summary>
    public static List<CommandStep> BuildApply(int adapterIndex, int role, string folder, SharePermission permission, string everyone, bool shareExists)
    {
        var ip = IpFor(role);
        var steps = new List<CommandStep>
        {
            new(L.F("고정 IP {0} / {1}", ip, SubnetMask), Netsh, ["interface", "ipv4", "set", "address", $"name={adapterIndex}", "source=static", $"address={ip}", $"mask={SubnetMask}"]),
            new(L.F("DNS {0}", Dns), Netsh, ["interface", "ipv4", "set", "dnsservers", $"name={adapterIndex}", "source=static", $"address={Dns}", "register=primary", "validate=no"]),
            new(L.T("방화벽: 파일 및 프린터 공유 허용"), Netsh, ["advfirewall", "firewall", "set", "rule", $"group={FileSharingGroup}", "new", "enable=Yes"]),
            new(L.T("방화벽: 네트워크 검색 허용"), Netsh, ["advfirewall", "firewall", "set", "rule", $"group={NetworkDiscoveryGroup}", "new", "enable=Yes"]),
        };

        // 이미 같은 이름으로 공유돼 있으면(독립 앱이 만든 경우 포함) 권한은 건드리지 않는다 (원본도 '이미 공유됨'으로 넘어감)
        if (!shareExists)
        {
            var grant = permission == SharePermission.Read ? "READ" : "CHANGE";
            steps.Add(new(L.F("공유 폴더 {0} → \\\\{1}\\{2}", folder, ip, ShareName), Net,
                ["share", $"{ShareName}={folder}", $"/GRANT:{everyone},{grant}", $"/REMARK:{Remark}"]));
        }

        return steps;
    }

    /// <summary>해제 명령 (독립 앱의 win_reset_cmds와 같은 범위). 저장한 이전 설정이 있으면 그것으로, 없으면 DHCP로.</summary>
    public static List<CommandStep> BuildReset(int adapterIndex, SavedIpConfig? previous)
    {
        var steps = new List<CommandStep>();
        if (previous is { Dhcp: false, Address: { } address, Mask: { } mask })
        {
            var args = new List<string> { "interface", "ipv4", "set", "address", $"name={adapterIndex}", "source=static", $"address={address}", $"mask={mask}" };
            if (previous.Gateway is { } gateway) args.Add($"gateway={gateway}");
            steps.Add(new(L.F("이전 IP {0}로 복원", address), Netsh, args));
        }
        else
        {
            steps.Add(new(L.T("IP 자동 받기 (DHCP)"), Netsh, ["interface", "ipv4", "set", "address", $"name={adapterIndex}", "source=dhcp"]));
        }

        if (previous is { DnsDhcp: false, Dns.Count: > 0 })
        {
            steps.Add(new(L.F("이전 DNS {0}로 복원", previous.Dns[0]), Netsh, ["interface", "ipv4", "set", "dnsservers", $"name={adapterIndex}", "source=static", $"address={previous.Dns[0]}", "register=primary", "validate=no"]));
            if (previous.Dns.Count > 1)
                steps.Add(new(L.F("보조 DNS {0}", previous.Dns[1]), Netsh, ["interface", "ipv4", "add", "dnsservers", $"name={adapterIndex}", $"address={previous.Dns[1]}", "index=2", "validate=no"], IgnoreFailure: true));
        }
        else
        {
            steps.Add(new(L.T("DNS 자동 받기 (DHCP)"), Netsh, ["interface", "ipv4", "set", "dnsservers", $"name={adapterIndex}", "source=dhcp"]));
        }

        steps.Add(new(L.F("공유 해제 ({0})", ShareName), Net, ["share", ShareName, "/DELETE", "/Y"], IgnoreFailure: true));
        steps.Add(new(L.T("방화벽: 네트워크 검색 끄기"), Netsh, ["advfirewall", "firewall", "set", "rule", $"group={NetworkDiscoveryGroup}", "new", "enable=No"]));
        return steps;
    }

    /// <summary>IPv4 주소 문자열 검사 (점 네 개, 0..255).</summary>
    public static bool IsIPv4(string? text)
        => text is not null && text.Count(c => c == '.') == 3 && IPAddress.TryParse(text, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork;
}
