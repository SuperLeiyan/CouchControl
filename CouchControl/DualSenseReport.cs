namespace CouchControl;

/// <summary>
/// Pure parser that converts a raw DualSense HID input report into an Xbox-style
/// <see cref="ControllerState"/>.
///
/// Supported report formats (byte 0 is the report ID, as returned by ReadFile):
///   USB  0x01 (64 bytes)  — full report: LX LY RX RY L2 R2 seq B0 B1 B2 ...
///   BT   0x31 (78 bytes)  — full report, same layout shifted by one byte
///   BT   0x01 (10+ bytes) — "simple" report used before the controller is switched
///                           to full mode: LX LY RX RY B0 B1 B2 L2 R2
///
/// Button bytes:
///   B0: bits 0-3 hat (0=N … 7=NW, 8=centered), 4 Square, 5 Cross, 6 Circle, 7 Triangle
///   B1: 0 L1, 1 R1, 2 L2 (digital), 3 R2 (digital), 4 Create, 5 Options, 6 L3, 7 R3
///   B2: 0 PS, 1 Touchpad click, 2 Mic mute
/// </summary>
internal static class DualSenseReport
{
    public const ushort SonyVendorId = 0x054C;
    public const ushort DualSenseProductId = 0x0CE6;
    public const ushort DualSenseEdgeProductId = 0x0DF2;

    public static bool TryParse(ReadOnlySpan<byte> report, bool bluetooth, out ControllerState state)
    {
        state = default;
        if (report.Length < 10) return false;

        int sticks, triggers, buttons;
        switch (report[0])
        {
            case 0x31 when report.Length >= 12:         // BT full
                sticks = 2; triggers = 6; buttons = 9;
                break;
            case 0x01 when !bluetooth && report.Length >= 11: // USB full
                sticks = 1; triggers = 5; buttons = 8;
                break;
            case 0x01 when bluetooth:                    // BT simple
                sticks = 1; buttons = 5; triggers = 8;
                break;
            default:
                return false;
        }

        byte b0 = report[buttons];
        byte b1 = report[buttons + 1];
        byte b2 = report[buttons + 2];
        int hat = b0 & 0x0F;

        state = new ControllerState(
            LeftThumbX:    ScaleStick(report[sticks],     invert: false),
            LeftThumbY:    ScaleStick(report[sticks + 1], invert: true),
            RightThumbX:   ScaleStick(report[sticks + 2], invert: false),
            RightThumbY:   ScaleStick(report[sticks + 3], invert: true),
            LeftTrigger:   report[triggers],
            RightTrigger:  report[triggers + 1],
            A:             (b0 & 0x20) != 0,   // Cross
            B:             (b0 & 0x40) != 0,   // Circle
            X:             (b0 & 0x10) != 0,   // Square
            Y:             (b0 & 0x80) != 0,   // Triangle
            LeftShoulder:  (b1 & 0x01) != 0,   // L1
            RightShoulder: (b1 & 0x02) != 0,   // R1
            Back:          (b1 & 0x10) != 0 || (b2 & 0x02) != 0, // Create or touchpad click
            Start:         (b1 & 0x20) != 0,   // Options
            LeftThumb:     (b1 & 0x40) != 0,   // L3
            RightThumb:    (b1 & 0x80) != 0,   // R3
            Guide:         (b2 & 0x01) != 0,   // PS
            DPadUp:        hat is 7 or 0 or 1,
            DPadDown:      hat is 3 or 4 or 5,
            DPadLeft:      hat is 5 or 6 or 7,
            DPadRight:     hat is 1 or 2 or 3);
        return true;
    }

    /// <summary>
    /// Reads the first touchpad contact. Only full reports carry touch data
    /// (USB 0x01 → touch at byte 33, BT 0x31 → byte 34); the BT simple report doesn't.
    /// Each touch point is 4 bytes: [contact: bit7 = 1 means NOT touching]
    /// [x low 8] [x high 4 | y low 4] [y high 8]. Pad range is about 1920 × 1080.
    /// Also returns whether the touchpad is physically clicked.
    /// </summary>
    public static bool TryParseTouch(ReadOnlySpan<byte> report, bool bluetooth,
        out bool touching, out int x, out int y, out bool clicked)
    {
        touching = false; x = 0; y = 0; clicked = false;

        int touch, buttons;
        if (report.Length >= 42 && report[0] == 0x31) { touch = 34; buttons = 9; }
        else if (report.Length >= 41 && report[0] == 0x01 && !bluetooth) { touch = 33; buttons = 8; }
        else return false;

        touching = (report[touch] & 0x80) == 0;
        x = report[touch + 1] | ((report[touch + 2] & 0x0F) << 8);
        y = (report[touch + 2] >> 4) | (report[touch + 3] << 4);
        clicked = (report[buttons + 2] & 0x02) != 0;
        return true;
    }

    /// <summary>
    /// Maps a DualSense stick byte (0..255, 128 ≈ center, Y grows downward) onto the
    /// full Xbox short range. Y axes are inverted because XInput Y grows upward.
    /// </summary>
    internal static short ScaleStick(byte value, bool invert)
    {
        int scaled = value * 257 - 32768;          // 0 → -32768, 255 → 32767
        return invert ? (short)(-1 - scaled) : (short)scaled; // bitwise-symmetric inversion
    }
}
