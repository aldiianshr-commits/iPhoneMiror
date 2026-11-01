using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using iPhoneMiror.App.ViewModels;
using iPhoneMiror.Core.Abstractions;
using iPhoneMiror.Core.Compatibility;

namespace iPhoneMiror.App.Services;

/// <summary>UI-thread view of the device list and service status, shared by all pages.</summary>
public sealed partial class DeviceHub : ObservableObject
{
    private readonly IDeviceConnectionService _service;
    private readonly UiDispatcher _ui;
    private readonly CompatibilityEvaluator _compat;

    public DeviceHub(IDeviceConnectionService service, UiDispatcher ui, CompatibilityEvaluator compat)
    {
        _service = service;
        _ui = ui;
        _compat = compat;
        _service.DeviceChanged += (_, e) => _ui.Post(() => OnDeviceChanged(e));
        _service.ServiceStatusChanged += (_, s) => _ui.Post(() => ApplyStatus(s));
        foreach (var d in _service.Devices) Devices.Add(new DeviceItemViewModel(d, _compat));
        ApplyStatus(_service.ServiceStatus);
    }

    public ObservableCollection<DeviceItemViewModel> Devices { get; } = [];

    [ObservableProperty] public partial bool ServiceAvailable { get; set; }
    [ObservableProperty] public partial string ServiceMessage { get; set; } = "";
    [ObservableProperty] public partial int ConnectedCount { get; set; }
    [ObservableProperty] public partial bool HasDevices { get; set; }

    public event EventHandler? DevicesChanged;

    public DeviceItemViewModel? Find(string? udid) => udid is null ? null : Devices.FirstOrDefault(d => d.Udid == udid);

    public DeviceItemViewModel? FirstConnected => Devices.FirstOrDefault(d => d.IsConnected) ?? Devices.FirstOrDefault(d => d.IsAttached);

    private void OnDeviceChanged(DeviceChangedEventArgs e)
    {
        var existing = Find(e.Device.Udid);
        if (e.Kind == DeviceChangeKind.Removed)
        {
            if (existing is not null) Devices.Remove(existing);
        }
        else if (existing is null)
        {
            Devices.Add(new DeviceItemViewModel(e.Device, _compat));
        }
        else
        {
            existing.Apply(e.Device);
        }
        ConnectedCount = Devices.Count(d => d.IsConnected);
        HasDevices = Devices.Count > 0;
        DevicesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyStatus(MuxServiceStatus s)
    {
        ServiceAvailable = s.IsAvailable;
        ServiceMessage = s.Message;
    }
}
