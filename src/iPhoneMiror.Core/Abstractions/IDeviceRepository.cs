using iPhoneMiror.Core.Models;

namespace iPhoneMiror.Core.Abstractions;

public interface IDeviceRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task UpsertAsync(DeviceInfo device, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DeviceRecord>> GetRecentAsync(int limit = 20, CancellationToken cancellationToken = default);
    Task<DeviceRecord?> GetAsync(string udid, CancellationToken cancellationToken = default);
    Task SetAutoReconnectAsync(string udid, bool enabled, CancellationToken cancellationToken = default);
    Task RemoveAsync(string udid, CancellationToken cancellationToken = default);
    Task AddEventAsync(string udid, string kind, string? detail, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ConnectionEvent>> GetEventsAsync(string? udid, int limit = 100, CancellationToken cancellationToken = default);
}
