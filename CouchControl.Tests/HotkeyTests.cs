using FsCheck;
using FsCheck.Xunit;
using SharpDX.DirectInput;
using System.Reflection;
using CouchControl;
using Xunit;

namespace CouchControl.Tests;

public class HotkeyTests
{
    // Helper: construct a JoystickState with specific button values via reflection.
    // JoystickState has no public constructor with parameters, so we use reflection
    // to set the backing fields.
    private static JoystickState MakeState(bool lb = false, bool start = false)
    {
        var state = new JoystickState();
        var buttons = new bool[11];
        buttons[4] = lb;    // LB = index 4
        buttons[7] = start; // Start = index 7
        SetField(state, "<Buttons>k__BackingField", buttons);
        SetField(state, "<PointOfViewControllers>k__BackingField", new int[] { -1 });
        return state;
    }

    private static void SetField(object obj, string fieldName, object value)
    {
        var field = obj.GetType().GetField(fieldName,
            BindingFlags.NonPublic | BindingFlags.Instance);
        field?.SetValue(obj, value);
    }

    // Feature: x-mute-controller-bridge, Property 5: Hotkey toggles IsMuted exactly once per leading edge
    [Property]
    public Property HotkeyTogglesExactlyOncePerLeadingEdge()
    {
        // Generate sequences of (lb, start) bool pairs
        var pairGen = Gen.Two(Arb.Generate<bool>());
        var seqGen = Gen.ListOf(pairGen);

        return Prop.ForAll(seqGen.ToArbitrary(), pairs =>
        {
            var bridge = new ControllerBridge();
            int toggleCount = 0;
            int leadingEdgeCount = 0;
            bool prevCombo = false;
            bool prevMuted = bridge.IsMuted;

            foreach (var (lb, start) in pairs)
            {
                bool combo = lb && start;

                // Count leading edges in the input sequence
                if (combo && !prevCombo)
                    leadingEdgeCount++;

                prevCombo = combo;

                bridge.CheckHotkey(MakeState(lb, start));

                // Count actual toggles
                if (bridge.IsMuted != prevMuted)
                    toggleCount++;

                prevMuted = bridge.IsMuted;
            }

            return toggleCount == leadingEdgeCount;
        });
    }

    [Fact]
    public void Hotkey_SinglePressAndRelease_TogglesOnce()
    {
        var bridge = new ControllerBridge();
        bool initial = bridge.IsMuted;

        bridge.CheckHotkey(MakeState(lb: true, start: true));   // press
        bridge.CheckHotkey(MakeState(lb: false, start: false));  // release

        Assert.NotEqual(initial, bridge.IsMuted);
    }

    [Fact]
    public void Hotkey_HeldFor5Ticks_TogglesOnce()
    {
        var bridge = new ControllerBridge();
        bool initial = bridge.IsMuted;

        for (int i = 0; i < 5; i++)
            bridge.CheckHotkey(MakeState(lb: true, start: true));

        Assert.NotEqual(initial, bridge.IsMuted);
    }

    [Fact]
    public void Hotkey_PressReleasePressAgain_TogglesTwice()
    {
        var bridge = new ControllerBridge();
        bool initial = bridge.IsMuted;

        bridge.CheckHotkey(MakeState(lb: true, start: true));   // press → toggle 1
        bridge.CheckHotkey(MakeState(lb: false, start: false));  // release
        bridge.CheckHotkey(MakeState(lb: true, start: true));   // press → toggle 2

        Assert.Equal(initial, bridge.IsMuted); // toggled twice = back to original
    }

    [Fact]
    public void Hotkey_NeitherButtonPressed_IsMutedUnchanged()
    {
        var bridge = new ControllerBridge();
        bool initial = bridge.IsMuted;

        bridge.CheckHotkey(MakeState(lb: false, start: false));

        Assert.Equal(initial, bridge.IsMuted);
    }

    [Fact]
    public void Hotkey_OnlyLbPressed_IsMutedUnchanged()
    {
        var bridge = new ControllerBridge();
        bool initial = bridge.IsMuted;

        bridge.CheckHotkey(MakeState(lb: true, start: false));

        Assert.Equal(initial, bridge.IsMuted);
    }

    [Fact]
    public void Hotkey_OnlyStartPressed_IsMutedUnchanged()
    {
        var bridge = new ControllerBridge();
        bool initial = bridge.IsMuted;

        bridge.CheckHotkey(MakeState(lb: false, start: true));

        Assert.Equal(initial, bridge.IsMuted);
    }
}
