using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using SideBySide.Core.Localization;

namespace SideBySide.Modules.DirectConnect;

public enum TransferDirection
{
    Send,
    Receive,
}

public enum TransferState
{
    Waiting,
    Running,
    Completed,
    Failed,
    Rejected,
}

/// <summary>전송 한 건 (UI 표시용). 값은 작업자 스레드에서 바뀌므로 바인딩은 스칼라 속성만 쓴다.</summary>
public sealed class TransferItem(TransferDirection direction, string fileName, long totalBytes, string peerName) : INotifyPropertyChanged
{
    private long _transferred;
    private TransferState _state = TransferState.Waiting;
    private string? _error;
    private string? _savedPath;

    public event PropertyChangedEventHandler? PropertyChanged;

    public TransferDirection Direction { get; } = direction;

    public bool IsSend => Direction == TransferDirection.Send;

    public string FileName { get; } = fileName;

    public long TotalBytes { get; } = totalBytes;

    public string PeerName { get; } = peerName;

    public long Transferred
    {
        get => _transferred;
        set { _transferred = value; Notify(); Notify(nameof(Fraction)); }
    }

    public double Fraction => TotalBytes <= 0 ? (State == TransferState.Completed ? 1 : 0) : Math.Clamp(Transferred / (double)TotalBytes, 0, 1);

    public TransferState State
    {
        get => _state;
        set { _state = value; Notify(); Notify(nameof(Fraction)); Notify(nameof(StateText)); }
    }

    public string? Error
    {
        get => _error;
        set { _error = value; Notify(); Notify(nameof(StateText)); }
    }

    public string? SavedPath
    {
        get => _savedPath;
        set { _savedPath = value; Notify(); }
    }

    public string StateText => State switch
    {
        TransferState.Waiting => L.T("대기"),
        TransferState.Running => $"{Fraction:P0}",
        TransferState.Completed => L.T("완료"),
        TransferState.Rejected => L.T("거절함"),
        _ => Error is { Length: > 0 } e ? L.F("실패: {0}", e) : L.T("실패"),
    };

    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>받기 요청 (승인 판단용).</summary>
public sealed record IncomingRequest(IPAddress Address, string SenderName, string FileName, long FileSize, Peer? Peer);

/// <summary>
/// TCP 전송 (transfer_service.dart 이식, 와이어 호환). 받기 서버는 다음을 막는다:
/// 경로 조작 파일 이름, 거대 헤더, 음수·과대 크기, 디스크 부족, 공용 인터넷 주소, 과도한 동시 연결, 멈춘 연결.
/// 받은 파일은 .part로 받은 뒤 완료되면 이름을 바꾸고, 인터넷에서 받은 파일처럼 표시(Zone.Identifier)한다.
/// </summary>
public sealed class TransferService : IDisposable
{
    public const int MaxConnections = 32;
    public const int MaxConnectionsPerAddress = 4;
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);
    private const int BufferSize = 1 << 20;
    private const long DiskReserve = 64L << 20;

    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, int> _perAddress = new();
    private readonly ConcurrentDictionary<string, OutgoingConnection> _outgoing = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private TcpListener? _listener;
    private int _connections;

    public TransferService(int preferredPort = Protocol.TransferPort, bool allowLoopback = false)
    {
        PreferredPort = preferredPort;
        AllowLoopback = allowLoopback;
    }

    public int PreferredPort { get; }

    public bool AllowLoopback { get; }

    /// <summary>실제로 연 받기 포트 (58733이 쓰이고 있으면 임의 포트).</summary>
    public int Port { get; private set; }

    public string SaveFolder { get; set; } = DefaultSaveFolder();

    public string SelfName { get; set; } = Environment.MachineName;

    /// <summary>받을지 묻는다. true면 받기. 같은 연결의 다음 파일은 짧은 시간 안이면 다시 묻지 않는다.</summary>
    public Func<IncomingRequest, Task<bool>>? Approve { get; set; }

