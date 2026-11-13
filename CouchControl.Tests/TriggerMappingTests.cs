using FsCheck;
using FsCheck.Xunit;
using CouchControl;
using Xunit;

namespace CouchControl.Tests;

public class TriggerMappingTests
{
    private const int RawMid = 32767;

    // Replicates the trigger split logic from ControllerBridge.BuildReport
    private static (byte lt, byte rt) ComputeTriggers(int z)
    {
        byte lt = z < RawMid ? (byte)ControllerBridge.Map(RawMid - z, 0, RawMid, 0, 255) : (byte)0;
        byte rt = z > RawMid ? (byte)ControllerBridge.Map(z - RawMid, 0, RawMid, 0, 255) : (byte)0;
        return (lt, rt);
    }

    // Feature: x-mute-controller-bridge, Property 3: Trigger mapping produces valid, mutually exclusive byte values
    [Property]
    public Property TriggerByteRangeAndMutualExclusivity()
    {
        return Prop.ForAll(
            Gen.Choose(0, 65535).ToArbitrary(),
            z =>
            {
                var (lt, rt) = ComputeTriggers(z);

                // Both must be in valid byte range [0, 255]
                bool ltInRange = lt >= 0 && lt <= 255;
                bool rtInRange = rt >= 0 && rt <= 255;

                // At most one can be non-zero (mutual exclusivity)
                bool mutuallyExclusive = !(lt > 0 && rt > 0);

                return ltInRange && rtInRange && mutuallyExclusive;
            });
    }

    [Fact]
    public void Triggers_ZAxisMin_LeftTriggerMax_RightTriggerZero()
    {
        var (lt, rt) = ComputeTriggers(0);
        Assert.Equal(255, lt);
        Assert.Equal(0, rt);
    }

    [Fact]
    public void Triggers_ZAxisMax_LeftTriggerZero_RightTriggerMax()
    {
        var (lt, rt) = ComputeTriggers(65535);
        Assert.Equal(0, lt);
        Assert.Equal(255, rt);
    }

    [Fact]
    public void Triggers_ZAxisMidpoint_BothZero()
    {
        var (lt, rt) = ComputeTriggers(32767);
        Assert.Equal(0, lt);
        Assert.Equal(0, rt);
    }
}
