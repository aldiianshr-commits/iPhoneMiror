namespace iPhoneMiror.Mirroring;

/// <summary>Frames-per-second over a sliding window.</summary>
public sealed class FrameRateMeter
{
    private readonly Queue<DateTimeOffset> _times = new();
    private readonly TimeSpan _window;

    public FrameRateMeter(TimeSpan? window = null)
    {
        _window = window ?? TimeSpan.FromSeconds(2);
    }

    public void Add(DateTimeOffset t)
    {
        lock (_times)
        {
            _times.Enqueue(t);
            Trim(t);
        }
    }

    public double GetFps(DateTimeOffset now)
    {
        lock (_times)
        {
            Trim(now);
            return _times.Count / _window.TotalSeconds;
        }
    }

    private void Trim(DateTimeOffset now)
    {
        while (_times.Count > 0 && now - _times.Peek() > _window) _times.Dequeue();
    }

    public void Reset()
    {
        lock (_times) _times.Clear();
    }
}