    /// <summary>보낸 기기의 탐색 정보 (신뢰한 기기 판단용).</summary>
    public Func<IPAddress, Peer?>? ResolvePeer { get; set; }

    /// <summary>새 전송이 시작됐다 (작업자 스레드).</summary>
    public event EventHandler<TransferItem>? TransferStarted;

    public bool IsListening => _listener is not null;

    /// <summary>Windows 기본 다운로드 폴더 아래 WifiDirectShare (독립 앱과 같은 위치).</summary>
    public static string DefaultSaveFolder()
    {
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        return Path.Combine(downloads, "WifiDirectShare");
    }

    // ── 받기 ────────────────────────────────────────────────────

    public void StartListening()
    {
        if (_listener is not null) return;
        var listener = new TcpListener(IPAddress.Any, PreferredPort);
        try
        {
            listener.Start();
        }
        catch (SocketException)
        {
            listener = new TcpListener(IPAddress.Any, 0); // 같은 PC의 독립 앱 등이 쓰고 있으면 임의 포트 (퐁으로 알림)
            listener.Start();
        }

        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = AcceptLoopAsync(listener, _cts.Token);
    }

    public void StopListening()
    {
        _listener?.Stop();
        _listener = null;
        Port = 0;
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException or InvalidOperationException)
            {
                return;
            }

            var remote = ((IPEndPoint)client.Client.RemoteEndPoint!).Address;
            if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
            var key = remote.ToString();
            if (!Protocol.IsLocalNetwork(remote, AllowLoopback)
                || Interlocked.Increment(ref _connections) > MaxConnections)
            {
                Interlocked.Decrement(ref _connections);
                client.Dispose();
                continue;
            }

