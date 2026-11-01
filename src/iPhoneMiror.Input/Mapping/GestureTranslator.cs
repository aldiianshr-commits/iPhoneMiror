using iPhoneMiror.Core.Abstractions;

namespace iPhoneMiror.Input.Mapping;

public abstract record Gesture;
public sealed record TapGesture(DevicePoint Point) : Gesture;
public sealed record DoubleTapGesture(DevicePoint Point) : Gesture;
public sealed record LongPressGesture(DevicePoint Point, TimeSpan Duration) : Gesture;
public sealed record SwipeGesture(DevicePoint From, DevicePoint To, TimeSpan Duration) : Gesture;

public sealed class GestureOptions
{
    public double DragThresholdPoints { get; init; } = 8;
    public TimeSpan LongPressThreshold { get; init; } = TimeSpan.FromMilliseconds(550);
    public TimeSpan DoubleTapWindow { get; init; } = TimeSpan.FromMilliseconds(300);
    public double DoubleTapMaxDistancePoints { get; init; } = 20;
    public bool DetectDoubleTap { get; init; } = true;
    /// <summary>Finger travel (points) per mouse-wheel notch (120 units).</summary>
    public double ScrollPointsPerNotch { get; init; } = 120;
    public TimeSpan WheelCoalesceWindow { get; init; } = TimeSpan.FromMilliseconds(90);
}

/// <summary>
/// Turns mouse events (already mapped to device points) into touch gestures. Deterministic: callers pass timestamps
/// and periodically call <see cref="Flush"/> so pending single taps and coalesced wheel scrolls are emitted.
/// </summary>
public sealed class GestureTranslator
{
    private readonly GestureOptions _o;
    private DevicePoint? _downPoint;
    private DateTimeOffset _downTime;
    private DevicePoint _lastMovePoint;
    private (DevicePoint Point, DateTimeOffset Time)? _pendingTap;
    private double _wheelAccum;
    private DevicePoint _wheelPoint;
    private DateTimeOffset _lastWheel;

    public GestureTranslator(GestureOptions? options = null)
    {
        _o = options ?? new GestureOptions();
    }

    public (double Width, double Height) DeviceSize { get; set; } = (390, 844);

    public bool IsPressed => _downPoint is not null;

    public void OnPressed(DevicePoint p, DateTimeOffset time)
    {
        _downPoint = p;
        _downTime = time;
        _lastMovePoint = p;
    }

    public void OnMoved(DevicePoint p)
    {
        if (_downPoint is not null) _lastMovePoint = p;
    }

    public IReadOnlyList<Gesture> OnReleased(DevicePoint? p, DateTimeOffset time)
    {
        var result = new List<Gesture>();
        if (_downPoint is not { } start) return result;
        var end = p ?? _lastMovePoint;
        _downPoint = null;
        var duration = time - _downTime;

        if (Distance(start, end) > _o.DragThresholdPoints)
        {
            result.AddRange(FlushPendingTap());
            result.Add(new SwipeGesture(start, end, duration < TimeSpan.FromMilliseconds(80) ? TimeSpan.FromMilliseconds(80) : duration));
            return result;
        }

        if (duration >= _o.LongPressThreshold)
        {
            result.AddRange(FlushPendingTap());
            result.Add(new LongPressGesture(start, duration));
            return result;
        }

        if (!_o.DetectDoubleTap)
        {
            result.Add(new TapGesture(start));
            return result;
        }

        if (_pendingTap is { } pending && time - pending.Time <= _o.DoubleTapWindow && Distance(pending.Point, start) <= _o.DoubleTapMaxDistancePoints)
        {
            _pendingTap = null;
            result.Add(new DoubleTapGesture(pending.Point));
            return result;
        }

        result.AddRange(FlushPendingTap());
        _pendingTap = (start, time);
        return result;
    }

    /// <param name="delta">Wheel delta (positive = wheel rotated away from the user = scroll up).</param>
    public IReadOnlyList<Gesture> OnWheel(double delta, DevicePoint at, DateTimeOffset time)
    {
        var result = new List<Gesture>();
        if (_wheelAccum != 0 && (Math.Sign(delta) != Math.Sign(_wheelAccum) || Distance(at, _wheelPoint) > 40))
        {
            var g = BuildWheelSwipe();
            if (g is not null) result.Add(g);
        }
        if (_wheelAccum == 0) _wheelPoint = at;
        _wheelAccum += delta;
        _lastWheel = time;
        return result;
    }

    /// <summary>Emits a pending single tap after the double-tap window, and coalesced wheel scrolls.</summary>
    public IReadOnlyList<Gesture> Flush(DateTimeOffset now)
    {
        var result = new List<Gesture>();
        if (_pendingTap is { } pending && now - pending.Time > _o.DoubleTapWindow)
        {
            _pendingTap = null;
            result.Add(new TapGesture(pending.Point));
        }
        if (_wheelAccum != 0 && now - _lastWheel >= _o.WheelCoalesceWindow)
        {
            var g = BuildWheelSwipe();
            if (g is not null) result.Add(g);
        }
        return result;
    }

    private IEnumerable<Gesture> FlushPendingTap()
    {
        if (_pendingTap is { } pending)
        {
            _pendingTap = null;
            yield return new TapGesture(pending.Point);
        }
    }

    private SwipeGesture? BuildWheelSwipe()
    {
        var notches = _wheelAccum / 120.0;
        _wheelAccum = 0;
        if (Math.Abs(notches) < 0.01) return null;
        var (w, h) = DeviceSize;
        var travel = Math.Clamp(notches * _o.ScrollPointsPerNotch, -h * 0.6, h * 0.6);
        var fromY = Math.Clamp(_wheelPoint.Y, h * 0.2, h * 0.8);
        // Wheel up (positive) reveals content above: the finger moves down.
        var toY = Math.Clamp(fromY + travel, 5, h - 5);
        var x = Math.Clamp(_wheelPoint.X, 5, w - 5);
        if (Math.Abs(toY - fromY) < 1) return null;
        return new SwipeGesture(new DevicePoint(x, fromY), new DevicePoint(x, toY), TimeSpan.FromMilliseconds(180));
    }

    private static double Distance(DevicePoint a, DevicePoint b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
}
