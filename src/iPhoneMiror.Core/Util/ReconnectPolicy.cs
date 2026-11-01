namespace iPhoneMiror.Core.Util;

/// <summary>Exponential back-off with jitter for reconnect attempts.</summary>
public sealed class ReconnectPolicy
{
    private readonly TimeSpan _initial;
    private readonly TimeSpan _max;
    private readonly Random _random;

    public ReconnectPolicy(TimeSpan initial, TimeSpan max, Random? random = null)
    {
        if (initial <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(initial));
        if (max < initial) throw new ArgumentOutOfRangeException(nameof(max));
        _initial = initial;
        _max = max;
        _random = random ?? Random.Shared;
    }

    /// <summary>Delay before attempt number <paramref name="attempt"/> (0-based). Includes up to 20% jitter, never above max.</summary>
    public TimeSpan GetDelay(int attempt)
    {
        if (attempt < 0) attempt = 0;
        var exp = Math.Min(attempt, 20);
        var baseMs = Math.Min(_initial.TotalMilliseconds * Math.Pow(2, exp), _max.TotalMilliseconds);
        var jitter = baseMs * 0.2 * _random.NextDouble();
        return TimeSpan.FromMilliseconds(Math.Min(baseMs + jitter, _max.TotalMilliseconds));
    }
}
