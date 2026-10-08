using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;

namespace SideBySide.App.Commands;

/// <summary>명령 ID → 실제 ICommand 매핑. 뷰는 ID로만 명령을 참조한다.</summary>
public sealed class CommandHost
{
    private readonly Dictionary<string, RelayCommand> _commands = [];

    public CommandHost() => Current = this;

    /// <summary>XAML 마크업 확장(<see cref="CommandExtension"/>)용.</summary>
    public static CommandHost? Current { get; private set; }

    public ICommand this[string id] => _commands.TryGetValue(id, out var c) ? c : throw new KeyNotFoundException($"명령 '{id}'가 등록되지 않았습니다.");

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

    /// <summary>범위에 해당하는 단축키를 요소에 붙인다.</summary>
    public void BindKeys(UIElement element, CommandScope scope)
    {
        foreach (var def in CommandRegistry.All)
        {
            if (def.Scope != scope || def.Key == Key.None || !_commands.ContainsKey(def.Id)) continue;
            element.InputBindings.Add(new KeyBinding(this[def.Id], def.Key, def.Modifiers));
        }
    }
}
