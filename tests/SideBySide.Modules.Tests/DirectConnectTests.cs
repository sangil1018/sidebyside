using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using SideBySide.Modules.DirectConnect;

namespace SideBySide.Modules.Tests;

/// <summary>
/// AgentZi Direct Connect(Flutter) 앱과 같은 바이트 형식으로 직접 말하는 테스트 피어로 호환성과 받기 서버 방어를 확인한다.
/// </summary>
public class DirectConnectTests
{
    private static TransferService Receiver(string folder, Func<IncomingRequest, Task<bool>>? approve = null)
    {
        var service = new TransferService(preferredPort: 0, allowLoopback: true) { SaveFolder = folder, Approve = approve };
        service.StartListening();
        return service;
    }

    /// <summary>Dart sendFile과 같은 프레임: 4바이트 BE 길이 + JSON 헤더 + 본문.</summary>
    private static async Task SendLikeDartAsync(NetworkStream stream, string name, byte[] body, string sender = "Galaxy")
    {
        var header = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Dictionary<string, object> { ["fileName"] = name, ["fileSize"] = body.Length, ["senderName"] = sender }));
        var len = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(len, (uint)header.Length);
        await stream.WriteAsync(len);
        await stream.WriteAsync(header);
        await stream.WriteAsync(body);
        await stream.FlushAsync();
    }

    private static async Task<int> ReadByteAsync(NetworkStream stream, int timeoutMs = 5000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        var buffer = new byte[1];
        try
        {
            var read = await stream.ReadAsync(buffer, cts.Token);
            return read == 0 ? -1 : buffer[0];
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            return -1;
        }
    }

    [Theory]
    [InlineData("photo.jpg", "photo.jpg")]
    [InlineData("사진 2024.png", "사진 2024.png")]
    [InlineData(@"..\..\Windows\evil.dll", "evil.dll")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("C:\\x\\a.txt", "a.txt")]
    [InlineData("file.txt:hidden", "file.txt_hidden")]
    [InlineData("CON", "_CON")]
    [InlineData("nul.txt", "_nul.txt")]
    [InlineData("..", "file")]
    [InlineData("name. . ", "name")]
    [InlineData("a\u0001b.txt", "a_b.txt")]
    public void Sanitizes_file_names(string input, string expected) => Assert.Equal(expected, Protocol.SanitizeFileName(input));

    [Theory]
    [InlineData("192.168.0.10", true)]
    [InlineData("192.168.49.1", true)]
    [InlineData("192.168.137.1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.20.10.2", true)]
    [InlineData("169.254.3.4", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("127.0.0.1", false)]
    public void Accepts_only_local_networks(string ip, bool ok) => Assert.Equal(ok, Protocol.IsLocalNetwork(IPAddress.Parse(ip), allowLoopback: false));

    [Fact]
    public void Header_and_discovery_json_match_flutter_field_names()
    {
        var frame = Protocol.EncodeHeader("a.txt", 12, "PC");
        var len = BinaryPrimitives.ReadUInt32BigEndian(frame);
        var json = JsonDocument.Parse(frame.AsMemory(4, (int)len)).RootElement;
        Assert.Equal("a.txt", json.GetProperty("fileName").GetString());
        Assert.Equal(12, json.GetProperty("fileSize").GetInt64());
        Assert.Equal("PC", json.GetProperty("senderName").GetString());

        var pong = JsonDocument.Parse(Protocol.Pong("abcd1234", "PC", 58733)).RootElement;
        Assert.Equal("pong", pong.GetProperty("type").GetString());
        Assert.Equal("windows", pong.GetProperty("platform").GetString());
        Assert.Equal(58733, pong.GetProperty("tcpPort").GetInt32());
        var ping = JsonDocument.Parse(Protocol.Ping("abcd1234", "PC")).RootElement;
        Assert.False(ping.TryGetProperty("tcpPort", out _));
    }

    [Fact]
    public async Task Receives_several_files_on_one_connection_with_ack_each()
    {
        using var d = new TempDir();
        using var receiver = Receiver(d.Path);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, receiver.Port);
        var stream = client.GetStream();

        // 워밍업 연결처럼 잠시 아무것도 보내지 않아도 끊기지 않는다
        await Task.Delay(500);
        await SendLikeDartAsync(stream, "one.txt", Encoding.UTF8.GetBytes("first"));
        Assert.Equal(Protocol.Ack, await ReadByteAsync(stream));
        await SendLikeDartAsync(stream, "one.txt", Encoding.UTF8.GetBytes("second"));
        Assert.Equal(Protocol.Ack, await ReadByteAsync(stream));
        await SendLikeDartAsync(stream, "empty.bin", []);
        Assert.Equal(Protocol.Ack, await ReadByteAsync(stream));

        Assert.Equal("first", File.ReadAllText(Path.Combine(d.Path, "one.txt")));
        Assert.Equal("second", File.ReadAllText(Path.Combine(d.Path, "one (1).txt")));
        Assert.Equal(0, new FileInfo(Path.Combine(d.Path, "empty.bin")).Length);
        Assert.Empty(Directory.GetFiles(d.Path, "*.part"));
        Assert.Contains("ZoneId=3", File.ReadAllText(Path.Combine(d.Path, "one.txt") + ":Zone.Identifier"));
    }

    [Fact]
    public async Task Path_traversal_name_is_saved_inside_folder()
    {
        using var d = new TempDir();
        var inbox = d.Dir("inbox");
        using var receiver = Receiver(inbox);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, receiver.Port);
        var stream = client.GetStream();

        await SendLikeDartAsync(stream, @"..\escaped.txt", Encoding.UTF8.GetBytes("x"));
        Assert.Equal(Protocol.Ack, await ReadByteAsync(stream));

        Assert.True(File.Exists(Path.Combine(inbox, "escaped.txt")));
        Assert.False(File.Exists(Path.Combine(d.Path, "escaped.txt")));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(70_000u)]
    [InlineData(uint.MaxValue)]
    public async Task Oversized_or_empty_header_closes_connection(uint headerLength)
    {
        using var d = new TempDir();
        using var receiver = Receiver(d.Path);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, receiver.Port);
        var stream = client.GetStream();
        var len = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(len, headerLength);
        await stream.WriteAsync(len);

        Assert.Equal(-1, await ReadByteAsync(stream));
        Assert.Empty(Directory.GetFiles(d.Path));
    }

    [Theory]
    [InlineData("""{"fileName":"a.txt","fileSize":-5}""")]
    [InlineData("""{"fileName":"","fileSize":1}""")]
    [InlineData("""{"fileSize":1}""")]
    [InlineData("not json")]
    [InlineData("""{"fileName":"big.bin","fileSize":9223372036854775807}""")]
    public async Task Bad_header_is_rejected(string json)
    {
        using var d = new TempDir();
        using var receiver = Receiver(d.Path);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, receiver.Port);
        var stream = client.GetStream();
        var header = Encoding.UTF8.GetBytes(json);
        var len = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(len, (uint)header.Length);
        await stream.WriteAsync(len);
        await stream.WriteAsync(header);

        Assert.Equal(-1, await ReadByteAsync(stream));
        Assert.Empty(Directory.GetFiles(d.Path)); // 받은 파일도, 남은 .part도 없다
    }

    [Fact]
    public async Task Rejected_transfer_closes_connection_and_saves_nothing()
    {
        using var d = new TempDir();
        using var receiver = Receiver(d.Path, _ => Task.FromResult(false));
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, receiver.Port);
        var stream = client.GetStream();

        await SendLikeDartAsync(stream, "no.txt", Encoding.UTF8.GetBytes("nope"));

        Assert.Equal(-1, await ReadByteAsync(stream));
        Assert.Empty(Directory.GetFiles(d.Path));
    }

    [Fact]
    public async Task Truncated_body_leaves_no_partial_file()
    {
        using var d = new TempDir();
        using var receiver = Receiver(d.Path);
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, receiver.Port);
            var stream = client.GetStream();
            var header = Encoding.UTF8.GetBytes("""{"fileName":"cut.bin","fileSize":1000}""");
            var len = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(len, (uint)header.Length);
            await stream.WriteAsync(len);
            await stream.WriteAsync(header);
            await stream.WriteAsync(new byte[10]);
        }

        await Task.Delay(800);
        Assert.Empty(Directory.GetFiles(d.Path));
    }

    [Fact]
    public async Task Sender_talks_to_receiver_and_reuses_connection()
    {
        using var d = new TempDir();
        var inbox = d.Dir("inbox");
        using var receiver = Receiver(inbox);
        using var sender = new TransferService(preferredPort: 0, allowLoopback: true) { SelfName = "Sender PC" };
        var a = d.File("a.txt", "alpha");
        var b = d.File("b.txt", "bravo");
        var peer = new Peer("ffff0000", "Receiver", "windows", IPAddress.Loopback, receiver.Port, DateTime.UtcNow);
        var items = new List<TransferItem>();

        await sender.SendFilesAsync(peer, [a, b], items.Add, CancellationToken.None);

        Assert.All(items, i => Assert.Equal(TransferState.Completed, i.State));
        Assert.Equal("alpha", File.ReadAllText(Path.Combine(inbox, "a.txt")));
        Assert.Equal("bravo", File.ReadAllText(Path.Combine(inbox, "b.txt")));
    }

    [Fact]
    public async Task Sender_reports_failure_when_nobody_listens()
    {
        using var d = new TempDir();
        using var sender = new TransferService(preferredPort: 0, allowLoopback: true);
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var closedPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var items = new List<TransferItem>();

        await sender.SendFilesAsync(new Peer("x", "Gone", "android", IPAddress.Loopback, closedPort, DateTime.UtcNow), [d.File("a.txt", "x")], items.Add, CancellationToken.None);

        Assert.Equal(TransferState.Failed, Assert.Single(items).State);
    }

    [Fact]
    public void Falls_back_to_free_port_when_transfer_port_is_taken()
    {
        var blocker = new TcpListener(IPAddress.Any, 0);
        blocker.Start();
        var taken = ((IPEndPoint)blocker.LocalEndpoint).Port;
        try
        {
            using var service = new TransferService(preferredPort: taken, allowLoopback: true);
            service.StartListening();
            Assert.True(service.IsListening);
            Assert.NotEqual(taken, service.Port);
        }
        finally
        {
            blocker.Stop();
        }
    }

    [Fact]
    public async Task Answers_flutter_ping_with_pong()
    {
        var port = FreeUdpPort();
        using var discovery = new DiscoveryService("aaaa1111", port, allowLoopback: true) { SelfName = "My PC", ReceivingEnabled = true, TcpPort = 58733 };
        discovery.Start();

        using var phone = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var ping = Encoding.UTF8.GetBytes("""{"type":"ping","id":"bbbb2222","name":"Galaxy","platform":"android"}""");
        await phone.SendAsync(ping, new IPEndPoint(IPAddress.Loopback, port));

        using var cts = new CancellationTokenSource(5000);
        var reply = await phone.ReceiveAsync(cts.Token);
        var pong = JsonDocument.Parse(reply.Buffer).RootElement;
        Assert.Equal("pong", pong.GetProperty("type").GetString());
        Assert.Equal("aaaa1111", pong.GetProperty("id").GetString());
        Assert.Equal(58733, pong.GetProperty("tcpPort").GetInt32());
    }

    [Fact]
    public async Task Stays_invisible_when_receiving_is_off()
    {
        var port = FreeUdpPort();
        using var discovery = new DiscoveryService("aaaa1111", port, allowLoopback: true) { ReceivingEnabled = false, TcpPort = 58733 };
        discovery.Start();

        using var phone = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await phone.SendAsync(Encoding.UTF8.GetBytes("""{"type":"ping","id":"bbbb2222"}"""), new IPEndPoint(IPAddress.Loopback, port));

        using var cts = new CancellationTokenSource(1500);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await phone.ReceiveAsync(cts.Token));
    }

    [Fact]
    public async Task Discovers_flutter_peer_from_pong()
    {
        // 휴대폰 역할: 탐색 포트에서 핑을 받으면 원본 앱처럼 보낸 주소·포트로 퐁
        using var phone = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)phone.Client.LocalEndPoint!).Port;
        using var discovery = new DiscoveryService("aaaa1111", port, allowLoopback: true, listen: false);
        var found = new TaskCompletionSource();
        discovery.PeersChanged += (_, _) => found.TrySetResult();
        discovery.Start();

        using var cts = new CancellationTokenSource(5000);
        var ping = await phone.ReceiveAsync(cts.Token);
        Assert.Equal("ping", JsonDocument.Parse(ping.Buffer).RootElement.GetProperty("type").GetString());
        var pong = Encoding.UTF8.GetBytes("""{"type":"pong","id":"bbbb2222","name":"Galaxy S24","platform":"android","tcpPort":58733}""");
        await phone.SendAsync(pong, ping.RemoteEndPoint);

        await found.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var peer = Assert.Single(discovery.Peers);
        Assert.Equal("Galaxy S24", peer.Name);
        Assert.True(peer.IsPhone);
        Assert.Equal(58733, peer.TcpPort);
    }

    private static int FreeUdpPort()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }
}
