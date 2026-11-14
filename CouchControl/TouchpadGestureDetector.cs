namespace CouchControl;

public enum TouchpadGesture { None, DoubleTap }

/// <summary>
/// Pure state machine turning DualSense touchpad samples into gestures:
///   double tap — two taps, the second starting within <see cref="DoubleTapGapMs"/> of the
///   first one's release. A tap is a short touch (≤ <see cref="TapMaxMs"/>) that doesn't move
///   or click the pad. A single tap does nothing (too easy to do by accident).
///
/// Touches that move (swipes), click the pad, or last longer produce nothing, so normal
/// touchpad clicks (mapped to Back) don't also count as taps. Fires on the second release.
/// </summary>
internal sealed class TouchpadGestureDetector
{
    public const int TapMaxMs = 300;
    public const int DoubleTapGapMs = 350;
    public const int MoveTolerance = 150; // pad units (~8% of the 1920-wide pad)

    private bool _touching;
    private long _touchStart;
    private int _startX, _startY;
    private bool _moved, _clicked;
    private long _lastTapAt = long.MinValue / 2; // release time of a tap waiting for its second

    public TouchpadGesture Update(bool touching, int x, int y, bool clicked, long nowMs)
    {
        if (touching && !_touching)
        {
            _touching = true;
            _touchStart = nowMs;
            _startX = x; _startY = y;
            _moved = false; _clicked = clicked;
            return TouchpadGesture.None;
        }

        if (touching)
        {
            _clicked |= clicked;
            if (Math.Abs(x - _startX) > MoveTolerance || Math.Abs(y - _startY) > MoveTolerance)
                _moved = true;
            return TouchpadGesture.None;
        }

        if (_touching) // released
        {
            _touching = false;
            _clicked |= clicked;
            bool cleanTap = !_moved && !_clicked && nowMs - _touchStart <= TapMaxMs;
            if (!cleanTap) { _lastTapAt = long.MinValue / 2; return TouchpadGesture.None; }
            if (_touchStart - _lastTapAt <= DoubleTapGapMs)
            {
                _lastTapAt = long.MinValue / 2; // a third tap starts a new pair
                return TouchpadGesture.DoubleTap;
            }
            _lastTapAt = nowMs;
            return TouchpadGesture.None;
        }

        return TouchpadGesture.None;
    }
}
