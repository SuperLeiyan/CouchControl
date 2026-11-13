namespace CouchControl;

public enum TouchpadGesture { None, LongTouch, DoubleTap }

/// <summary>
/// Pure state machine turning DualSense touchpad samples into gestures:
///   long touch — one finger held still for <see cref="LongTouchMs"/> without clicking
///   double tap — two short, still taps with ≤ <see cref="DoubleTapGapMs"/> between them
///
/// Touches that move (swipes), click the pad, or sit between a tap and a long touch
/// produce nothing, so normal touchpad clicks (mapped to Back) don't misfire.
/// The long touch fires while the finger is still down when reports keep arriving,
/// or on release otherwise (some virtual pads only report on change).
/// </summary>
internal sealed class TouchpadGestureDetector
{
    public const int LongTouchMs = 700;
    public const int TapMaxMs = 300;
    public const int DoubleTapGapMs = 400;
    public const int MoveTolerance = 150; // pad units (~8% of the 1920-wide pad)

    private bool _touching;
    private long _touchStart;
    private int _startX, _startY;
    private bool _moved, _clicked, _longFired;
    private bool _tapPending;
    private long _lastTapEnd;
    private bool _secondTapCandidate;

    public TouchpadGesture Update(bool touching, int x, int y, bool clicked, long nowMs)
    {
        if (touching && !_touching)
        {
            _touching = true;
            _touchStart = nowMs;
            _startX = x; _startY = y;
            _moved = false; _clicked = clicked; _longFired = false;
            _secondTapCandidate = _tapPending && nowMs - _lastTapEnd <= DoubleTapGapMs;
            _tapPending = false;
            return TouchpadGesture.None;
        }

        if (touching)
        {
            _clicked |= clicked;
            if (Math.Abs(x - _startX) > MoveTolerance || Math.Abs(y - _startY) > MoveTolerance)
                _moved = true;

            if (IsLongTouchCandidate(nowMs))
            {
                _longFired = true;
                return TouchpadGesture.LongTouch;
            }
            return TouchpadGesture.None;
        }

        if (_touching) // released
        {
            _touching = false;
            _clicked |= clicked;
            long held = nowMs - _touchStart;

            if (IsLongTouchCandidate(nowMs))
            {
                _longFired = true;
                return TouchpadGesture.LongTouch;
            }

            bool cleanTap = !_longFired && !_moved && !_clicked && held <= TapMaxMs;
            if (!cleanTap) return TouchpadGesture.None;

            if (_secondTapCandidate)
            {
                _secondTapCandidate = false;
                return TouchpadGesture.DoubleTap;
            }

            _tapPending = true;
            _lastTapEnd = nowMs;
        }

        return TouchpadGesture.None;
    }

    private bool IsLongTouchCandidate(long nowMs)
        => !_longFired && !_moved && !_clicked && nowMs - _touchStart >= LongTouchMs;
}
