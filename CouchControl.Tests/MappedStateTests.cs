using FsCheck;
using FsCheck.Xunit;
using CouchControl;
using Xunit;

namespace CouchControl.Tests;

public class MappedStateTests
{
    // Feature: x-mute-controller-bridge, Property 6: Mapped state faithfully reflects physical controller state
    [Property]
    public Property MappedStateFaithfullyReflectsPhysicalControllerState()
    {
        return Prop.ForAll(
            Generators.JoystickStateArbitrary(),
            data =>
            {
                var jsState = Generators.ToJoystickState(data);
                var report = ControllerBridge.BuildReport(jsState);

                // Sticks: LeftThumbX == ScaleAxis(X), LeftThumbY == -ScaleAxis(Y)
                bool lxOk = report.LeftThumbX  == ControllerBridge.ScaleAxis(data.X);
                bool lyOk = report.LeftThumbY  == (short)-ControllerBridge.ScaleAxis(data.Y);
                bool rxOk = report.RightThumbX == ControllerBridge.ScaleAxis(data.RotationX);
                bool ryOk = report.RightThumbY == (short)-ControllerBridge.ScaleAxis(data.RotationY);

                // Buttons: each index 0-10 maps to the correct field
                bool[] b = data.Buttons;
                bool buttonsOk =
                    report.A             == b[0]  &&
                    report.B             == b[1]  &&
                    report.X             == b[2]  &&
                    report.Y             == b[3]  &&
                    report.LeftShoulder  == b[4]  &&
                    report.RightShoulder == b[5]  &&
                    report.Back          == b[6]  &&
                    report.Start         == b[7]  &&
                    report.LeftThumb     == b[8]  &&
                    report.RightThumb    == b[9]  &&
                    report.Guide         == b[10];

                // D-Pad: matches MapDpad output
                var (up, right, down, left) = ControllerBridge.MapDpad(data.PointOfViewControllers);
                bool dpadOk =
                    report.DPadUp    == up    &&
                    report.DPadRight == right &&
                    report.DPadDown  == down  &&
                    report.DPadLeft  == left;

                return lxOk && lyOk && rxOk && ryOk && buttonsOk && dpadOk;
            });
    }

    // D-Pad boundary unit tests
    [Fact]
    public void MapDpad_Centered_NoDPadButtonsSet()
    {
        var (up, right, down, left) = ControllerBridge.MapDpad([-1]);
        Assert.False(up); Assert.False(right); Assert.False(down); Assert.False(left);
    }

    [Fact]
    public void MapDpad_Angle0_UpOnly()
    {
        var (up, right, down, left) = ControllerBridge.MapDpad([0]);
        Assert.True(up); Assert.False(right); Assert.False(down); Assert.False(left);
    }

    [Fact]
    public void MapDpad_Angle9000_RightOnly()
    {
        var (up, right, down, left) = ControllerBridge.MapDpad([9000]);
        Assert.False(up); Assert.True(right); Assert.False(down); Assert.False(left);
    }

    [Fact]
    public void MapDpad_Angle18000_DownOnly()
    {
        var (up, right, down, left) = ControllerBridge.MapDpad([18000]);
        Assert.False(up); Assert.False(right); Assert.True(down); Assert.False(left);
    }

    [Fact]
    public void MapDpad_Angle27000_LeftOnly()
    {
        var (up, right, down, left) = ControllerBridge.MapDpad([27000]);
        Assert.False(up); Assert.False(right); Assert.False(down); Assert.True(left);
    }

    [Fact]
    public void MapDpad_Angle4500_UpAndRight()
    {
        var (up, right, down, left) = ControllerBridge.MapDpad([4500]);
        Assert.True(up); Assert.True(right); Assert.False(down); Assert.False(left);
    }

    [Fact]
    public void MapDpad_EmptyPov_NoDPadButtonsSet()
    {
        var (up, right, down, left) = ControllerBridge.MapDpad([]);
        Assert.False(up); Assert.False(right); Assert.False(down); Assert.False(left);
    }
}