            if (_perAddress.AddOrUpdate(key, 1, (_, n) => n + 1) > MaxConnectionsPerAddress)
            {
                Release(key);
                client.Dispose();
                continue;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await HandleConnectionAsync(client, remote, token);
                }
                finally
                {
                    Release(key);
                    client.Dispose();
                }
            }, token);
        }
    }

    private void Release(string key)
    {
        Interlocked.Decrement(ref _connections);
        _perAddress.AddOrUpdate(key, 0, (_, n) => Math.Max(0, n - 1));
    }

    /// <summary>한 연결에서 파일을 계속 받는다 (보낸 쪽이 연결을 닫을 때까지).</summary>
    private async Task HandleConnectionAsync(TcpClient client, IPAddress remote, CancellationToken token)
    {
        client.NoDelay = true;
        var stream = client.GetStream();
        var approvedUntil = DateTime.MinValue;
        var lengthBuffer = new byte[4];
        while (!token.IsCancellationRequested)
        {
            // 파일 사이: 미리 열어 둔(워밍업) 연결도 있으므로 넉넉히 기다린다
            if (!await ReadExactAsync(stream, lengthBuffer, IdleTimeout, token)) return;
            var length = BinaryPrimitives.ReadUInt32BigEndian(lengthBuffer);
            if (length is 0 or > Protocol.MaxHeaderLength) return;

            var headerBytes = new byte[length];
            if (!await ReadExactAsync(stream, headerBytes, StallTimeout, token)) return;
            if (Protocol.ParseHeader(headerBytes) is not { } header) return;

            var peer = ResolvePeer?.Invoke(remote);
            var senderName = string.IsNullOrWhiteSpace(header.SenderName) ? remote.ToString() : header.SenderName.Trim();
            if (senderName.Length > 64) senderName = senderName[..64];
            var fileName = Protocol.SanitizeFileName(header.FileName);
            var item = new TransferItem(TransferDirection.Receive, fileName, header.FileSize, peer?.Name ?? senderName);
            TransferStarted?.Invoke(this, item);

            // 승인: 한 번 받으면 같은 연결로 이어지는 파일은 2분 동안 다시 묻지 않는다 (여러 장을 한 번에 보낼 때)
            if (DateTime.UtcNow > approvedUntil)
            {
                var ok = Approve is null || await Approve(new IncomingRequest(remote, senderName, fileName, header.FileSize, peer));
                if (!ok)
                {
                    item.State = TransferState.Rejected;
                    return; // 거절 = 연결 종료 (원본 송신 앱은 실패로 처리)
                }
            }

            approvedUntil = DateTime.UtcNow.AddMinutes(2);
            if (!await ReceiveBodyAsync(stream, header.FileSize, fileName, item, token)) return;

            try
            {
                await stream.WriteAsync(new[] { Protocol.Ack }, token);
                await stream.FlushAsync(token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
        }
    }

    private async Task<bool> ReceiveBodyAsync(NetworkStream stream, long size, string fileName, TransferItem item, CancellationToken token)
    {
        string folder;
        try
        {
            folder = Path.GetFullPath(SaveFolder);
            Directory.CreateDirectory(folder);
            var free = new DriveInfo(Path.GetPathRoot(folder)!).AvailableFreeSpace;
            if (size > free - DiskReserve) throw new IOException(L.T("디스크 공간이 부족합니다."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Fail(item, ex.Message);
            return false;
        }

        var part = Path.Combine(folder, fileName + "." + Guid.NewGuid().ToString("N")[..8] + ".part");
        if (!Protocol.IsInside(folder, part))
        {
            Fail(item, L.T("파일 이름이 올바르지 않습니다."));
            return false;
        }

        item.State = TransferState.Running;
        var buffer = new byte[(int)Math.Min(BufferSize, Math.Max(4096, size))];
        try
        {
            await using (var output = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
            {
                long received = 0;
                long lastReport = 0;
                while (received < size)
                {
                    var want = (int)Math.Min(buffer.Length, size - received);
                    var read = await ReadSomeAsync(stream, buffer.AsMemory(0, want), StallTimeout, token);
                    if (read == 0) throw new IOException(L.T("전송이 중간에 끊겼습니다."));
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                    received += read;
                    if (received - lastReport >= (1 << 20) || received == size)
                    {
                        item.Transferred = received;
                        lastReport = received;
                    }
                }
            }

            // 완료: 겹치지 않는 최종 이름으로 바꾸고, 인터넷에서 받은 파일로 표시 (실행 파일이면 SmartScreen 경고)
            var final = Protocol.UniquePath(folder, fileName);
            if (!Protocol.IsInside(folder, final)) throw new IOException(L.T("파일 이름이 올바르지 않습니다."));
            File.Move(part, final);
            MarkFromNetwork(final);
            item.Transferred = size;
            item.SavedPath = final;
            item.State = TransferState.Completed;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or TimeoutException)
        {
            TryDelete(part);
            Fail(item, ex is OperationCanceledException ? L.T("취소했습니다.") : ex.Message);
            return false;
        }
    }

    private static void MarkFromNetwork(string path)
    {
        try
        {
            File.WriteAllText(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // FAT 드라이브 등 대체 스트림을 지원하지 않는 곳
        }
    }

    // ── 보내기 ──────────────────────────────────────────────────

    /// <summary>파일들을 기기에 차례로 보낸다 (연결 재사용, 실패하면 새 연결로 1회 재시도 — 원본과 같음).</summary>
    public async Task SendFilesAsync(Peer peer, IReadOnlyList<string> files, Action<TransferItem>? onCreated, CancellationToken cancellationToken)
    {
        var items = files.Where(File.Exists).Select(f => (Path: f, Item: new TransferItem(TransferDirection.Send, Path.GetFileName(f), SafeLength(f), peer.Name))).ToList();
        foreach (var (_, item) in items)
        {
            onCreated?.Invoke(item);
            TransferStarted?.Invoke(this, item);
        }

        // 한 기기 연결에 여러 파일을 섞어 쓰지 않게 보내기는 한 번에 하나씩
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            foreach (var (path, item) in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await SendOneAsync(peer, path, item, cancellationToken);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task SendOneAsync(Peer peer, string path, TransferItem item, CancellationToken token)
    {
        var key = $"{peer.Address}:{peer.TcpPort}";
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            OutgoingConnection? connection = null;
            try
            {
                connection = await ConnectionForAsync(key, peer, token);
                await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var size = file.Length;
                var stream = connection.Stream;
                await stream.WriteAsync(Protocol.EncodeHeader(Path.GetFileName(path), size, SelfName), token);

                item.State = TransferState.Running;
                var buffer = new byte[(int)Math.Min(BufferSize, Math.Max(4096, size))];
                long sent = 0, lastReport = 0;
                int read;
                while ((read = await file.ReadAsync(buffer, token)) > 0)
                {
                    await stream.WriteAsync(buffer.AsMemory(0, read), token);
                    sent += read;
                    if (sent - lastReport >= (1 << 20) || sent == size)
                    {
                        item.Transferred = sent;
                        lastReport = sent;
                    }
                }

                await stream.FlushAsync(token);

                // 받는 쪽의 1바이트 ACK: 연결이 건강한지 확인한 뒤 다음 파일에 재사용
                var ack = new byte[1];
                if (!await ReadExactAsync(stream, ack, Protocol.AckTimeout, token) || ack[0] != Protocol.Ack)
                    throw new IOException(L.T("받는 기기가 응답하지 않았습니다."));

                item.Transferred = size;
                item.State = TransferState.Completed;
                return;
            }
            catch (Exception ex) when (ex is IOException or SocketException or TimeoutException or ObjectDisposedException or UnauthorizedAccessException)
            {
                if (connection is not null && _outgoing.TryRemove(new KeyValuePair<string, OutgoingConnection>(key, connection))) connection.Dispose();
                if (attempt < 2 && ex is not UnauthorizedAccessException) continue; // 끊긴 연결을 재사용했을 수 있다 → 새 연결로 한 번 더
                Fail(item, ex.Message);
                return;
            }
        }
    }

    private async Task<OutgoingConnection> ConnectionForAsync(string key, Peer peer, CancellationToken token)
    {
        if (_outgoing.TryGetValue(key, out var existing) && existing.Client.Connected) return existing;
        if (existing is not null && _outgoing.TryRemove(new KeyValuePair<string, OutgoingConnection>(key, existing))) existing.Dispose();

        var client = new TcpClient { NoDelay = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(6));
        try
        {
            await client.ConnectAsync(peer.Address, peer.TcpPort, timeout.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            client.Dispose();
            throw new TimeoutException(L.T("기기에 연결하지 못했습니다."));
        }
        catch
        {
            client.Dispose();
            throw;
        }

        var connection = new OutgoingConnection(client);
        _outgoing[key] = connection;
        return connection;
    }

    // ── 공통 ────────────────────────────────────────────────────

    /// <summary>정확히 버퍼만큼 읽는다. 상대가 닫았거나(파일 사이의 정상 종료 포함) 시간이 지나면 false.</summary>
    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, TimeSpan timeout, CancellationToken token)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            int read;
            try
            {
                read = await ReadSomeAsync(stream, buffer.AsMemory(offset), timeout, token);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or ObjectDisposedException)
            {
                return false;
            }

            if (read == 0) return false;
            offset += read;
        }

        return true;
    }

    private static async Task<int> ReadSomeAsync(Stream stream, Memory<byte> buffer, TimeSpan timeout, CancellationToken token)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(timeout);
        try
        {
            return await stream.ReadAsync(buffer, cts.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException(L.T("응답이 없어 연결을 끊었습니다."));
        }
    }

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (IOException) { return 0; }
    }

    private static void Fail(TransferItem item, string message)
    {
        item.Error = message;
        item.State = TransferState.Failed;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        StopListening();
        foreach (var connection in _outgoing.Values) connection.Dispose();
        _outgoing.Clear();
        TransferStarted = null;
    }

    private sealed class OutgoingConnection(TcpClient client) : IDisposable
    {
        public TcpClient Client { get; } = client;

        public NetworkStream Stream { get; } = client.GetStream();

        public void Dispose() => Client.Dispose();
    }
}
