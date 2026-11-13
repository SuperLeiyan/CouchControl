using FsCheck;
using FsCheck.Xunit;
using CouchControl;
using Xunit;

namespace CouchControl.Tests;

public class AxisScalingTests
{
    // Feature: x-mute-controller-bridge, Property 1: Axis scaling output is always in the valid short range
    [Property]
    public Property AxisScalingOutputRangeAndSign()
    {
        return Prop.ForAll(
            Gen.Choose(0, 65535).ToArbitrary(),
            raw =>
            {
                var output = ControllerBridge.ScaleAxis(raw);
                
                // Output must be in valid short range
                var inRange = output >= short.MinValue && output <= short.MaxValue;
                
                // For values outside deadzone, sign should match sign of (raw - 32767)
                const int DeadzoneRaw = 3276;
                const int RawMid = 32767;
                var centered = raw - RawMid;
                
                bool signCorrect = true;
                if (Math.Abs(centered) >= DeadzoneRaw)
                {
                    // Outside deadzone: sign should match
                    if (centered > 0)
                        signCorrect = output > 0;
                    else if (centered < 0)
                        signCorrect = output < 0;
                }
                
                return inRange && signCorrect;
            });
    }

    // Feature: x-mute-controller-bridge, Property 2: Deadzone zeroes near-center axis values
    [Property]
    public Property DeadzoneZeroesNearCenterValues()
    {
        const int RawMid = 32767;
        const int DeadzoneRaw = 3276;
        
        return Prop.ForAll(
            Gen.Choose(RawMid - DeadzoneRaw, RawMid + DeadzoneRaw).ToArbitrary(),
            raw =>
            {
                var output = ControllerBridge.ScaleAxis(raw);
                return output == 0;
            });
    }

    [Fact]
    public void ScaleAxis_MinValue_ReturnsMinimumNegativeShort()
    {
        var result = ControllerBridge.ScaleAxis(0);
        Assert.True(result <= -32000); // Should be close to -32768
    }

    [Fact]
    public void ScaleAxis_MaxValue_ReturnsMaximumPositiveShort()
    {
        var result = ControllerBridge.ScaleAxis(65535);
        Assert.Equal(32767, result);
    }

    [Fact]
    public void ScaleAxis_ExactCenter_ReturnsZero()
    {
        var result = ControllerBridge.ScaleAxis(32767);
        Assert.Equal(0, result);
    }

    [Fact]
    public void ScaleAxis_LowerDeadzoneBoundary_ReturnsZero()
    {
        // Deadzone is exclusive: |centered| < 3276 returns 0.
        // At exactly 32767 - 3276 = 29491, centered = -3276, which is NOT < 3276, so non-zero.
        // One unit inside the boundary (32767 - 3275 = 29492) should return 0.
        var result = ControllerBridge.ScaleAxis(32767 - 3275);
        Assert.Equal(0, result);
    }

    [Fact]
    public void ScaleAxis_UpperDeadzoneBoundary_ReturnsZero()
    {
        // One unit inside the upper boundary (32767 + 3275 = 36042) should return 0.
        var result = ControllerBridge.ScaleAxis(32767 + 3275);
        Assert.Equal(0, result);
    }
}
