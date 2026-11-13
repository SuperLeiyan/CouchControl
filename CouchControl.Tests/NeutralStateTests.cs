using Nefarius.ViGEm.Client.Targets.Xbox360;
using CouchControl;
using Xunit;

namespace CouchControl.Tests;

public class NeutralStateTests
{
    // Feature: x-mute-controller-bridge, Property 4: Neutral state report is always fully zeroed
    // SendNeutralState applies ControllerState.Neutral — verify the default struct is fully zeroed.
    [Fact]
    public void NeutralState_DefaultConstruct_AllAxesZero()
    {
        var state = ControllerState.Neutral;

        Assert.Equal(0, state.LeftThumbX);
        Assert.Equal(0, state.LeftThumbY);
        Assert.Equal(0, state.RightThumbX);
        Assert.Equal(0, state.RightThumbY);
        Assert.Equal((byte)0, state.LeftTrigger);
        Assert.Equal((byte)0, state.RightTrigger);
    }

    [Fact]
    public void NeutralState_DefaultConstruct_NoButtonsSet()
    {
        var state = ControllerState.Neutral;

        Assert.False(state.A);
        Assert.False(state.B);
        Assert.False(state.X);
        Assert.False(state.Y);
        Assert.False(state.LeftShoulder);
        Assert.False(state.RightShoulder);
        Assert.False(state.Back);
        Assert.False(state.Start);
        Assert.False(state.LeftThumb);
        Assert.False(state.RightThumb);
        Assert.False(state.Guide);
        Assert.False(state.DPadUp);
        Assert.False(state.DPadDown);
        Assert.False(state.DPadLeft);
        Assert.False(state.DPadRight);
    }
}
