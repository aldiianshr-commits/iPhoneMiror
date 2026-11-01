using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using iPhoneMiror.App.Services;
using iPhoneMiror.Core.Abstractions;
using iPhoneMiror.Core.Models;

namespace iPhoneMiror.App.ViewModels;

public sealed partial class DashboardViewModel : ObservableObject
{
    private readonly IDeviceRepository _repository;
    private readonly NavigationService _navigation;
    private readonly MirroringViewModel _mirroring;
    private readonly IAppleAccountService _account;

    public DashboardViewModel(DeviceHub hub, IDeviceRepository repository, NavigationService navigation, MirroringViewModel mirroring, IAppleAccountService account, UiDispatcher ui)
    {
        Hub = hub;
        _repository = repository;
        _navigation = navigation;
        _mirroring = mirroring;
        _account = account;
        hub.DevicesChanged += async (_, _) =>
        {
            OnPropertyChanged(nameof(PrimaryDeviceText));
            StartMirroringCommand.NotifyCanExecuteChanged();
            await LoadRecentAsync();
        };
        _mirroring.StartCommand.CanExecuteChanged += (_, _) => StartMirroringCommand.NotifyCanExecuteChanged();
        hub.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(DeviceHub.ServiceAvailable) or nameof(DeviceHub.ServiceMessage)) OnPropertyChanged(nameof(ServiceSummary)); };
        _account.StatusChanged += (_, _) => ui.Post(() => OnPropertyChanged(nameof(AccountSummary)));
        _mirroring.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MirroringViewModel.StateText)) OnPropertyChanged(nameof(MirroringSummary));
            if (e.PropertyName is nameof(MirroringViewModel.InputStatusText)) OnPropertyChanged(nameof(InputSummary));
        };
    }

    public DeviceHub Hub { get; }

    public ObservableCollection<RecentDeviceItem> RecentDevices { get; } = [];

    [ObservableProperty]
    private bool _hasRecent;

    public string Greeting
    {
        get
        {
            var h = DateTime.Now.Hour;
            return h < 5 ? "Good evening" : h < 12 ? "Good morning" : h < 18 ? "Good afternoon" : "Good evening";
        }
    }

    public string TodayText => DateTime.Now.ToString("dddd, d MMMM yyyy", CultureInfo.CurrentCulture);

    public string ServiceSummary => Hub.ServiceAvailable ? "Apple Mobile Device Service is running" : "Apple Mobile Device Service unavailable";

    public string PrimaryDeviceText => Hub.FirstConnected is { } d ? $"{d.Name} · {d.IosVersionText}" : "No iPhone connected";

    public string MirroringSummary => _mirroring.StateText;

    public string InputSummary => _mirroring.InputStatusText;

    public string AccountSummary => _account.Status.State switch
    {
        AppleAccountState.SignedIn => _account.Status.Email ?? "Signed in",
        AppleAccountState.NotConfigured => "Not configured",
        _ => "Signed out",
    };

    public async Task LoadRecentAsync()
    {
        try
        {
            var recent = await _repository.GetRecentAsync(6);
            RecentDevices.Clear();
            foreach (var r in recent)
            {
                RecentDevices.Add(new RecentDeviceItem(r.Udid, r.Name ?? DeviceModelCatalog.GetMarketingName(r.ProductType),
                    DeviceModelCatalog.GetMarketingName(r.ProductType), r.ProductVersion is null ? "iOS —" : $"iOS {r.ProductVersion}",
                    r.LastSeen.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
                    r.LastConnectionType == ConnectionType.Network ? "Wi-Fi" : "USB"));
            }
            HasRecent = RecentDevices.Count > 0;
        }
        catch (Exception)
        {
            HasRecent = false;
        }
    }

    private bool CanStartMirroring() => (Hub.FirstConnected is { IsAttached: true } || _mirroring.IsAirPlay) && !_mirroring.IsRunning;

    [RelayCommand(CanExecute = nameof(CanStartMirroring))]
    private async Task StartMirroringAsync()
    {
        if (Hub.FirstConnected is { } d) _mirroring.SelectedDevice = d;
        if (!_mirroring.StartCommand.CanExecute(null)) return;
        await _mirroring.StartCommand.ExecuteAsync(null);
        _mirroring.OpenWindowCommand.Execute(null);
    }

    [RelayCommand] private void OpenDisplay() => _mirroring.OpenWindowCommand.Execute(null);
    [RelayCommand] private void GoConnect() => _navigation.NavigateTo("connect");
    [RelayCommand] private void GoDevices() => _navigation.NavigateTo("devices");
    [RelayCommand] private void GoMirroring() => _navigation.NavigateTo("mirroring");
    [RelayCommand] private void GoAccount() => _navigation.NavigateTo("account");
}
