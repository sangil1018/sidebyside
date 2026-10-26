using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SideBySide.Modules.TempCleaner;

/// <summary>
/// 경로 바꿔치기(TOCTOU)에 안전한 삭제. 검사 후 삭제 사이에 상위 폴더를 정션으로 바꿔 다른 곳의 파일을 지우게 하는 공격
/// (특히 누구나 쓸 수 있는 Windows\Temp를 관리자 권한으로 정리할 때)을 막는다.
/// <list type="number">
/// <item>링크를 따라가지 않고(FILE_FLAG_OPEN_REPARSE_POINT) 삭제 권한으로 파일 핸들을 연다.</item>
/// <item>그 핸들의 실제 경로(GetFinalPathNameByHandle — 정션이 모두 풀린 경로)가 정리 대상 폴더의 실제 경로 안인지 확인한다.</item>
/// <item>같은 핸들에 삭제 표시를 한다 (경로로 다시 찾지 않으므로 그 사이 바꿔치기가 소용없다).</item>
/// </list>
/// 파일이 링크면 링크 자체만 지워진다.
/// </summary>
public static class SecureDelete
{
    private const uint DELETE = 0x00010000;
    private const uint FILE_READ_ATTRIBUTES = 0x0080;
    private const uint FILE_SHARE_ALL = 0x1 | 0x2 | 0x4;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    private const uint FILE_NAME_NORMALIZED = 0x0;
    private const int FileDispositionInfo = 4;
    private const int FileDispositionInfoEx = 21;
    private const uint FILE_DISPOSITION_FLAG_DELETE = 0x1;
    private const uint FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE = 0x10;

    /// <summary>폴더의 실제 경로 (정션·심볼릭 링크를 모두 푼 경로, 끝에 \ 포함). 열 수 없으면 null.</summary>
    public static string? FinalDirectoryPath(string directory)
    {
        using var handle = CreateFile(directory, FILE_READ_ATTRIBUTES, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (handle.IsInvalid) return null;
        return FinalPath(handle) is { } path ? path.TrimEnd('\\') + "\\" : null;
    }

    /// <summary>
    /// <paramref name="path"/>를 지운다. 실제 위치가 <paramref name="finalRoot"/>(<see cref="FinalDirectoryPath"/>) 밖이면 지우지 않는다.
    /// 반환: 지웠으면 true와 크기. 사용 중·권한 없음·대상 밖이면 false.
    /// </summary>
    public static bool TryDelete(string path, string finalRoot, out long length)
    {
        length = 0;
        using var handle = CreateFile(path, DELETE | FILE_READ_ATTRIBUTES, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
        if (handle.IsInvalid) return false;

        // 실제 위치가 대상 폴더 안인지 (상위 폴더가 그 사이 정션으로 바뀌었으면 여기서 걸린다)
        if (FinalPath(handle) is not { } final || !final.StartsWith(finalRoot, StringComparison.OrdinalIgnoreCase)) return false;
        if (!GetFileSizeEx(handle, out length)) length = 0;

        // 읽기 전용이어도 지운다 (경로로 속성을 바꾸지 않는다). 구버전 Windows는 일반 삭제 표시로 대신
        var ex = new FILE_DISPOSITION_INFO_EX { Flags = FILE_DISPOSITION_FLAG_DELETE | FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE };
        if (SetFileInformationByHandle(handle, FileDispositionInfoEx, ref ex, (uint)Marshal.SizeOf<FILE_DISPOSITION_INFO_EX>())) return true;

        var basic = new FILE_DISPOSITION_INFO { DeleteFile = 1 };
        return SetFileInformationByHandle(handle, FileDispositionInfo, ref basic, (uint)Marshal.SizeOf<FILE_DISPOSITION_INFO>());
    }

    private static string? FinalPath(SafeFileHandle handle)
    {
        var buffer = new char[1024];
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, FILE_NAME_NORMALIZED);
        if (length == 0) return null;
        if (length >= buffer.Length)
        {
            buffer = new char[length + 1];
            length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, FILE_NAME_NORMALIZED);
            if (length == 0 || length >= buffer.Length) return null;
        }

        // \\?\C:\... 와 \\?\UNC\server\... 를 일반 경로로
        var path = new string(buffer, 0, (int)length);
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + path[8..];
        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_DISPOSITION_INFO
    {
        public byte DeleteFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_DISPOSITION_INFO_EX
    {
        public uint Flags;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, [Out] char[] buffer, uint length, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileSizeEx(SafeFileHandle handle, out long size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, ref FILE_DISPOSITION_INFO info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, ref FILE_DISPOSITION_INFO_EX info, uint size);
}
