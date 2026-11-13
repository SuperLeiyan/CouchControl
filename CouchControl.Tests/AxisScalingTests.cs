using FsCheck;
using FsCheck.Xunit;
using CouchControl;
using Xunit;

namespace CouchControl.Tests;

public class AxisScalingTests
{
    private const short Deadzone = ControllerBridge.DeadzoneRaw;

    private static readonly Gen<short> AnyShort =
        Gen.Choose(short.MinValue, short.MaxValue).Select(v => (short)v);

    // Values strictly inside (-Deadzone, Deadzone) are zeroed.
    [Property]
    public Property DeadzoneZeroesNearCenterValues()
    {
        return Prop.ForAll(
            Gen.Choose(-Deadzone + 1, Deadzone - 1).ToArbitrary(),
            raw => ControllerBridge.ApplyDeadzone((short)raw) == 0);
    }

    // Values at or beyond the deadzone pass through unchanged.
    [Property]
    public Property OutsideDeadzoneValuesPassThrough()
    {
        return Prop.ForAll(
            AnyShort.Where(v => v <= -Deadzone || v >= Deadzone).ToArbitrary(),
            raw => ControllerBridge.ApplyDeadzone(raw) == raw);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(Deadzone - 1, 0)]
    [InlineData(-Deadzone + 1, 0)]
    [InlineData(Deadzone, Deadzone)]
    [InlineData(-Deadzone, -Deadzone)]
    [InlineData(short.MaxValue, short.MaxValue)]
    [InlineData(short.MinValue, short.MinValue)]
    public void ApplyDeadzone_KnownInputs(short raw, short expected)
    {
        Assert.Equal(expected, ControllerBridge.ApplyDeadzone(raw));
    }
}
