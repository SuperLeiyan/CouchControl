using System.Runtime.InteropServices;
using Nefarius.Utilities.DeviceManagement.PnP;
using SharpDX.XInput;

namespace CouchControl;

/// <summary>
/// Makes sure the virtual controller gets XInput slot 0 (player 1), which many games
/// read exclusively. XInput gives a new controller the first free slot, so when a
/// physical pad already holds slot 0, it is taken off the system while the virtual pad
/// connects and brought back straight after; it then reconnects into a later slot.
///
/// Devices are disabled where Windows allows it. Some can't be disabled (e.g. the HID
/// node of a Bluetooth Xbox controller, CR_NOT_DISABLEABLE); those are removed instead
/// and come back when their parent is re-enumerated, like "Scan for hardware changes".
///
/// What was done is recorded in a state file so it's undone on the next start if
/// CouchControl exits in between.
/// </summary>
internal static class SlotZeroClaimer
{
    private static string StateFile => Path.Combine(AppPaths.DataDir, "slot0-state.txt");
    private static string OldStateFile => Path.Combine(AppPaths.DataDir, "slot0-disabled.txt");

    /// <summary>One device taken off the system: disabled, or removed (with its parent to re-scan).</summary>
    internal readonly record struct Change(bool Removed, string InstanceId, string? ParentId)
    {
        public override string ToString() => $"{(Removed ? "remove" : "disable")}|{InstanceId}|{ParentId}";

        public static Change? Parse(string line)
        {
            var p = line.Split('|');
            if (p.Length == 1 && p[0].Length > 0) return new Change(false, p[0], null); // old format
            if (p.Length < 2 || p[1].Length == 0) return null;
            return new Change(p[0] == "remove", p[1], p.Length > 2 && p[2].Length > 0 ? p[2] : null);
        }
    }

    private static bool SlotZeroTaken => new Controller(UserIndex.One).IsConnected;

    /// <summary>
    /// Call right before connecting the virtual controller. Returns what was changed;
    /// pass it to <see cref="Restore"/> once the virtual controller is connected.
    /// </summary>
    public static List<Change> FreeSlotZero()
    {
        RestoreStale();

        var changes = new List<Change>();
        if (!SlotZeroTaken) return changes;

        var ids = HidHideManager.FindPhysicalControllers()
            .Where(d => d.kind != "DualSense")
            .Select(d => d.id)
            .ToList();
        if (ids.Count == 0)
        {
            AppLogger.Log("Slot 0: taken by a controller CouchControl doesn't manage; the virtual controller will use a later slot.");
            return changes;
        }

        foreach (var id in ids)
        {
            if (TryTakeOffline(id) is { } change)
            {
                changes.Add(change);
                SaveState(changes);
            }
        }
        if (changes.Count == 0) return changes;

        // Wait for XInput to release slot 0.
        for (int i = 0; i < 30 && SlotZeroTaken; i++)
            Thread.Sleep(100);

        AppLogger.Log(SlotZeroTaken
            ? "Slot 0: still taken after taking physical controllers offline."
            : $"Slot 0: freed for the virtual controller ({string.Join(", ", changes.Select(c => c.Removed ? "removed" : "disabled"))} {changes.Count} device(s)).");
        return changes;
    }

    private static Change? TryTakeOffline(string id)
    {
        PnPDevice? device;
        try { device = PnPDevice.GetDeviceByInstanceId(id, DeviceLocationFlags.Normal) as PnPDevice; }
        catch { return null; } // gone already (e.g. a child of a device handled earlier)
        if (device is null) return null;

        try
        {
            device.Disable();
            return new Change(false, id, null);
        }
        catch (Exception ex)
        {
            AppLogger.Log($"Slot 0: can't disable {id} ({ex.Message}); removing it instead.");
        }

        string? parent = null;
        try { parent = device.Parent?.InstanceId; } catch { }
        try
        {
            device.Remove();
            return new Change(true, id, parent);
        }
        catch (Exception ex)
        {
            AppLogger.Log($"Slot 0: could not remove {id}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Brings back devices taken offline by <see cref="FreeSlotZero"/>.</summary>
    public static void Restore(IReadOnlyList<Change> changes)
    {
        if (changes.Count == 0) return;
        Undo(changes);
        DeleteState();
        AppLogger.Log("Slot 0: physical controllers brought back.");
    }

    private static void Undo(IEnumerable<Change> changes)
    {
        bool anyRemoved = false;
        foreach (var c in changes.Reverse())
        {
            if (c.Removed)
            {
                anyRemoved = true;
                if (c.ParentId != null) Reenumerate(c.ParentId);
            }
            else
            {
                try
                {
                    if (PnPDevice.GetDeviceByInstanceId(c.InstanceId, DeviceLocationFlags.Phantom) is PnPDevice device)
                        device.Enable();
                }
                catch (Exception ex)
                {
                    AppLogger.Log($"Slot 0: could not re-enable {c.InstanceId}: {ex.Message}");
                }
            }
        }
        if (anyRemoved && !Devcon.Refresh())
            AppLogger.Log("Slot 0: hardware re-scan failed.");
    }

    private static void RestoreStale()
    {
        try
        {
            var lines = new List<string>();
            foreach (var f in new[] { StateFile, OldStateFile })
                if (File.Exists(f)) lines.AddRange(File.ReadAllLines(f));
            var changes = lines.Select(l => Change.Parse(l.Trim())).OfType<Change>().ToList();
            if (changes.Count > 0)
            {
                Undo(changes);
                AppLogger.Log($"Slot 0: brought back {changes.Count} device(s) left offline by a previous run.");
            }
            DeleteState();
        }
        catch (Exception ex)
        {
            AppLogger.Log($"Slot 0: stale cleanup failed: {ex.Message}");
        }
    }

    private static void Reenumerate(string instanceId)
    {
        if (CM_Locate_DevNodeW(out uint devInst, instanceId, 0) == 0)
        {
            int cr = CM_Reenumerate_DevNode(devInst, 0);
            if (cr != 0) AppLogger.Log($"Slot 0: re-scan of {instanceId} failed (CONFIGRET {cr}).");
        }
    }

    private static void SaveState(IEnumerable<Change> changes)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllLines(StateFile, changes.Select(c => c.ToString()));
        }
        catch { }
    }

    private static void DeleteState()
    {
        try { File.Delete(StateFile); } catch { }
        try { File.Delete(OldStateFile); } catch { }
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Reenumerate_DevNode(uint dnDevInst, uint ulFlags);
}
