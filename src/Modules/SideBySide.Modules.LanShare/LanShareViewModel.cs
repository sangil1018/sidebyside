using System.Collections.ObjectModel;
using System.Net.NetworkInformation;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SideBySide.Core.Localization;

namespace SideBySide.Modules.LanShare;

public sealed class LanShareSettings
{
    public string? AdapterId { get; set; }

    public int Role { get; set; } = 1;

    public string Folder { get; set; } = LanShareCore.DefaultFolder;

    public SharePermission Permission { get; set; } = SharePermission.Change;

    /// <summary>어댑터 ID → 적용 직전 IP 설정 (해제 시 복원).</summary>
    public Dictionary<string, SavedIpConfig> Previous { get; set; } = [];
}

/// <summary>실행 기록 한 줄.</summary>
public sealed record LogLine(bool Ok, string Text, string Detail);

public sealed partial class LanShareViewModel : ObservableObject
{
    private readonly IModuleHost _host;
    private readonly LanShareSettings _settings;

    public LanShareViewModel(IModuleHost host)
    {
        _host = host;
        _settings = host.LoadData<LanShareSettings>();
        _role = _settings.Role is 1 or 2 ? _settings.Role : 1;
        _folder = string.IsNullOrWhiteSpace(_settings.Folder) ? LanShareCore.DefaultFolder : _settings.Folder;
        _permission = Enum.IsDefined(_settings.Permission) ? _settings.Permission : SharePermission.Change;
        Log.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasLog));
        RefreshAdapters();
    }

    public ObservableCollection<AdapterInfo> Adapters { get; } = [];

    public ObservableCollection<LogLine> Log { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand), nameof(ResetCommand))]
    private AdapterInfo? _adapter;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPc1), nameof(IsPc2), nameof(PeerPath), nameof(MyIp))]
    private int _role;

    [ObservableProperty]
    private string _folder;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReadOnly), nameof(IsReadWrite))]
    private SharePermission _permission;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand), nameof(ResetCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private string? _sharePath;

    public bool IsPc1
    {
        get => Role == 1;
        set { if (value) Role = 1; }
    }

    public bool IsPc2
    {
        get => Role == 2;
        set { if (value) Role = 2; }
    }

    public bool IsReadOnly
    {
        get => Permission == SharePermission.Read;
        set { if (value) Permission = SharePermission.Read; }
    }

    public bool IsReadWrite
    {
        get => Permission == SharePermission.Change;
        set { if (value) Permission = SharePermission.Change; }
    }

    public string MyIp => LanShareCore.IpFor(Role);

    public bool HasLog => Log.Count > 0;

    public string PeerPath => LanShareCore.PeerSharePath(Role);

    /// <summary>현재 상태 요약 (독립 앱으로 설정한 경우도 IP·공유 이름이 같아 그대로 감지된다).</summary>
    public string StateText
    {
        get
        {
            var parts = new List<string>();
            if (Adapter?.Role is { } r) parts.Add(L.F("이 PC는 PC{0} ({1})로 설정되어 있습니다.", r, LanShareCore.IpFor(r)));
            else if (Adapter is not null) parts.Add(L.F("{0}: {1}", Adapter.Name, Adapter.Addresses.Count > 0 ? string.Join(", ", Adapter.Addresses) : L.T("IP 없음")));
            parts.Add(SharePath is { } path ? L.F("공유 중: \\\\{0}\\{1} → {2}", Environment.MachineName, LanShareCore.ShareName, path) : L.T("공유 안 함"));
            return string.Join("\n", parts);
        }
    }

    public void UseFolder(string folder)
    {
        Folder = folder;
        Status = L.T("공유할 폴더를 바꿨습니다. '적용'을 누르면 공유됩니다.");
    }

    [RelayCommand]
    private void RefreshAdapters()
    {
        var selectedId = Adapter?.Id ?? _settings.AdapterId;
        Adapters.Clear();
        foreach (var a in LanShareCore.Adapters()) Adapters.Add(a);
        Adapter = Adapters.FirstOrDefault(a => a.Id == selectedId) ?? Adapters.FirstOrDefault(a => a.Role is not null) ?? Adapters.FirstOrDefault();
        SharePath = LanShareCore.ExistingSharePath();
        if (Adapter?.Role is { } r) Role = r;
        OnPropertyChanged(nameof(StateText));
        if (Adapters.Count == 0) Status = L.T("유선 이더넷 어댑터를 찾지 못했습니다. 랜선(또는 USB 랜 어댑터)을 연결하세요.");
    }

    [RelayCommand]
    private void BrowseFolder()
    {
        if (_host.PickFolder(L.T("공유할 폴더"), Directory.Exists(Folder) ? Folder : null) is { } folder) Folder = folder;
    }

    private bool CanRun() => !IsBusy && Adapter is not null;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task ApplyAsync()
    {
        if (Adapter is not { } adapter) return;
        if (LanShareCore.ValidateShareFolder(Folder) is { } folderError)
        {
            _host.ShowError(folderError);
            return;
        }

        var permissionText = Permission == SharePermission.Read ? L.T("읽기") : L.T("읽기/쓰기");
        var message = L.F("'{0}'을(를) {1} / {2}로 설정하고, {3} 폴더를 같은 네트워크의 모든 사용자에게 {4} 권한으로 공유합니다.\n\n직접 연결한 PC끼리만 쓰세요. 공용 네트워크에 연결된 어댑터에서는 쓰지 마세요.",
            adapter.Name, MyIp, LanShareCore.SubnetMask, Folder, permissionText);
        if (!_host.Confirm(message, L.T("LAN 직접 연결 공유"))) return;

        // 해제할 때 되돌릴 수 있게 지금 설정을 저장 (이미 LAN 공유 IP면 덮어쓰지 않는다)
        if (adapter.Role is null) _settings.Previous[adapter.Id] = LanShareCore.ReadIpConfig(adapter.Id);
        Save();

        var payload = JsonSerializer.Serialize(new ApplyRequest { AdapterIndex = adapter.IPv4Index, Role = Role, Folder = Folder, Permission = Permission });
        await RunAsync(LanShareElevated.ApplyOperation, payload, L.T("적용"));
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task ResetAsync()
    {
        if (Adapter is not { } adapter) return;
        _settings.Previous.TryGetValue(adapter.Id, out var previous);
        var target = previous is { Dhcp: false, Address: { } a } ? a : L.T("자동(DHCP)");
        if (!_host.Confirm(L.F("'{0}'의 IP를 {1}(으)로 되돌리고 '{2}' 공유를 해제합니다.", adapter.Name, target, LanShareCore.ShareName), L.T("LAN 직접 연결 공유"))) return;

        var payload = JsonSerializer.Serialize(new ResetRequest { AdapterIndex = adapter.IPv4Index, Previous = previous });
        if (await RunAsync(LanShareElevated.ResetOperation, payload, L.T("해제")))
        {
            _settings.Previous.Remove(adapter.Id);
            Save();
        }
    }

    private async Task<bool> RunAsync(string operation, string payload, string title)
    {
        IsBusy = true;
        Status = L.T("관리자 권한으로 실행 중…");
        Log.Clear();
        try
        {
            var result = await _host.RunElevatedAsync(operation, payload);
            if (result.Cancelled)
            {
                Status = L.T("관리자 권한을 거부해 아무것도 바꾸지 않았습니다.");
                return false;
            }

            if (!result.Succeeded)
            {
                Status = result.Error ?? L.T("실패했습니다.");
                return false;
            }

            var response = JsonSerializer.Deserialize<StepsResponse>(result.Output!) ?? new StepsResponse();
            if (response.Error is { } error)
            {
                Status = error;
                Log.Add(new LogLine(false, error, string.Empty));
                return false;
            }

            foreach (var step in response.Steps) Log.Add(new LogLine(step.Succeeded, step.Description, step.Output));
            var failed = response.Steps.Count(s => !s.Succeeded);
            Status = failed == 0 ? L.F("{0} 완료.", title) : L.F("{0}: {1}개 단계가 실패했습니다. 아래 기록을 확인하세요.", title, failed);
            return failed == 0;
        }
        catch (JsonException ex)
        {
            Status = ex.Message;
            return false;
        }
        finally
        {
            IsBusy = false;
            await Task.Delay(800); // 어댑터 주소가 반영될 시간
            RefreshAdapters();
        }
    }

    [RelayCommand]
    private void OpenPeer() => _host.NavigateTo(PeerPath);

    [RelayCommand]
    private void OpenShareFolder()
    {
        var path = SharePath ?? Folder;
        if (Directory.Exists(path)) _host.NavigateTo(path);
    }

    [RelayCommand]
    private async Task PingPeerAsync()
    {
        var peer = LanShareCore.PeerIpFor(Role);
        Status = L.F("{0} 확인 중…", peer);
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(peer, 1500);
            Status = reply.Status == IPStatus.Success
                ? L.F("상대 PC({0}) 응답: {1} ms", peer, reply.RoundtripTime)
                : L.F("상대 PC({0})가 응답하지 않습니다: {1}", peer, reply.Status);
        }
        catch (PingException ex)
        {
            Status = ex.Message;
        }
    }

    public void Save()
    {
        _settings.AdapterId = Adapter?.Id;
        _settings.Role = Role;
        _settings.Folder = Folder;
        _settings.Permission = Permission;
        _host.SaveData(_settings);
    }
}
