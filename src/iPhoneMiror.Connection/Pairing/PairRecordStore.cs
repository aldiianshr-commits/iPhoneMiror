using iPhoneMiror.Connection.Usbmux;
using iPhoneMiror.Core.Abstractions;
using iPhoneMiror.Core.Plist;
using Microsoft.Extensions.Logging;

namespace iPhoneMiror.Connection.Pairing;

public interface IPairRecordStore
{
    Task<PairRecord?> LoadAsync(string udid, CancellationToken ct = default);
    Task SaveAsync(string udid, int muxDeviceId, PairRecord record, CancellationToken ct = default);
    Task DeleteLocalAsync(string udid);
}

/// <summary>
/// Pair records are read from Apple Mobile Device Service first (so existing iTunes/Apple Devices trust is reused),
/// then from the local, DPAPI-protected copy. New records are saved to both places.
/// </summary>
public sealed class PairRecordStore : IPairRecordStore
{
    private readonly IUsbmuxClient _usbmux;
    private readonly string _directory;
    private readonly IDataProtector? _protector;
    private readonly ILogger<PairRecordStore> _logger;

    public PairRecordStore(IUsbmuxClient usbmux, string directory, IDataProtector? protector, ILogger<PairRecordStore> logger)
    {
        _usbmux = usbmux;
        _directory = directory;
        _protector = protector;
        _logger = logger;
    }

    private string PathFor(string udid) => Path.Combine(_directory, SanitizeFileName(udid) + ".pairrecord");

    private static string SanitizeFileName(string s) => string.Concat(s.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_'));

    public async Task<PairRecord?> LoadAsync(string udid, CancellationToken ct = default)
    {
        try
        {
            var data = await _usbmux.ReadPairRecordAsync(udid, ct).ConfigureAwait(false);
            if (data is { Length: > 0 })
            {
                var record = PairRecord.FromBytes(data);
                if (record.HasHostPrivateKey) return record;
                _logger.LogDebug("Pair record from Apple Mobile Device Service for {Udid} lacks a host key.", udid);
            }
        }
        catch (Exception ex) when (ex is UsbmuxException or PlistFormatException)
        {
            _logger.LogDebug(ex, "ReadPairRecord via usbmux failed for {Udid}.", udid);
        }

        var path = PathFor(udid);
        if (!File.Exists(path)) return null;
        try
        {
            var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            if (_protector is not null) bytes = _protector.Unprotect(bytes);
            return PairRecord.FromBytes(bytes);
        }
        catch (Exception ex) when (ex is IOException or PlistFormatException or System.Security.Cryptography.CryptographicException)
        {
            _logger.LogWarning(ex, "Local pair record for {Udid} is unreadable.", udid);
            return null;
        }
    }

    public async Task SaveAsync(string udid, int muxDeviceId, PairRecord record, CancellationToken ct = default)
    {
        var bytes = record.ToBytes();
        try
        {
            await _usbmux.SavePairRecordAsync(udid, muxDeviceId, bytes, ct).ConfigureAwait(false);
        }
        catch (UsbmuxException ex)
        {
            _logger.LogWarning(ex, "Apple Mobile Device Service did not accept the pair record for {Udid}; keeping local copy only.", udid);
        }

        Directory.CreateDirectory(_directory);
        var data = _protector is not null ? _protector.Protect(bytes) : bytes;
        var path = PathFor(udid);
        await File.WriteAllBytesAsync(path + ".tmp", data, ct).ConfigureAwait(false);
        File.Move(path + ".tmp", path, overwrite: true);
    }

    public Task DeleteLocalAsync(string udid)
    {
        var path = PathFor(udid);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }
}
