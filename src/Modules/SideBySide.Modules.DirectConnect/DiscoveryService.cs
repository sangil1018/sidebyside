using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace SideBySide.Modules.DirectConnect;

/// <summary>발견된 기기.</summary>
public sealed record Peer(string Id, string Name, string Platform, IPAddress Address, int TcpPort, DateTime LastSeenUtc)
{
    public bool IsPhone => Platform is "android" or "ios";
}

/// <summary>
/// UDP 탐색 (discovery_service.dart 이식, 와이어 호환).
/// 원본과 달리 소켓을 둘 쓴다: 58732 수신 소켓(다른 기기의 핑을 받아 퐁 응답)과, 핑을 보내고 퐁을 받는 임시 포트 소켓.
/// 같은 PC에서 독립 Windows 앱이 58732를 함께 쓰고 있어도(SO_REUSEADDR) 퐁이 엉뚱한 앱으로 가지 않는다.
/// </summary>
public sealed class DiscoveryService : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Peer> _peers = [];
    private readonly CancellationTokenSource _cts = new();
    private Socket? _listener;
    private Socket? _pinger;
    private List<IPAddress> _broadcasts = [];
    private DateTime _interfacesReadUtc;

    /// <param name="listen">false면 다른 기기의 핑을 받는 수신 소켓을 열지 않는다 (같은 포트를 쓰는 테스트용).</param>
    public DiscoveryService(string selfId, int discoveryPort = Protocol.DiscoveryPort, bool allowLoopback = false, bool listen = true)
    {
        SelfId = selfId;
        DiscoveryPort = discoveryPort;
        AllowLoopback = allowLoopback;
        _listen = listen;
    }

    private readonly bool _listen;

    public string SelfId { get; }

    public int DiscoveryPort { get; }

    public bool AllowLoopback { get; }

    /// <summary>핑·퐁에 실을 이 기기 이름.</summary>
    public string SelfName { get; set; } = Environment.MachineName;

    /// <summary>켜져 있을 때만 핑에 응답한다 (꺼져 있으면 다른 기기 목록에 보이지 않음 — 원본과 같음).</summary>
    public bool ReceivingEnabled { get; set; }

    /// <summary>퐁에 알릴 TCP 포트 (받기 서버 포트).</summary>
    public int TcpPort { get; set; }

    /// <summary>58732를 열지 못해 다른 기기의 핑을 받을 수 없는 상태.</summary>
    public bool ListenerFallback { get; private set; }

    /// <summary>기기 목록이 바뀌었다 (작업자 스레드).</summary>
    public event EventHandler? PeersChanged;

    public IReadOnlyList<Peer> Peers
    {
        get
        {
            lock (_gate) return _peers.Values.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
    }

    public Peer? FindById(string id)
    {
        lock (_gate) return _peers.GetValueOrDefault(id);
    }

    public Peer? FindByAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        lock (_gate) return _peers.Values.FirstOrDefault(p => p.Address.Equals(address));
    }

    public void Start()
    {
        if (_listen)
        {
            _listener = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listener.EnableBroadcast = true;
            try
            {
                _listener.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
            }
            catch (SocketException)
            {
                // 다른 프로그램이 배타적으로 쓰고 있다: 다른 기기의 핑은 못 받지만 우리 핑에 대한 퐁은 받는다 (원본과 같은 대체)
                _listener.Dispose();
                _listener = null;
                ListenerFallback = true;
            }
        }

        _pinger = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { EnableBroadcast = true };
        _pinger.Bind(new IPEndPoint(IPAddress.Any, 0));
        IgnoreConnectionReset(_listener);
        IgnoreConnectionReset(_pinger);

        var token = _cts.Token;
        if (_listener is not null) _ = ReceiveLoopAsync(_listener, token);
        _ = ReceiveLoopAsync(_pinger, token);
        _ = TickLoopAsync(token);
    }

    /// <summary>바로 한 번 핑 (새로 고침).</summary>
    public void RefreshNow()
    {
        _interfacesReadUtc = DateTime.MinValue;
        SendPing();
    }

    private async Task TickLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(Protocol.PingInterval);
        do
        {
            SendPing();
            Prune();
        }
        while (await WaitAsync(timer, token));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken token)
    {
        try
        {
            return await timer.WaitForNextTickAsync(token);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private void SendPing()
    {
        if (_pinger is not { } socket) return;
        var payload = Protocol.Ping(SelfId, SelfName);
        foreach (var address in BroadcastAddresses())
        {
            try
            {
                socket.SendTo(payload, new IPEndPoint(address, DiscoveryPort));
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                // 일부 네트워크는 전체 브로드캐스트를 막는다 → 서브넷 브로드캐스트가 대신 간다
            }
        }
    }

    /// <summary>255.255.255.255 + 각 IPv4 인터페이스의 /24 브로드캐스트 (원본과 같은 가정). Wi-Fi Direct·핫스팟 연결도 새로 읽는다.</summary>
    private List<IPAddress> BroadcastAddresses()
    {
        if (DateTime.UtcNow - _interfacesReadUtc < TimeSpan.FromSeconds(6)) return _broadcasts;
        var list = new List<IPAddress> { IPAddress.Broadcast };
        if (AllowLoopback) list.Add(IPAddress.Loopback);
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var a in nic.GetIPProperties().UnicastAddresses)
                {
                    if (a.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var b = a.Address.GetAddressBytes();
                    var broadcast = new IPAddress([b[0], b[1], b[2], 255]);
                    if (!list.Contains(broadcast)) list.Add(broadcast);
                }
            }
        }
        catch (NetworkInformationException)
        {
        }

        _interfacesReadUtc = DateTime.UtcNow;
        return _broadcasts = list;
    }

    private async Task ReceiveLoopAsync(Socket socket, CancellationToken token)
    {
        var buffer = new byte[Protocol.MaxDatagram + 1];
        while (!token.IsCancellationRequested)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                // Windows는 보낸 곳에서 ICMP 거부가 오면 다음 수신에서 오류를 낸다 → 무시하고 계속
                continue;
            }

            if (result.RemoteEndPoint is not IPEndPoint remote || !Protocol.IsLocalNetwork(remote.Address, AllowLoopback)) continue;
            if (result.ReceivedBytes > Protocol.MaxDatagram) continue;
            Handle(socket, remote, Protocol.ParseDiscovery(buffer.AsSpan(0, result.ReceivedBytes)));
        }
    }

    private void Handle(Socket socket, IPEndPoint remote, Protocol.DiscoveryMessage? message)
    {
        if (message?.Id is not { Length: > 0 and <= 64 } id || id == SelfId) return;

        if (message.Type == "ping")
        {
            if (!ReceivingEnabled || TcpPort <= 0) return; // 받기가 꺼져 있으면 보이지 않는다
            try
            {
                socket.SendTo(Protocol.Pong(SelfId, SelfName, TcpPort), remote);
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
            }
        }
        else if (message.Type == "pong" && message.TcpPort is > 0 and <= 65535)
        {
            var name = string.IsNullOrWhiteSpace(message.Name) ? "Unknown" : message.Name.Trim();
            if (name.Length > 64) name = name[..64];
            var platform = message.Platform is "windows" or "android" or "ios" ? message.Platform : "other";
            var peer = new Peer(id, name, platform, remote.Address.IsIPv4MappedToIPv6 ? remote.Address.MapToIPv4() : remote.Address, message.TcpPort.Value, DateTime.UtcNow);
            bool changed;
            lock (_gate)
            {
                changed = !_peers.TryGetValue(id, out var old) || old.Name != peer.Name || !old.Address.Equals(peer.Address) || old.TcpPort != peer.TcpPort;
                if (_peers.Count < 256 || _peers.ContainsKey(id)) _peers[id] = peer;
            }

            if (changed) PeersChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Prune()
    {
        bool removed;
        lock (_gate)
        {
            var stale = _peers.Values.Where(p => DateTime.UtcNow - p.LastSeenUtc > Protocol.PeerTimeout).Select(p => p.Id).ToList();
            foreach (var id in stale) _peers.Remove(id);
            removed = stale.Count > 0;
        }

        if (removed) PeersChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Windows UDP 소켓은 보낸 곳에서 ICMP '포트 닫힘'이 오면 다음 수신을 오류로 끝낸다 (SIO_UDP_CONNRESET). 탐색에는 의미가 없어 끈다.
    /// </summary>
    private static void IgnoreConnectionReset(Socket? socket)
    {
        const int SioUdpConnReset = -1744830452;
        if (socket is null || !OperatingSystem.IsWindows()) return;
        try
        {
            socket.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
        }
        catch (SocketException)
        {
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener?.Dispose();
        _pinger?.Dispose();
        lock (_gate) _peers.Clear();
        PeersChanged = null;
    }
}
