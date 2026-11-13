namespace CouchControl;

/// <summary>
/// Desktop mode settings, stored under "DesktopMode" in config.json.
///
/// Button names: A B X Y LB RB LT RT Back Start LS RS DPadUp DPadDown DPadLeft DPadRight
/// (DualSense: Cross=A, Circle=B, Square=X, Triangle=Y, Create/touchpad click=Back, Options=Start).
///
/// Actions:
///   LeftClick, RightClick, MiddleClick, X1Click, X2Click — held while the button is held
///   TouchKeyboard, ShowDesktop                            — fire once on press
///   None                                                  — do nothing
///   anything else is a key combo, e.g. "Esc", "Enter", "Win+Tab", "Ctrl+W", "BrowserBack"
/// </summary>
public sealed class DesktopModeSettings
{
    /// <summary>Master switch for the built-in desktop mode.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Buttons pressed together to switch between game and desktop mode. In desktop mode,
    /// these buttons' own actions fire on release instead of press, so the chord doesn't
    /// trigger them.
    /// </summary>
    public string ToggleChord { get; set; } = "Back+Start";

    public bool StartInDesktopMode { get; set; } = false;
    public bool PlaySoundOnSwitch { get; set; } = true;

    /// <summary>Cursor speed in pixels per second at full stick deflection.</summary>
    public double PointerSpeed { get; set; } = 1800;

    /// <summary>Response curve exponent: 1 = linear, higher = finer control near center.</summary>
    public double PointerCurve { get; set; } = 2.2;

    /// <summary>Radial stick dead zone as a fraction of full deflection (0–0.5).</summary>
    public double StickDeadzone { get; set; } = 0.15;

    /// <summary>Wheel units per second at full deflection (120 = one notch).</summary>
    public double ScrollSpeed { get; set; } = 1500;

    public string LeftStick { get; set; } = "Mouse";   // Mouse | Scroll | None
    public string RightStick { get; set; } = "Scroll"; // Mouse | Scroll | None

    /// <summary>
    /// Defaults use the same actions as the JoyXoff desktop profile this replaces. JoyXoff's
    /// file doesn't say which face button is which, so the face-button positions are a best guess.
    /// </summary>
    public Dictionary<string, string> Buttons { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["A"] = "LeftClick",
        ["B"] = "RightClick",
        ["X"] = "Esc",
        ["Y"] = "Enter",
        ["LB"] = "Win+Tab",
        ["RB"] = "Win",
        ["LT"] = "BrowserBack",
        ["RT"] = "BrowserForward",
        ["Back"] = "Win+Down",
        ["Start"] = "Win+Up",
        ["DPadLeft"] = "Left",
        ["DPadRight"] = "Right",
        ["DPadDown"] = "Down",
        ["DPadUp"] = "Up",
        ["LS"] = "Ctrl+W",
        ["RS"] = "MiddleClick",
    };
}

/// <summary>A parsed button action.</summary>
internal abstract record ButtonAction
{
    public sealed record Mouse(MouseButton Button) : ButtonAction;
    public sealed record Keys(IReadOnlyList<ushort> VirtualKeys) : ButtonAction;
    public sealed record OneShot(string Name) : ButtonAction;

    public static ButtonAction? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();
        switch (t.ToLowerInvariant())
        {
            case "none": return null;
            case "leftclick": return new Mouse(MouseButton.Left);
            case "rightclick": return new Mouse(MouseButton.Right);
            case "middleclick": return new Mouse(MouseButton.Middle);
            case "x1click": return new Mouse(MouseButton.X1);
            case "x2click": return new Mouse(MouseButton.X2);
            case "touchkeyboard": return new OneShot("TouchKeyboard");
            case "showdesktop": return new OneShot("ShowDesktop");
        }

        var vks = new List<ushort>();
        foreach (var part in t.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var vk = KeyNames.Lookup(part);
            if (vk == null) return null;
            vks.Add(vk.Value);
        }
        return vks.Count > 0 ? new Keys(vks) : null;
    }
}

/// <summary>Key name → Windows virtual-key code.</summary>
internal static class KeyNames
{
    private static readonly Dictionary<string, ushort> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Esc"] = 0x1B, ["Escape"] = 0x1B, ["Enter"] = 0x0D, ["Return"] = 0x0D, ["Tab"] = 0x09,
        ["Space"] = 0x20, ["Backspace"] = 0x08, ["Delete"] = 0x2E, ["Del"] = 0x2E, ["Insert"] = 0x2D,
        ["Home"] = 0x24, ["End"] = 0x23, ["PageUp"] = 0x21, ["PageDown"] = 0x22,
        ["Left"] = 0x25, ["Up"] = 0x26, ["Right"] = 0x27, ["Down"] = 0x28,
        ["Ctrl"] = 0xA2, ["Control"] = 0xA2, ["Shift"] = 0xA0, ["Alt"] = 0xA4, ["Win"] = 0x5B, ["Apps"] = 0x5D,
        ["PrintScreen"] = 0x2C, ["CapsLock"] = 0x14,
        ["BrowserBack"] = 0xA6, ["BrowserForward"] = 0xA7, ["BrowserRefresh"] = 0xA8, ["BrowserHome"] = 0xAC,
        ["VolumeMute"] = 0xAD, ["VolumeDown"] = 0xAE, ["VolumeUp"] = 0xAF,
        ["MediaNext"] = 0xB0, ["MediaPrev"] = 0xB1, ["MediaStop"] = 0xB2, ["MediaPlayPause"] = 0xB3,
    };

    public static ushort? Lookup(string name)
    {
        if (Map.TryGetValue(name, out var vk)) return vk;
        if (name.Length == 1)
        {
            char c = char.ToUpperInvariant(name[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9') return c;
        }
        if (name.Length is 2 or 3 && (name[0] is 'F' or 'f') && int.TryParse(name[1..], out int f) && f is >= 1 and <= 24)
            return (ushort)(0x70 + f - 1);
        return null;
    }
}
