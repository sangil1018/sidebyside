using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using SideBySide.App.Modules;
using SideBySide.Modules.TempCleaner;

namespace SideBySide.Modules.Tests;

/// <summary>승격 프로세스 진입점을 승격 없이 직접 호출해 입력 검증을 확인한다 (UAC는 띄우지 않음).</summary>
public class ElevationTests
{
    [Fact]
    public void Normal_start_is_not_handled() => Assert.Null(Elevation.TryRunElevated(["C:\\some\\folder"]));

    [Theory]
    [InlineData("tempcleaner.clean", "SideBySide-elev-0123456789abcdef0123456789abcdef", "short")]
    [InlineData("tempcleaner.clean", "evil-pipe", "0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("cmd.exe", "SideBySide-elev-0123456789abcdef0123456789abcdef", "0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("unknown.op", "SideBySide-elev-0123456789abcdef0123456789abcdef", "0000000000000000000000000000000000000000000000000000000000000000")]
    public void Malformed_or_unknown_requests_are_refused(string op, string pipe, string hash)
        => Assert.Equal(2, Elevation.TryRunElevated([Elevation.Switch, op, pipe, hash]));

    [Fact]
    public async Task Payload_that_does_not_match_hash_is_not_executed()
    {
        var pipeName = "SideBySide-elev-" + Guid.NewGuid().ToString("N");
        var signed = Encoding.UTF8.GetBytes("""{"Ids":[]}""");
        var tampered = Encoding.UTF8.GetBytes("""{"Ids":["windows-temp"]}""");

        await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var run = Task.Run(() => Elevation.TryRunElevated([Elevation.Switch, CleanerElevated.Operation, pipeName, Elevation.Hash(signed)]));
        await server.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await WriteAsync(server, tampered);

        Assert.Equal(3, await run.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Matching_payload_runs_registered_operation_and_returns_result()
    {
        var pipeName = "SideBySide-elev-" + Guid.NewGuid().ToString("N");
        // 사용자 영역 ID만 넣어 실제로는 아무것도 지우지 않는다 (승격 쪽에서 거부)
        var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new CleanRequest { Ids = ["user-temp"] }));

        await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var run = Task.Run(() => Elevation.TryRunElevated([Elevation.Switch, CleanerElevated.Operation, pipeName, Elevation.Hash(payload)]));
        await server.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await WriteAsync(server, payload);

        var header = new byte[5];
        await server.ReadExactlyAsync(header);
        var body = new byte[BitConverter.ToInt32(header, 1)];
        await server.ReadExactlyAsync(body);

        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, header[0]);
        var response = JsonSerializer.Deserialize<CleanResponse>(body)!;
        Assert.Equal(["user-temp"], response.Rejected);
    }

    [Fact]
    public async Task Host_refuses_operations_not_registered_by_the_module()
    {
        var result = await Elevation.RunAsync("cmd.run", "{}", CancellationToken.None);
        Assert.False(result.Succeeded);
        Assert.False(result.Cancelled);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Every_elevated_operation_belongs_to_a_module_that_declares_it()
    {
        Assert.NotNull(ModuleCatalog.FindElevated("tempcleaner.clean"));
        Assert.NotNull(ModuleCatalog.FindElevated("lanshare.apply"));
        Assert.NotNull(ModuleCatalog.FindElevated("lanshare.reset"));
        Assert.Null(ModuleCatalog.FindElevated("resize.anything"));
        Assert.Equal(3, ModuleCatalog.All.Sum(m => m.ElevatedOperations.Count));
    }

    private static async Task WriteAsync(Stream stream, byte[] body)
    {
        var header = new byte[5];
        BitConverter.TryWriteBytes(header.AsSpan(1), body.Length);
        await stream.WriteAsync(header);
        await stream.WriteAsync(body);
        await stream.FlushAsync();
    }
}
