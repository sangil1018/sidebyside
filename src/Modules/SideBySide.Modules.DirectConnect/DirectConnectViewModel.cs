using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SideBySide.Core.Localization;

namespace SideBySide.Modules.DirectConnect;

public sealed class DirectConnectSettings
{
    /// <summary>탐색에 쓰는 이 기기의 고정 ID (원본 앱처럼 무작위 16진수 8자리).</summary>
    public string? DeviceId { get; set; }

    public string? DeviceName { get; set; }

    public bool ReceiveEnabled { get; set; } = true;

    public string? SaveFolder { get; set; }

    /// <summary>받기 전에 확인 (신뢰한 기기는 묻지 않음).</summary>
    public bool AskBeforeReceive { get; set; } = true;

    public List<string> TrustedIds { get; set; } = [];

    /// <summary>화면을 닫아도 받기 대기를 계속.</summary>
    public bool BackgroundReceive { get; set; }
}

public sealed partial class DirectConnectViewModel : ObservableObject, IDisposable
{
    private const int MaxTransfers = 200;
    private readonly IModuleHost _host;
    private readonly Dispatcher _dispatcher;
    private readonly DirectConnectSettings _settings;
    private readonly CancellationTokenSource _cts = new();
    private DiscoveryService? _discovery;
    private TransferService? _transfer;
    private TaskCompletionSource<bool>? _approval;

    public DirectConnectViewModel(IModuleHost host, Dispatcher dispatcher)
    {
        _host = host;
        _dispatcher = dispatcher;
        _settings = host.LoadData<DirectConnectSettings>();
        if (_settings.DeviceId is not { Length: 8 }) _settings.DeviceId = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        _deviceName = string.IsNullOrWhiteSpace(_settings.DeviceName) ? Environment.MachineName : _settings.DeviceName;
        _receiveEnabled = _settings.ReceiveEnabled;
        _askBeforeReceive = _settings.AskBeforeReceive;
        _backgroundReceive = _settings.BackgroundReceive;
        _saveFolder = string.IsNullOrWhiteSpace(_settings.SaveFolder) ? TransferService.DefaultSaveFolder() : _settings.SaveFolder;
        PendingFiles.CollectionChanged += (_, _) => SendCommand.NotifyCanExecuteChanged();
    }

    public ObservableCollection<Peer> Peers { get; } = [];

    public ObservableCollection<string> PendingFiles { get; } = [];

