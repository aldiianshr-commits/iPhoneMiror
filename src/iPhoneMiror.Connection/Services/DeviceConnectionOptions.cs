namespace iPhoneMiror.Connection.Services;

public sealed class DeviceConnectionOptions
{
    public bool AutoPair { get; set; } = true;
    public bool AutoReconnect { get; set; } = true;
    public bool EnableWifiDevices { get; set; } = true;
    public TimeSpan PairingTimeout { get; set; } = TimeSpan.FromMinutes(2);
    public TimeSpan PairingPollInterval { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan ReconnectInitialDelay { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan ReconnectMaxDelay { get; set; } = TimeSpan.FromSeconds(30);
    public int MaxInitializeAttempts { get; set; } = 5;
    public string Label { get; set; } = "iPhoneMiror";
}
