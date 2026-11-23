using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SideBySide.App.Views;

/// <summary>여러 항목을 한 번의 SelectionChanged로 선택할 수 있는 ListView (대량 선택 동기화 성능용).</summary>
public sealed class FileListView : ListView
{
    public void SelectItems(IEnumerable items) => SetSelectedItems(items);

    /// <summary>
    /// ListBox는 Ctrl+←/→ 같은 이동 키를 (포커스만 이동·가로 스크롤로) 먼저 처리해 버려서
    /// 창에 붙은 전역 단축키(반대쪽 창으로 복사/이동 등)가 실행되지 않는다.
    /// 수식 키가 붙은 이동 키가 창 단축키로 지정돼 있으면 목록이 처리하지 않고 창으로 넘긴다.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (Window.GetWindow(this) is { } window && IsWindowShortcut(key, Keyboard.Modifiers, window.InputBindings)) return;
        base.OnKeyDown(e);
    }

    /// <summary>
    /// 목록이 처리하지 말고 창으로 넘겨야 하는 키인지: 수식 키가 붙은 이동 키이고, 그 조합이 지금 실행할 수 있는 창 단축키일 때.
    /// (실제 키보드 상태와 분리해 둔 판별 — 테스트에서 수식 키를 지정해 확인한다.)
    /// </summary>
    internal static bool IsWindowShortcut(Key key, ModifierKeys modifiers, InputBindingCollection bindings)
    {
        if (modifiers == ModifierKeys.None) return false;
        if (key is not (Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown)) return false;

        foreach (var binding in bindings)
        {
            if (binding is KeyBinding { Gesture: KeyGesture gesture } kb
                && gesture.Key == key && gesture.Modifiers == modifiers
                && kb.Command?.CanExecute(kb.CommandParameter) == true)
                return true;
        }

        return false;
    }
}
