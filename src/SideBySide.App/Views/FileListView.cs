using System.Collections;
using System.Windows.Controls;

namespace SideBySide.App.Views;

/// <summary>여러 항목을 한 번의 SelectionChanged로 선택할 수 있는 ListView (대량 선택 동기화 성능용).</summary>
public sealed class FileListView : ListView
{
    public void SelectItems(IEnumerable items) => SetSelectedItems(items);
}
