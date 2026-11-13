using FsCheck;
using SharpDX.DirectInput;
using System.Reflection;

namespace CouchControl.Tests;

/// <summary>
/// Plain data record mirroring the JoystickState fields used by ControllerBridge.BuildReport.
/// Used as the generator input type since JoystickState has no public parameterized constructor.
/// </summary>
public record JoystickStateData(
    int X,
    int Y,
    int RotationX,
    int RotationY,
    int Z,
    bool[] Buttons,       // length 11
    int[] PointOfViewControllers); // length 1, -1 = centered

/// <summary>
/// FsCheck generators for test input types.
/// </summary>
public static class Generators
{
    private static readonly Gen<int> AxisGen = Gen.Choose(0, 65535);
    private static readonly Gen<bool> BoolGen = Arb.Generate<bool>();

    public static Arbitrary<JoystickStateData> JoystickStateArbitrary()
    {
        var gen =
            from x  in AxisGen
            from y  in AxisGen
            from rx in AxisGen
            from ry in AxisGen
            from z  in AxisGen
            from b0  in BoolGen from b1  in BoolGen from b2  in BoolGen
            from b3  in BoolGen from b4  in BoolGen from b5  in BoolGen
            from b6  in BoolGen from b7  in BoolGen from b8  in BoolGen
            from b9  in BoolGen from b10 in BoolGen
            // POV: -1 (centered) or one of the 8 cardinal/diagonal angles in hundredths of degrees
            from pov in Gen.Elements(-1, 0, 4500, 9000, 13500, 18000, 22500, 27000, 31500)
            select new JoystickStateData(
                X: x, Y: y, RotationX: rx, RotationY: ry, Z: z,
                Buttons: [b0, b1, b2, b3, b4, b5, b6, b7, b8, b9, b10],
                PointOfViewControllers: [pov]);

        return gen.ToArbitrary();
    }

    /// <summary>
    /// Converts a <see cref="JoystickStateData"/> to a real <see cref="JoystickState"/>
    /// via reflection so it can be passed to ControllerBridge.BuildReport.
    /// </summary>
    public static JoystickState ToJoystickState(JoystickStateData data)
    {
        var state = new JoystickState();
        SetField(state, "<X>k__BackingField",                      data.X);
        SetField(state, "<Y>k__BackingField",                      data.Y);
        SetField(state, "<RotationX>k__BackingField",              data.RotationX);
        SetField(state, "<RotationY>k__BackingField",              data.RotationY);
        SetField(state, "<Z>k__BackingField",                      data.Z);
        SetField(state, "<Buttons>k__BackingField",                data.Buttons);
        SetField(state, "<PointOfViewControllers>k__BackingField", data.PointOfViewControllers);
        return state;
    }

    private static void SetField(object obj, string name, object value)
    {
        var field = obj.GetType().GetField(name,
            BindingFlags.NonPublic | BindingFlags.Instance);
        field?.SetValue(obj, value);
    }
}
