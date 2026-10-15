using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using SideBySide.Core.Localization;

namespace SideBySide.App.Commands;

/// <summary>명령 ID → 실제 ICommand 매핑. 뷰는 ID로만 명령을 참조한다.</summary>
public sealed class CommandHost
{
    private readonly Dictionary<string, RelayCommand> _commands = [];

    public CommandHost() => Current = this;

    /// <summary>XAML 마크업 확장(<see cref="CommandExtension"/>)용.</summary>
    public static CommandHost? Current { get; private set; }

    public ICommand this[string id] => _commands.TryGetValue(id, out var c) ? c : throw new KeyNotFoundException(L.F("명령 '{0}'가 등록되지 않았습니다.", id));

    public void Register(string id, Action execute, Func<bool>? canExecute = null)
        => _commands[id] = canExecute is null ? new RelayCommand(execute) : new RelayCommand(execute, canExecute);

    public void Execute(string id)
    {
        var command = this[id];
        if (command.CanExecute(null)) command.Execute(null);
    }

    public void RaiseCanExecuteChanged()
    {
        foreach (var c in _commands.Values) c.NotifyCanExecuteChanged();
    }

    private readonly List<(UIElement Element, CommandScope Scope, List<KeyBinding> Bindings)> _bound = [];

    /// <summary>범위에 해당하는 단축키를 요소에 붙인다. 단축키를 바꾸면 <see cref="RebindAll"/>이 다시 붙인다.</summary>
    public void BindKeys(UIElement element, CommandScope scope)
    {
        var bindings = new List<KeyBinding>();
        _bound.Add((element, scope, bindings));
        AddBindings(element, scope, bindings);
    }

    /// <summary><see cref="CommandRegistry.ApplyOverrides"/> 뒤에 호출: 붙여 둔 단축키를 모두 새 표로 바꾼다.</summary>
    public void RebindAll()
    {
        foreach (var (element, scope, bindings) in _bound)
        {
            foreach (var binding in bindings) element.InputBindings.Remove(binding);
            bindings.Clear();
            AddBindings(element, scope, bindings);
        }
    }

    private void AddBindings(UIElement element, CommandScope scope, List<KeyBinding> bindings)
    {
        foreach (var def in CommandRegistry.All)
        {
            if (def.Scope != scope || def.Key == Key.None || !_commands.ContainsKey(def.Id)) continue;
            try
            {
                var binding = new KeyBinding(this[def.Id], def.Key, def.Modifiers);
                element.InputBindings.Add(binding);
                bindings.Add(binding);
            }
            catch (NotSupportedException)
            {
                // 사용할 수 없는 조합은 건너뛴다
            }
        }
    }
}
