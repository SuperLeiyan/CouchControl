using FsCheck;
using SharpDX.XInput;

namespace CouchControl.Tests;

/// <summary>
/// FsCheck generators for test input types.
/// </summary>
public static class Generators
{
    private static readonly Gen<short> AxisGen =
        Gen.Choose(short.MinValue, short.MaxValue).Select(v => (short)v);

    private static readonly Gen<byte> TriggerGen =
        Gen.Choose(0, 255).Select(v => (byte)v);

    // Every button flag XInput can report (Guide isn't exposed by XInput's public API).
    private static readonly GamepadButtonFlags[] AllButtons =
    [
        GamepadButtonFlags.DPadUp, GamepadButtonFlags.DPadDown,
        GamepadButtonFlags.DPadLeft, GamepadButtonFlags.DPadRight,
        GamepadButtonFlags.Start, GamepadButtonFlags.Back,
        GamepadButtonFlags.LeftThumb, GamepadButtonFlags.RightThumb,
        GamepadButtonFlags.LeftShoulder, GamepadButtonFlags.RightShoulder,
        GamepadButtonFlags.A, GamepadButtonFlags.B,
        GamepadButtonFlags.X, GamepadButtonFlags.Y,
    ];

    private static readonly Gen<GamepadButtonFlags> ButtonsGen =
        Gen.ArrayOf(AllButtons.Length, Arb.Generate<bool>()).Select(bits =>
        {
            var flags = GamepadButtonFlags.None;
            for (int i = 0; i < AllButtons.Length; i++)
                if (bits[i]) flags |= AllButtons[i];
            return flags;
        });

    public static Arbitrary<Gamepad> GamepadArbitrary()
    {
        var gen =
            from lx in AxisGen
            from ly in AxisGen
            from rx in AxisGen
            from ry in AxisGen
            from lt in TriggerGen
            from rt in TriggerGen
            from buttons in ButtonsGen
            select new Gamepad
            {
                LeftThumbX = lx, LeftThumbY = ly,
                RightThumbX = rx, RightThumbY = ry,
                LeftTrigger = lt, RightTrigger = rt,
                Buttons = buttons,
            };

        return gen.ToArbitrary();
    }
}
