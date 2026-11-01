using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using iPhoneMiror.App.Services;
using iPhoneMiror.Core.Abstractions;
using iPhoneMiror.Core.Models;
using iPhoneMiror.Core.Settings;

namespace iPhoneMiror.App.ViewModels;

public sealed record PairingInfoItem(string Name, string Udid, string Pairing, string Connection);

public sealed partial class AccountViewModel : ObservableObject
{
    private readonly IAppleAccountService _account;
    private readonly ISettingsStore _settings;
    private readonly UiDispatcher _ui;

    public AccountViewModel(IAppleAccountService account, ISettingsStore settings, DeviceHub hub, UiDispatcher ui)
    {
        _account = account;
        _settings = settings;
        _ui = ui;
        Hub = hub;
        ServicesId = settings.Current.AppleAccount.ServicesId;
        RedirectUri = settings.Current.AppleAccount.RedirectUri;
        _account.StatusChanged += (_, s) => _ui.Post(() => Apply(s));
        hub.DevicesChanged += (_, _) => RefreshPairings();
        Apply(_account.Status);
        RefreshPairings();
    }

    public DeviceHub Hub { get; }
    public ObservableCollection<PairingInfoItem> Pairings { get; } = [];

    [ObservableProperty] public partial string StateText { get; set; } = "";
    [ObservableProperty] public partial string Message { get; set; } = "";
    [ObservableProperty] public partial string AccountText { get; set; } = "";
    [ObservableProperty] public partial string VerifiedText { get; set; } = "";
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SignOutCommand), nameof(SignInCommand))]
    public partial bool IsSignedIn { get; set; }
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    public partial bool IsConfigured { get; set; }
    [ObservableProperty] public partial string ServicesId { get; set; } = "";
    [ObservableProperty] public partial string RedirectUri { get; set; } = "";
    [ObservableProperty] public partial string ConfigMessage { get; set; } = "";
    [ObservableProperty] public partial IndicatorKind Indicator { get; set; }

    private void Apply(AppleAccountStatus s)
    {
        IsSignedIn = s.State == AppleAccountState.SignedIn;
        IsConfigured = _account.IsConfigured;
        StateText = s.State switch
        {
            AppleAccountState.SignedIn => "Signed in",
            AppleAccountState.SigningIn => "Waiting for browser…",
            AppleAccountState.SignedOut => "Signed out",
            AppleAccountState.NotConfigured => "Not configured",
            _ => "Error",
        };
        Indicator = s.State switch
        {
            AppleAccountState.SignedIn => IndicatorKind.Good,
            AppleAccountState.SigningIn => IndicatorKind.Warn,
            AppleAccountState.Error => IndicatorKind.Bad,
            _ => IndicatorKind.Idle,
        };
        Message = s.Message;
        AccountText = IsSignedIn ? (s.Email ?? "Apple ID (e-mail hidden)") : "—";
        VerifiedText = s.AuthenticatedAt is { } at ? $"Identity verified by Apple on {at.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}" : "";
    }

    private void RefreshPairings()
    {
        Pairings.Clear();
        foreach (var d in Hub.Devices)
        {
            Pairings.Add(new PairingInfoItem(d.Name, d.Udid, d.PairingText, d.ConnectionText));
        }
    }

    private bool CanSignIn() => IsConfigured && !IsSignedIn;

    [RelayCommand(CanExecute = nameof(CanSignIn))]
    private async Task SignInAsync()
    {
        try { await _account.BeginSignInAsync(); }
        catch (Exception ex) { Message = ex.Message; }
    }

    [RelayCommand(CanExecute = nameof(IsSignedIn))]
    private Task SignOutAsync() => _account.SignOutAsync();

    [RelayCommand]
    private async Task SaveConfigAsync()
    {
        var id = ServicesId.Trim();
        var uri = RedirectUri.Trim();
        if (uri.Length > 0 && (!Uri.TryCreate(uri, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps))
        {
            ConfigMessage = "The redirect URI must be an absolute https:// URL registered for your Services ID.";
            return;
        }
        await _settings.UpdateAsync(s =>
        {
            s.AppleAccount.ServicesId = id;
            s.AppleAccount.RedirectUri = uri;
        });
        await _account.LoadAsync();
        ConfigMessage = _account.IsConfigured ? "Saved. You can now sign in." : "Saved. Both fields are required to enable Sign in with Apple.";
    }
}
