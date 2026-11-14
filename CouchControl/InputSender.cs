using System.Runtime.InteropServices;

namespace CouchControl;

public enum MouseButton { Left, Right, Middle, X1, X2 }

/// <summary>Destination for desktop-mode output. Abstracted so the mapper can be tested.</summary>
internal interface IInputSink
{
    void MouseMove(int dx, int dy);
    void MouseButton(MouseButton button, bool down);
    void Wheel(int delta, bool horizontal);
    void Keys(IReadOnlyList<ushort> virtualKeys, bool down);
}

/// <summary>Sends mouse and keyboard input to Windows via SendInput.</summary>
internal sealed class InputSender : IInputSink
{
    public static readonly InputSender Instance = new();

    public void MouseMove(int dx, int dy)
    {
        if (dx == 0 && dy == 0) return;
        Send(Mouse(MOUSEEVENTF_MOVE, dx, dy, 0));
    }

    public void MouseButton(MouseButton button, bool down)
    {
        var (flags, data) = button switch
        {
            CouchControl.MouseButton.Left   => (down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP, 0u),
            CouchControl.MouseButton.Right  => (down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP, 0u),
            CouchControl.MouseButton.Middle => (down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP, 0u),
            CouchControl.MouseButton.X1     => (down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP, XBUTTON1),
            _                               => (down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP, XBUTTON2),
        };
        Send(Mouse(flags, 0, 0, data));
    }

    public void Wheel(int delta, bool horizontal)
    {
        if (delta == 0) return;
        Send(Mouse(horizontal ? MOUSEEVENTF_HWHEEL : MOUSEEVENTF_WHEEL, 0, 0, unchecked((uint)delta)));
    }

    /// <summary>Presses keys in order, or releases them in reverse order.</summary>
    public void Keys(IReadOnlyList<ushort> virtualKeys, bool down)
    {
        if (virtualKeys.Count == 0) return;
        var inputs = new INPUT[virtualKeys.Count];
        for (int i = 0; i < virtualKeys.Count; i++)
        {
            ushort vk = virtualKeys[down ? i : virtualKeys.Count - 1 - i];
            uint flags = (down ? 0 : KEYEVENTF_KEYUP) | (IsExtended(vk) ? KEYEVENTF_EXTENDEDKEY : 0);
            inputs[i] = new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion
                {
                    ki = new KEYBDINPUT { wVk = vk, wScan = (ushort)MapVirtualKey(vk, 0), dwFlags = flags }
                }
            };
        }
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    /// <summary>Types one character, independent of the keyboard layout.</summary>
    public void Text(char c)
    {
        var inputs = new INPUT[2];
        for (int i = 0; i < 2; i++)
        {
            inputs[i] = new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion
                {
                    ki = new KEYBDINPUT { wVk = 0, wScan = c, dwFlags = KEYEVENTF_UNICODE | (i == 1 ? KEYEVENTF_KEYUP : 0) }
                }
            };
        }
        SendInput(2, inputs, Marshal.SizeOf<INPUT>());
    }

    private static bool IsExtended(ushort vk) => vk switch
    {
        0x21 or 0x22 or 0x23 or 0x24 => true,           // PgUp, PgDn, End, Home
        0x25 or 0x26 or 0x27 or 0x28 => true,           // arrows
        0x2D or 0x2E => true,                           // Insert, Delete
        0x5B or 0x5C or 0x5D => true,                   // LWin, RWin, Apps
        0x6F or 0x90 => true,                           // Numpad /, NumLock
        0xA3 or 0xA5 => true,                           // RCtrl, RAlt
        >= 0xA6 and <= 0xB7 => true,                    // browser / volume / media / launch keys
        _ => false
    };

    private static INPUT Mouse(uint flags, int dx, int dy, uint data) => new()
    {
        type = INPUT_MOUSE,
        u = new InputUnion { mi = new MOUSEINPUT { dx = dx, dy = dy, mouseData = data, dwFlags = flags } }
    };

    private static void Send(INPUT input) => SendInput(1, [input], Marshal.SizeOf<INPUT>());

    // ── Interop ───────────────────────────────────────────────────────────

    private const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    private const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004,
        MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010, MOUSEEVENTF_MIDDLEDOWN = 0x0020,
        MOUSEEVENTF_MIDDLEUP = 0x0040, MOUSEEVENTF_XDOWN = 0x0080, MOUSEEVENTF_XUP = 0x0100,
        MOUSEEVENTF_WHEEL = 0x0800, MOUSEEVENTF_HWHEEL = 0x1000;
    private const uint XBUTTON1 = 1, XBUTTON2 = 2;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001, KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_UNICODE = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion u; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);
}
