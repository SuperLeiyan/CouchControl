namespace CouchControl;

public enum TouchpadGesture { None, Tap }

/// <summary>
/// Pure state machine turning DualSense touchpad samples into gestures:
///   tap — one short touch (≤ <see cref="TapMaxMs"/>) that doesn't move or click the pad
///
/// Touches that move (swipes), click the pad, or last longer produce nothing, so normal
/// touchpad clicks (mapped to Back) don't also trigger a tap. The tap fires on release.
/// </summary>
internal sealed class TouchpadGestureDetector
{
    public const int TapMaxMs = 300;
    public const int MoveTolerance = 150; // pad units (~8% of the 1920-wide pad)

    private bool _touching;
    private long _touchStart;
    private int _startX, _startY;
    private bool _moved, _clicked;

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
            return cleanTap ? TouchpadGesture.Tap : TouchpadGesture.None;
        }

        return TouchpadGesture.None;
    }
}
