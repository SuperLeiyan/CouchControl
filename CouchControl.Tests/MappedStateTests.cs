using FsCheck;
using FsCheck.Xunit;
using SharpDX.XInput;
using CouchControl;
using Xunit;

namespace CouchControl.Tests;

public class MappedStateTests
{
    // Mapped state faithfully reflects the physical XInput controller state.
    [Property]
    public Property MappedStateFaithfullyReflectsPhysicalControllerState()
    {
        return Prop.ForAll(
            Generators.GamepadArbitrary(),
            gp =>
            {
                var report = ControllerBridge.BuildReport(gp);
                var b = gp.Buttons;

                bool sticksOk =
                    report.LeftThumbX  == ControllerBridge.ApplyDeadzone(gp.LeftThumbX)  &&
                    report.LeftThumbY  == ControllerBridge.ApplyDeadzone(gp.LeftThumbY)  &&
                    report.RightThumbX == ControllerBridge.ApplyDeadzone(gp.RightThumbX) &&
                    report.RightThumbY == ControllerBridge.ApplyDeadzone(gp.RightThumbY);

                bool triggersOk =
                    report.LeftTrigger  == gp.LeftTrigger &&
                    report.RightTrigger == gp.RightTrigger;

                bool buttonsOk =
                    report.A             == b.HasFlag(GamepadButtonFlags.A)             &&
                    report.B             == b.HasFlag(GamepadButtonFlags.B)             &&
                    report.X             == b.HasFlag(GamepadButtonFlags.X)             &&
                    report.Y             == b.HasFlag(GamepadButtonFlags.Y)             &&
                    report.LeftShoulder  == b.HasFlag(GamepadButtonFlags.LeftShoulder)  &&
                    report.RightShoulder == b.HasFlag(GamepadButtonFlags.RightShoulder) &&
                    report.Back          == b.HasFlag(GamepadButtonFlags.Back)          &&
                    report.Start         == b.HasFlag(GamepadButtonFlags.Start)         &&
                    report.LeftThumb     == b.HasFlag(GamepadButtonFlags.LeftThumb)     &&
                    report.RightThumb    == b.HasFlag(GamepadButtonFlags.RightThumb)    &&
                    report.Guide         == false;

                bool dpadOk =
                    report.DPadUp    == b.HasFlag(GamepadButtonFlags.DPadUp)   &&
                    report.DPadDown  == b.HasFlag(GamepadButtonFlags.DPadDown) &&
                    report.DPadLeft  == b.HasFlag(GamepadButtonFlags.DPadLeft) &&
                    report.DPadRight == b.HasFlag(GamepadButtonFlags.DPadRight);

                return sticksOk && triggersOk && buttonsOk && dpadOk;
            });
    }

    [Fact]
    public void BuildReport_IdleGamepad_IsNeutral()
    {
        Assert.Equal(ControllerState.Neutral, ControllerBridge.BuildReport(default));
    }

    [Fact]
    public void BuildReport_DPadDiagonal_SetsBothDirections()
    {
        var gp = new Gamepad { Buttons = GamepadButtonFlags.DPadUp | GamepadButtonFlags.DPadRight };
        var report = ControllerBridge.BuildReport(gp);
        Assert.True(report.DPadUp); Assert.True(report.DPadRight);
        Assert.False(report.DPadDown); Assert.False(report.DPadLeft);
    }
}