    public ObservableCollection<TransferItem> Transfers { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private Peer? _selectedPeer;

    [ObservableProperty]
    private string _deviceName;

    [ObservableProperty]
    private bool _receiveEnabled;

    [ObservableProperty]
    private bool _askBeforeReceive;

    [ObservableProperty]
    private bool _backgroundReceive;

    [ObservableProperty]
    private string _saveFolder;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRequest))]
    private IncomingRequest? _request;

    public bool HasRequest => Request is not null;

    public bool IsRunning => _discovery is not null;

    /// <summary>화면을 닫아도 계속 실행할지 (받기 + 백그라운드 받기).</summary>
    public bool KeepRunning => BackgroundReceive && ReceiveEnabled;

    public string RequestText => Request is { } r
        ? L.F("{0}이(가) 파일을 보내려 합니다: {1} ({2:N0} KB)", r.Peer?.Name ?? r.SenderName, r.FileName, Math.Max(1, r.FileSize / 1024))
        : string.Empty;

    /// <summary>탐색·받기 서버 시작 (모듈이 켜져 있고 화면이 열렸거나 백그라운드 받기일 때만).</summary>
    public void Start()
    {
        if (_discovery is not null) return;
        _transfer = new TransferService
        {
            SaveFolder = SaveFolder,
            SelfName = DeviceName,
            Approve = ApproveAsync,
        };
        _discovery = new DiscoveryService(_settings.DeviceId!) { SelfName = DeviceName };
        _transfer.ResolvePeer = _discovery.FindByAddress;
        _transfer.TransferStarted += (_, item) => _dispatcher.BeginInvoke(() => AddTransfer(item));
        _discovery.PeersChanged += (_, _) => _dispatcher.BeginInvoke(RefreshPeers);

        try
        {
            if (ReceiveEnabled) _transfer.StartListening();
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            Status = L.F("받기 서버를 열지 못했습니다: {0}", ex.Message);
        }

        _discovery.TcpPort = _transfer.Port;
        _discovery.ReceivingEnabled = ReceiveEnabled && _transfer.IsListening;
        _discovery.Start();
        UpdateStatus();
    }

    public void Stop()
    {
        _approval?.TrySetResult(false);
        _discovery?.Dispose();
        _transfer?.Dispose();
        _discovery = null;
        _transfer = null;
        Peers.Clear();
    }

    private void UpdateStatus()
    {
        if (_transfer is null) return;
        var parts = new List<string>();
        parts.Add(ReceiveEnabled && _transfer.IsListening
            ? L.F("받기 대기 중 (포트 {0})", _transfer.Port)
            : L.T("받기 꺼짐 — 다른 기기에서 이 PC가 보이지 않습니다."));
        if (_discovery?.ListenerFallback == true) parts.Add(L.T("탐색 포트를 다른 프로그램이 독점하고 있어 이 PC에서 보낸 기기만 찾습니다."));
        Status = string.Join(" · ", parts);
    }

    partial void OnReceiveEnabledChanged(bool value)
    {
        if (_transfer is not null && _discovery is not null)
        {
            try
            {
                if (value) _transfer.StartListening();
                else _transfer.StopListening();
            }
            catch (System.Net.Sockets.SocketException ex)
            {
                Status = ex.Message;
            }

            _discovery.TcpPort = _transfer.Port;
            _discovery.ReceivingEnabled = value && _transfer.IsListening;
            UpdateStatus();
        }

        Save();
    }

    partial void OnDeviceNameChanged(string value)
    {
        var name = string.IsNullOrWhiteSpace(value) ? Environment.MachineName : value.Trim();
        if (_discovery is not null) _discovery.SelfName = name;
        if (_transfer is not null) _transfer.SelfName = name;
        Save();
    }

    partial void OnAskBeforeReceiveChanged(bool value) => Save();

    partial void OnBackgroundReceiveChanged(bool value) => Save();

    partial void OnSaveFolderChanged(string value)
    {
        if (_transfer is not null) _transfer.SaveFolder = value;
        Save();
    }

    partial void OnRequestChanged(IncomingRequest? value) => OnPropertyChanged(nameof(RequestText));

    private void RefreshPeers()
    {
        if (_discovery is null) return;
        var selectedId = SelectedPeer?.Id;
        Peers.Clear();
        foreach (var p in _discovery.Peers) Peers.Add(p);
        SelectedPeer = Peers.FirstOrDefault(p => p.Id == selectedId) ?? (Peers.Count == 1 ? Peers[0] : SelectedPeer is null ? null : Peers.FirstOrDefault());
    }

    public IReadOnlyList<Peer> CurrentPeers => _discovery?.Peers ?? [];

    private void AddTransfer(TransferItem item)
    {
        Transfers.Insert(0, item);
        while (Transfers.Count > MaxTransfers) Transfers.RemoveAt(Transfers.Count - 1);
        if (item.Direction == TransferDirection.Receive)
        {
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(TransferItem.State) && item.State == TransferState.Completed)
                    _dispatcher.BeginInvoke(() =>
                    {
                        _host.ShowNotice(L.F("받음: {0}", item.FileName));
                        _host.RefreshFolder(SaveFolder);
                    });
            };
        }
    }

    /// <summary>받기 승인 (작업자 스레드에서 호출). 신뢰한 기기거나 확인을 끈 경우 바로 받는다. 60초 안에 답이 없으면 거절.</summary>
    private async Task<bool> ApproveAsync(IncomingRequest request)
    {
        if (!AskBeforeReceive) return true;
        if (request.Peer is { } peer && _settings.TrustedIds.Contains(peer.Id)) return true;

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _dispatcher.InvokeAsync(() =>
        {
            _approval?.TrySetResult(false); // 한 번에 하나만 묻는다
            _approval = tcs;
            Request = request;
            _host.BringToFront();
        });

        var done = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(60), _cts.Token).ContinueWith(_ => false, TaskScheduler.Default));
        var accepted = done == tcs.Task && tcs.Task.Result;
        await _dispatcher.InvokeAsync(() =>
        {
            if (ReferenceEquals(_approval, tcs))
            {
                _approval = null;
                Request = null;
            }
        });
        return accepted;
    }

    [RelayCommand]
    private void Accept() => _approval?.TrySetResult(true);

    [RelayCommand]
    private void Reject() => _approval?.TrySetResult(false);

    [RelayCommand]
    private void AlwaysAccept()
    {
        if (Request?.Peer is { } peer && !_settings.TrustedIds.Contains(peer.Id))
        {
            _settings.TrustedIds.Add(peer.Id);
            Save();
        }

        _approval?.TrySetResult(true);
    }

    [RelayCommand]
    private void ForgetTrusted()
    {
        _settings.TrustedIds.Clear();
        Save();
        Status = L.T("신뢰한 기기 목록을 비웠습니다. 다음부터 받기 전에 다시 묻습니다.");
    }

    // ── 보내기 ──────────────────────────────────────────────────

    public void QueueFiles(IEnumerable<string> paths)
    {
        foreach (var p in paths.Where(File.Exists))
        {
            if (!PendingFiles.Contains(p, StringComparer.OrdinalIgnoreCase)) PendingFiles.Add(p);
        }
    }

    /// <summary>우클릭 메뉴에서 기기를 바로 고른 경우.</summary>
    public void SendTo(string peerId, IReadOnlyList<string> paths)
    {
        var peer = _discovery?.FindById(peerId);
        if (peer is null)
        {
            QueueFiles(paths);
            Status = L.T("기기가 사라졌습니다. 목록에서 다시 고르세요.");
            return;
        }

        _ = SendAsync(peer, paths.Where(File.Exists).ToList());
    }

    private bool CanSend() => SelectedPeer is not null && PendingFiles.Count > 0;

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task Send()
    {
        if (SelectedPeer is not { } peer) return;
        var files = PendingFiles.ToList();
        PendingFiles.Clear();
        await SendAsync(peer, files);
    }

    private async Task SendAsync(Peer peer, IReadOnlyList<string> files)
    {
        if (_transfer is null || files.Count == 0) return;
        try
        {
            var transfer = _transfer;
            await Task.Run(() => transfer.SendFilesAsync(peer, files, null, _cts.Token));
            Status = L.F("{0}에 {1}개 파일 보내기 끝", peer.Name, files.Count);
        }
        catch (OperationCanceledException)
        {
        }
    }

    [RelayCommand]
    private void AddFiles() => QueueFiles(_host.PickFiles(L.T("보낼 파일")));

    [RelayCommand]
    private void UseSelection() => QueueFiles(_host.SelectedPaths);

    [RelayCommand]
    private void ClearPending() => PendingFiles.Clear();

    [RelayCommand]
    private void Refresh() => _discovery?.RefreshNow();

    [RelayCommand]
    private void OpenSaveFolder()
    {
        Directory.CreateDirectory(SaveFolder);
        _host.NavigateTo(SaveFolder);
    }

    [RelayCommand]
    private void ChangeSaveFolder()
    {
        if (_host.PickFolder(L.T("받은 파일을 저장할 폴더"), SaveFolder) is { } folder) SaveFolder = folder;
    }

    [RelayCommand]
    private void OpenReceived(TransferItem? item)
    {
        if (item?.SavedPath is { } path && File.Exists(path)) _host.NavigateTo(path);
    }

    [RelayCommand]
    private void OpenHotspotSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:network-mobilehotspot") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _host.ShowError(ex.Message);
        }
    }

    [RelayCommand]
    private void ClearTransfers()
    {
        foreach (var t in Transfers.Where(t => t.State is TransferState.Completed or TransferState.Failed or TransferState.Rejected).ToList()) Transfers.Remove(t);
    }

    public void Save()
    {
        _settings.DeviceName = DeviceName;
        _settings.ReceiveEnabled = ReceiveEnabled;
        _settings.AskBeforeReceive = AskBeforeReceive;
        _settings.BackgroundReceive = BackgroundReceive;
        _settings.SaveFolder = string.Equals(SaveFolder, TransferService.DefaultSaveFolder(), StringComparison.OrdinalIgnoreCase) ? null : SaveFolder;
        _host.SaveData(_settings);
    }

    public void Dispose()
    {
        _cts.Cancel();
        Stop();
    }
}
