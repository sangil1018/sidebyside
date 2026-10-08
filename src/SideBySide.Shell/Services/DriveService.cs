using System.Collections.Concurrent;
using System.IO;
using SideBySide.Shell.Abstractions;
using static SideBySide.Shell.Interop.NativeMethods;

namespace SideBySide.Shell.Services;

public sealed class DriveService : IDriveService
{
    private static readonly TimeSpan EjectTimeout = TimeSpan.FromSeconds(5);
    private readonly ConcurrentDictionary<string, bool> _ejectable = new(StringComparer.OrdinalIgnoreCase);

    public bool IsEjectable(string root)
        => _ejectable.GetOrAdd(Normalize(root), r =>
        {
            try
            {
                var drive = new DriveInfo(r);
                return drive.DriveType switch
                {
                    DriveType.Removable or DriveType.CDRom => true,
                    DriveType.Fixed => IsUsbBus(r),
                    _ => false,
                };
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
                return false;
            }
        });

    public async Task<bool> EjectAsync(string root)
    {
        root = Normalize(root);
        if (!await ShellContextMenu.InvokeVerbAsync(root, "eject")) return false;

        // 꺼내기는 비동기로 끝나므로 드라이브가 사라질 때까지 잠시 확인
        var deadline = DateTime.UtcNow + EjectTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!DriveExists(root)) break;
            await Task.Delay(250);
        }

        var gone = !DriveExists(root);
        if (gone) _ejectable.TryRemove(root, out _);
        return gone;
    }

    private static bool DriveExists(string root)
    {
        try
        {
            var drive = new DriveInfo(root);
            return drive.DriveType != DriveType.NoRootDirectory && (drive.DriveType != DriveType.CDRom || drive.IsReady);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string Normalize(string root)
    {
        var r = Path.GetPathRoot(root) ?? root;
        return r.EndsWith('\\') ? r : r + "\\";
    }

    /// <summary>외장 USB 하드처럼 "고정 디스크"로 보고되지만 USB로 연결된 장치인지 (IOCTL_STORAGE_QUERY_PROPERTY).</summary>
    private static bool IsUsbBus(string root)
    {
        using var handle = CreateFile(@"\\.\" + root.TrimEnd('\\'), 0, 0x3 /* SHARE_READ|WRITE */, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);
        if (handle.IsInvalid) return false;

        var query = new byte[12]; // STORAGE_PROPERTY_QUERY { StorageDeviceProperty=0, PropertyStandardQuery=0 }
        var output = new byte[1024];
        if (!DeviceIoControl(handle, IOCTL_STORAGE_QUERY_PROPERTY, query, query.Length, output, output.Length, out var returned, IntPtr.Zero) || returned < 32)
            return false;

        // STORAGE_DEVICE_DESCRIPTOR.BusType (오프셋 28)
        return BitConverter.ToInt32(output, 28) == BusTypeUsb;
    }
}
