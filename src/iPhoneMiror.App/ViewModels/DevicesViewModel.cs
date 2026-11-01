using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using iPhoneMiror.App.Services;
using iPhoneMiror.Connection.Pairing;
using iPhoneMiror.Core.Abstractions;

namespace iPhoneMiror.App.ViewModels;

public sealed partial class DevicesViewModel : ObservableObject
{
    private readonly IDeviceConnectionService _connections;
    private readonly IDeviceRepository _repository;
    private readonly IPairRecordStore _pairRecords;
    private readonly MirroringViewModel _mirroring;
    private readonly NavigationService _navigation;

    public DevicesViewModel(DeviceHub hub, IDeviceConnectionService connections, IDeviceRepository repository, IPairRecordStore pairRecords,
        MirroringViewModel mirroring, NavigationService navigation)
    {
        Hub = hub;
        _connections = connections;
        _repository = repository;
        _pairRecords = pairRecords;
        _mirroring = mirroring;
        _navigation = navigation;
        hub.DevicesChanged += (_, _) =>
        {
            if (Selected is null || !Hub.Devices.Contains(Selected)) Selected = Hub.Devices.FirstOrDefault();
        };
        Selected = Hub.Devices.FirstOrDefault();
    }

    public DeviceHub Hub { get; }

    [ObservableProperty] public partial DeviceItemViewModel? Selected { get; set; }
    [ObservableProperty] public partial string Message { get; set; } = "";

    public bool HasSelection => Selected is not null;

    partial void OnSelectedChanged(DeviceItemViewModel? value) => OnPropertyChanged(nameof(HasSelection));

    [RelayCommand]
    private async Task RefreshAsync(DeviceItemViewModel? d)
    {
        d ??= Selected;
        if (d is null) return;
        Message = $"Refreshing {d.Name}…";
        try
        {
            await _connections.RefreshAsync(d.Udid);
            Message = $"{d.Name} refreshed.";
        }
        catch (Exception ex) { Message = ex.Message; }
    }

    [RelayCommand]
    private async Task DisconnectAsync(DeviceItemViewModel? d)
    {
        d ??= Selected;
        if (d is null) return;
        if (_mirroring.Session.ActiveDeviceUdid == d.Udid) await _mirroring.StopCommand.ExecuteAsync(null);
        await _connections.DisconnectAsync(d.Udid);
        Message = $"{d.Name} disconnected. It will not be used until you reconnect it.";
    }

    [RelayCommand]
    private async Task ReconnectAsync(DeviceItemViewModel? d)
    {
        d ??= Selected;
        if (d is null) return;
        await _connections.ReconnectAsync(d.Udid);
        Message = $"Reconnecting {d.Name}…";
    }

    [RelayCommand]
    private async Task PairAsync(DeviceItemViewModel? d)
    {
        d ??= Selected;
        if (d is null) return;
        Message = "Unlock the iPhone and tap “Trust”.";
        try { Message = $"Pairing result: {await _connections.PairAsync(d.Udid)}"; }
        catch (Exception ex) { Message = ex.Message; }
    }

    [RelayCommand]
    private async Task ForgetAsync(DeviceItemViewModel? d)
    {
        d ??= Selected;
        if (d is null) return;
        await _repository.RemoveAsync(d.Udid);
        await _pairRecords.DeleteLocalAsync(d.Udid);
        Message = $"Removed {d.Name} from recent devices and deleted iPhoneMiror's local pairing copy. To remove trust on the iPhone: Settings > General > Transfer or Reset iPhone > Reset > Reset Location & Privacy.";
    }

    [RelayCommand]
    private async Task MirrorAsync(DeviceItemViewModel? d)
    {
        d ??= Selected;
        if (d is null) return;
        _mirroring.SelectedDevice = d;
        _navigation.NavigateTo("mirroring");
        if (_mirroring.StartCommand.CanExecute(null)) await _mirroring.StartCommand.ExecuteAsync(null);
        _mirroring.OpenWindowCommand.Execute(null);
    }
}
