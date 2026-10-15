using System.Windows;
using SideBySide.App.Infrastructure;
using SideBySide.Core.FileSystem;
using SideBySide.Core.Localization;

namespace SideBySide.App.Dialogs;

/// <summary>복사/이동 대상에 같은 이름이 있을 때: 덮어쓰기 / 둘 다 유지 / 건너뛰기 / 작업 취소.</summary>
public partial class ConflictDialog : Window
{
    public ConflictDialog(TransferConflict conflict)
    {
        InitializeComponent();
        var name = Path.GetFileName(conflict.DestinationPath);
        Headline.Text = L.F("'{0}'에 '{1}'이(가) 이미 있습니다.", Path.GetDirectoryName(conflict.DestinationPath), name);
        SourceInfo.Text = Describe(conflict.SourceIsDirectory, conflict.SourceSize, conflict.SourceModifiedUtc, conflict.SourceModifiedUtc > conflict.DestinationModifiedUtc);
        TargetInfo.Text = Describe(conflict.DestinationIsDirectory, conflict.DestinationSize, conflict.DestinationModifiedUtc, conflict.DestinationModifiedUtc > conflict.SourceModifiedUtc);

        // 파일↔폴더 충돌은 덮어쓸 수 없다
        OverwriteButton.IsEnabled = conflict.CanOverwrite;
        if (!conflict.CanOverwrite) OverwriteButton.IsDefault = false;
    }

    public ConflictChoice Choice { get; private set; } = ConflictChoice.Cancel;

    public bool ApplyToAll => ApplyAllBox.IsChecked == true;

    private static string Describe(bool isDirectory, long size, DateTime modifiedUtc, bool newer)
        => (isDirectory ? L.T("폴더") : Formatting.Size(size)) + " · " + Formatting.Date(modifiedUtc) + (newer ? L.T(" (더 최신)") : string.Empty);

    private void Finish(ConflictChoice choice)
    {
        Choice = choice;
        DialogResult = true;
    }

    private void OnOverwrite(object sender, RoutedEventArgs e) => Finish(ConflictChoice.Overwrite);

    private void OnKeepBoth(object sender, RoutedEventArgs e) => Finish(ConflictChoice.KeepBoth);

    private void OnSkip(object sender, RoutedEventArgs e) => Finish(ConflictChoice.Skip);

    private void OnCancel(object sender, RoutedEventArgs e) => Finish(ConflictChoice.Cancel);
}
