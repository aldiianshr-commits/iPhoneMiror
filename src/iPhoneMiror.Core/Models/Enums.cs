namespace iPhoneMiror.Core.Models;

/// <summary>Physical or logical transport through which a device is reachable.</summary>
public enum ConnectionType
{
    Unknown = 0,
    Usb = 1,
    /// <summary>Wi-Fi (network) connection brokered by Apple Mobile Device Service ("Wi-Fi sync").</summary>
    Network = 2,
    /// <summary>Bluetooth presence only. Bluetooth cannot carry screen video.</summary>
    Bluetooth = 3,
}

public enum DeviceConnectionState
{
    Disconnected = 0,
    Connecting = 1,
    /// <summary>Transport is up, but the device has not trusted this computer yet.</summary>
    AwaitingTrust = 2,
    /// <summary>Trusted and a lockdown session can be opened.</summary>
    Connected = 3,
    Reconnecting = 4,
    Error = 5,
}

public enum PairingState
{
    Unknown = 0,
    NotPaired = 1,
    /// <summary>The "Trust This Computer?" dialog is displayed on the device.</summary>
    DialogPending = 2,
    /// <summary>The device is locked with a passcode; it must be unlocked before pairing.</summary>
    DeviceLocked = 3,
    UserDenied = 4,
    Paired = 5,
    Failed = 6,
}

public enum MirroringState
{
    Idle = 0,
    Starting = 1,
    Streaming = 2,
    Reconnecting = 3,
    Stopped = 4,
    Error = 5,
}

/// <summary>How the screen image is obtained from the iPhone.</summary>
public enum MirroringBackendKind
{
    /// <summary>MJPEG stream from WebDriverAgent running on the device (USB tunnel or Wi-Fi). Supports input.</summary>
    WebDriverAgent = 0,
    /// <summary>AirPlay screen mirroring received by an external UxPlay process (Wi-Fi only, no input).</summary>
    AirPlayUxPlay = 1,
}

public enum FeatureSupport
{
    /// <summary>Feature was verified on real hardware with this iOS version.</summary>
    Verified = 0,
    /// <summary>Implemented against documented/open protocols, not yet verified on real hardware for this version.</summary>
    ImplementedUnverified = 1,
    /// <summary>Requires an extra component (e.g. WebDriverAgent or UxPlay) to be installed.</summary>
    RequiresComponent = 2,
    /// <summary>Not available; see the reason.</summary>
    Unavailable = 3,
}
