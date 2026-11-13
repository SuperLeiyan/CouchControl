using Nefarius.Utilities.DeviceManagement.PnP;
using SharpDX.XInput;

namespace CouchControl;

/// <summary>
/// Makes sure the virtual controller gets XInput slot 0 (player 1), which many games
/// read exclusively. XInput gives a new controller the first free slot, so when a
/// physical pad already holds slot 0, its device is disabled while the virtual pad
/// connects and re-enabled straight after; it then reconnects into a later slot.
///
/// Disabled devices are recorded in a state file so they are re-enabled on the next
/// start if CouchControl exits before re-enabling them.
/// </summary>
internal static class SlotZeroClaimer
{
    private static string StateFile => Path.Combine(AppPaths.DataDir, "slot0-disabled.txt");

    /// <summary>
    /// Call right before connecting the virtual controller. Returns the device instance
    /// IDs that were disabled; pass them to <see cref="Restore"/> once it's connected.
    /// </summary>
    public static List<string> FreeSlotZero()
    {
        RestoreStale();

        var disabled = new List<string>();
        if (!new Controller(UserIndex.One).IsConnected) return disabled;

        var ids = HidHideManager.FindPhysicalControllers()
            .Where(d => d.kind != "DualSense")
            .Select(d => d.id)
            .ToList();
        if (ids.Count == 0)
        {
            AppLogger.Log("Slot 0 is taken by a controller CouchControl doesn't manage; virtual controller will use a later slot.");
            return disabled;
        }

        SaveState(ids);
        foreach (var id in ids)
        {
            try
            {
                if (PnPDevice.GetDeviceByInstanceId(id, DeviceLocationFlags.Normal) is PnPDevice device)
                {
                    device.Disable();
                    disabled.Add(id);
                }
            }
            catch (Exception ex)
            {
                // Children of an already-disabled device disappear; that's expected.
                AppLogger.Log($"Slot 0: could not disable {id}: {ex.Message}");
            }
        }
        SaveState(disabled);

        // Wait for XInput to release slot 0.
        for (int i = 0; i < 30 && new Controller(UserIndex.One).IsConnected; i++)
            Thread.Sleep(100);

        AppLogger.Log(new Controller(UserIndex.One).IsConnected
            ? "Slot 0: still taken after disabling physical controllers."
            : $"Slot 0: freed for the virtual controller (temporarily disabled {disabled.Count} device(s)).");
        return disabled;
    }

    /// <summary>Re-enables devices disabled by <see cref="FreeSlotZero"/>.</summary>
    public static void Restore(IReadOnlyList<string> ids)
    {
        if (ids.Count == 0) return;
        // Parents first (reverse of the disable order), so children re-appear before we touch them.
        foreach (var id in ids.Reverse())
            Enable(id);
        DeleteState();
        AppLogger.Log("Slot 0: re-enabled physical controllers.");
    }

    private static void RestoreStale()
    {
        try
        {
            if (!File.Exists(StateFile)) return;
            var ids = File.ReadAllLines(StateFile).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            foreach (var id in ids) Enable(id);
            if (ids.Count > 0) AppLogger.Log($"Slot 0: re-enabled {ids.Count} device(s) left disabled by a previous run.");
            DeleteState();
        }
        catch (Exception ex)
        {
            AppLogger.Log($"Slot 0: stale cleanup failed: {ex.Message}");
        }
    }

    private static void Enable(string id)
    {
        try
        {
            if (PnPDevice.GetDeviceByInstanceId(id, DeviceLocationFlags.Phantom) is PnPDevice device)
                device.Enable();
        }
        catch (Exception ex)
        {
            AppLogger.Log($"Slot 0: could not re-enable {id}: {ex.Message}");
        }
    }

    private static void SaveState(IEnumerable<string> ids)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllLines(StateFile, ids);
        }
        catch { }
    }

    private static void DeleteState()
    {
        try { File.Delete(StateFile); } catch { }
    }
}
