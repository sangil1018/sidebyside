using System.IO;
using System.Security.Principal;
using SideBySide.Core.FileSystem;
using SideBySide.Core.RecycleBin;
using SideBySide.Shell.Abstractions;
using static SideBySide.Shell.Interop.NativeMethods;
using SideBySide.Core.Localization;

namespace SideBySide.Shell.Services;

/// <summary>
/// SHFileOperation 기반 삭제(휴지통)·이름 바꾸기. FOF_SILENT로 셸 진행 창을 띄우지 않고,
/// 별도 STA 스레드에서 실행해 UI가 멈추지 않는다.
/// </summary>
public sealed class ShellFileOperations(IOwnerWindow owner) : IFileOperations
{
    public Task<bool> DeleteAsync(IReadOnlyList<string> paths, bool permanent)
        => RunAsync(FO_DELETE, paths, null, permanent
            ? (ushort)(FOF_SILENT | FOF_NOCONFIRMATION)
            : (ushort)(FOF_SILENT | FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_WANTNUKEWARNING));

    public Task<bool> RenameAsync(string path, string newName)
        => RunAsync(FO_RENAME, [path], Path.Combine(Path.GetDirectoryName(path)!, newName), (ushort)(FOF_SILENT | FOF_ALLOWUNDO));

    public Task<IReadOnlyList<string>> RestoreFromRecycleBinAsync(IReadOnlyList<string> originalPaths)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value;
        if (sid is null) return Task.FromResult<IReadOnlyList<string>>([]);

        // 드라이브마다 <루트>\$Recycle.Bin\<SID> 폴더에 보관됨
        return Task.Run(() => RecycleBinStore.Restore(originalPaths, path =>
        {
            var root = Path.GetPathRoot(path);
            return string.IsNullOrEmpty(root) ? null : Path.Combine(root, "$Recycle.Bin", sid);
        }));
    }

    public string CreateFolder(string parentDirectory)
    {
        var path = PathNames.MakeUnique(parentDirectory, L.T("새 폴더"));
        Directory.CreateDirectory(path);
        return path;
    }

    public string CreateTextFile(string parentDirectory)
    {
        var path = PathNames.MakeUnique(parentDirectory, L.T("새 텍스트 문서"), ".txt");
        using (File.Create(path))
        {
        }

        return path;
    }

    private Task<bool> RunAsync(uint func, IReadOnlyList<string> from, string? to, ushort flags)
    {
        if (from.Count == 0) return Task.FromResult(true);

        var hwnd = owner.Handle;
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var op = new SHFILEOPSTRUCT
                {
                    hwnd = hwnd,
                    wFunc = func,
                    pFrom = string.Join('\0', from) + '\0',
                    pTo = to is null ? null : to + '\0',
                    fFlags = flags,
                };
                var result = SHFileOperation(ref op);
                tcs.SetResult(result == 0 && !op.fAnyOperationsAborted);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        })
        { IsBackground = true, Name = "ShellFileOperation" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }
}
