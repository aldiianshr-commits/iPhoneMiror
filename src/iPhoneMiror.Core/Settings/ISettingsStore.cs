namespace iPhoneMiror.Core.Settings;

public interface ISettingsStore
{
    AppSettings Current { get; }
    event EventHandler<AppSettings>? Changed;
    Task LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(CancellationToken cancellationToken = default);
    Task UpdateAsync(Action<AppSettings> mutate, CancellationToken cancellationToken = default);
}
