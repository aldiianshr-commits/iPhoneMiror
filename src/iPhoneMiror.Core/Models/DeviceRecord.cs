namespace iPhoneMiror.Core.Models;

/// <summary>Persisted entry for a device seen at least once (Recent Devices).</summary>
public sealed record DeviceRecord(
    string Udid,
    string? Name,
    string? ProductType,
    string? ProductVersion,
    ConnectionType LastConnectionType,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    bool AutoReconnect);

public sealed record ConnectionEvent(
    long Id,
    string Udid,
    DateTimeOffset Timestamp,
    string Kind,
    string? Detail);
