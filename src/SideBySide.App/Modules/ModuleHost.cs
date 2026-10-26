using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using Microsoft.Win32;
using SideBySide.App.ViewModels;
using SideBySide.Core.Localization;
using SideBySide.Core.Settings;
using SideBySide.Modules;

namespace SideBySide.App.Modules;

/// <summary>모듈 하나에 주는 앱 기능. 설정 데이터는 이 모듈 항목만 읽고 쓴다.</summary>
internal sealed class ModuleHost(ModuleDescriptor descriptor, ModuleManager manager, MainViewModel main, AppSettings settings) : IModuleHost
{
    public string ModuleId => descriptor.Id;

    public Window? Owner => manager.WindowOf(descriptor.Id) ?? Application.Current?.MainWindow;

    public string? ActiveFolder
    {
        get
        {
            var tab = main.ActiveTab;
            return tab.IsThisPc || TabViewModel.IsVirtualFolder(tab.Path) || !Directory.Exists(tab.Path) ? null : tab.Path;
        }
    }

    public IReadOnlyList<string> SelectedPaths => main.ActiveTab.SelectedPaths;

    public void NavigateTo(string path)
    {
        if (Directory.Exists(path))
        {
            _ = main.ActiveTab.NavigateAsync(path);
        }
        else if (Path.GetDirectoryName(path) is { } parent && Directory.Exists(parent))
        {
            _ = main.ActiveTab.NavigateAsync(parent, Path.GetFileName(path));
        }

        Application.Current?.MainWindow?.Activate();
    }

    public void RefreshFolder(string folder)
    {
        foreach (var pane in new[] { main.Left, main.Right })
        {
            foreach (var tab in pane.Tabs)
            {
                if (string.Equals(tab.Path.TrimEnd('\\'), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) _ = tab.RefreshAsync();
            }
        }
    }

    public void ShowNotice(string text) => main.ShowNotice(text);

    public void ShowError(string message)
    {
        if (Owner is { } owner) MessageBox.Show(owner, message, descriptor.Title, MessageBoxButton.OK, MessageBoxImage.Error);
        else MessageBox.Show(message, descriptor.Title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    public bool Confirm(string message, string title)
        => (Owner is { } owner
            ? MessageBox.Show(owner, message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question)
            : MessageBox.Show(message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question)) == MessageBoxResult.OK;

    public string? PickFolder(string title, string? initialFolder)
    {
        var dialog = new OpenFolderDialog { Title = title };
        if (initialFolder is not null && Directory.Exists(initialFolder)) dialog.InitialDirectory = initialFolder;
        return dialog.ShowDialog(Owner) == true ? dialog.FolderName : null;
    }

    public IReadOnlyList<string> PickFiles(string title)
    {
        var dialog = new OpenFileDialog { Title = title, Multiselect = true, Filter = L.T("모든 파일") + " (*.*)|*.*" };
        if (ActiveFolder is { } folder) dialog.InitialDirectory = folder;
        return dialog.ShowDialog(Owner) == true ? dialog.FileNames : [];
    }

    public T LoadData<T>() where T : class, new()
    {
        if (!settings.Modules.TryGetValue(descriptor.Id, out var module) || module.Data is null) return new T();
        try
        {
            return module.Data.Deserialize<T>(JsonSettingsStore.JsonOptions) ?? new T();
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            return new T(); // 형식이 바뀐 이전 데이터는 버린다
        }
    }

    public void SaveData<T>(T data) where T : class
    {
        var node = JsonSerializer.SerializeToNode(data, JsonSettingsStore.JsonOptions) as JsonObject;
        if (node is null || node.ToJsonString().Length > ModuleSettings.MaxDataLength) return;
        if (!settings.Modules.TryGetValue(descriptor.Id, out var module)) settings.Modules[descriptor.Id] = module = new ModuleSettings();
        module.Data = node;
    }

    public Task<ElevatedResult> RunElevatedAsync(string operation, string payloadJson, CancellationToken cancellationToken = default)
    {
        // 이 모듈이 등록한 작업만 (다른 모듈의 작업 ID로 승격하지 못하게)
        if (!descriptor.ElevatedOperations.ContainsKey(operation))
            return Task.FromResult(new ElevatedResult(false, null, L.F("허용되지 않은 작업입니다: {0}", operation)));
        return Elevation.RunAsync(operation, payloadJson, cancellationToken);
    }

    public void BringToFront() => manager.BringToFront(descriptor.Id);
}
