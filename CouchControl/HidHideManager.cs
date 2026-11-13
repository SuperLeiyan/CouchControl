using System.Diagnostics;
using System.Text.RegularExpressions;
using Nefarius.Drivers.HidHide;
using Nefarius.Utilities.DeviceManagement.PnP;

namespace CouchControl;

/// <summary>
/// Everything CouchControl does with HidHide:
///
///  • Allow list — adds CouchControl and JoyXoff so they can see hidden controllers.
///    Existing entries are never removed.
///  • Physical controllers (when "Manage HidHide" is on) — hides real Xbox/XInput pads
///    and DualSense controllers so games only see CouchControl's virtual controller.
///
/// Only instance IDs CouchControl added itself are tracked as "owned". Owned entries stay
/// hidden after CouchControl exits: HidHide can't take a device away from an app that
/// opened it while it was visible (Windows components do this within moments), so
/// un-hiding between runs would let them read the physical pad past the mute again.
/// They're re-adopted on the next start, and un-hidden when "Manage HidHide" is turned
/// off. Devices the user hid manually are left alone.
/// </summary>
internal static class HidHideManager
{
    // XInput-capable devices have "IG_" in their interface path; ViGEm virtual pads don't.
    private static readonly Regex XInputPath = new(@"[&#_]IG_[0-9A-F]{2}", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DualSensePath = new(@"054c[_&]pid[_&](0ce6|0df2)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly object Sync = new();
    private static readonly HashSet<string> OwnedDevices = new(StringComparer.OrdinalIgnoreCase);
    private static bool _usable;

    public static bool IsUsable => _usable;

    /// <summary>
    /// Call once at startup before creating any ViGEm devices: clears stale owned entries
    /// and, when <paramref name="manage"/> is set, makes sure the allow list is correct.
    /// </summary>
    public static void Initialize(bool manage)
    {
        lock (Sync)
        {
            LoadOwnedEntries(keep: manage);

            try
            {
                var svc = new HidHideControlService();
                if (!svc.IsInstalled)
                {
                    AppLogger.Log("HidHide: not installed. Games will see all controllers directly.");
                    return;
                }
                if (svc.IsAppListInverted)
                {
                    if (!manage || File.Exists(AppPaths.HidHideBackupFile))
                    {
                        // Either automation is off, or we converted before and the user
                        // deliberately switched back. Respect that.
                        AppLogger.Log("HidHide: application list is inverted (block-list mode); CouchControl won't manage HidHide.");
                        return;
                    }
                    ConvertFromInverted(svc);
                }

                if (manage)
                {
                    EnsureAllowed(svc, Environment.ProcessPath);

                    if (!svc.IsActive)
                    {
                        svc.IsActive = true;
                        AppLogger.Log("HidHide: device hiding was disabled; enabled it.");
                    }
                }

                _usable = IsSelfAllowed(svc);
                if (!_usable)
                    AppLogger.Log("HidHide: CouchControl is not on the application list, so it won't hide anything. " +
                        "Turn on 'Manage HidHide' or add CouchControl.exe in HidHide Configuration Client.");
            }
            catch (Exception ex)
            {
                AppLogger.Log($"HidHide: initialization failed: {ex.Message}");
            }
        }
    }

    // ── Physical controllers ──────────────────────────────────────────────

    /// <summary>
    /// Raised (outside the lock, on the caller's thread) with the kinds of controllers that
    /// were just hidden while already connected. Windows may have opened them while they
    /// were visible, so they need reconnecting once for the hiding to fully take effect.
    /// </summary>
    public static event Action<IReadOnlyList<string>>? ConnectedControllersHidden;

    /// <summary>Hides any present physical XInput pad or DualSense not already hidden.</summary>
    public static void HidePhysicalControllers()
    {
        var newlyHidden = new List<string>();
        HidePhysicalControllersLocked(newlyHidden);
        if (newlyHidden.Count > 0)
            ConnectedControllersHidden?.Invoke(newlyHidden.Distinct().ToList());
    }

    private static void HidePhysicalControllersLocked(List<string> newlyHidden)
    {
        lock (Sync)
        {
            if (!_usable) return;
            try
            {
                var svc = new HidHideControlService();
                var blocked = new HashSet<string>(svc.BlockedInstanceIds, StringComparer.OrdinalIgnoreCase);
                foreach (var (id, kind) in FindPhysicalControllers())
                {
                    if (blocked.Contains(id)) continue;
                    // Record ownership before hiding, so a crash in between can't leave an
                    // entry hidden that CouchControl doesn't know about.
                    OwnedDevices.Add(id);
                    SaveOwned();
                    svc.AddBlockedInstanceId(id);
                    newlyHidden.Add(kind.StartsWith("Xbox", StringComparison.Ordinal) ? "Xbox controller" : kind);
                    AppLogger.Log($"HidHide: hid {kind} {id} from games.");
                }
            }
            catch (Exception ex)
            {
                // This runs every 2 s. HidHide's driver allows one client at a time, so it fails
                // while e.g. the HidHide Configuration Client is open; log once until it recovers.
                if (!_hideFailureLogged)
                {
                    _hideFailureLogged = true;
                    AppLogger.Log($"HidHide: hiding physical controllers failed: {ex.Message.Split('\n')[0].Trim()} " +
                        "(Is the HidHide Configuration Client open? Close it so CouchControl can manage HidHide.) Retrying quietly.");
                }
                return;
            }
            if (_hideFailureLogged)
            {
                _hideFailureLogged = false;
                AppLogger.Log("HidHide: access restored.");
            }
        }
    }

    private static bool _hideFailureLogged;

    /// <summary>Un-hides the physical controllers CouchControl hid (not the user's own entries).</summary>
    public static void ReleasePhysicalControllers()
    {
        lock (Sync)
        {
            foreach (var id in OwnedDevices.ToList())
                Release(id, OwnedDevices);
        }
    }

    private static void ReleaseAllLocked()
    {
        foreach (var id in OwnedDevices.ToList()) Release(id, OwnedDevices);
    }

    /// <summary>
    /// Physical controllers to hide from games:
    ///   • every XInput (XUSB) device — hides it from XInput games
    ///   • XInput-compatible HID devices ("IG_" in the path) — the DirectInput/HID view
    ///     of the same pads
    ///   • DualSense HID devices (USB, Bluetooth, or USB/IP from a stream host)
    /// Anything under ViGEmBus is skipped: that's CouchControl's own virtual controllers
    /// (and their DirectInput children) or another app's, and games must keep seeing them.
    /// </summary>
    internal static IEnumerable<(string id, string kind)> FindPhysicalControllers()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<(string, string)>();

        foreach (var (_, id) in Enumerate(DeviceInterfaceIds.XUsbDevice))
            if (seen.Add(id) && !IsUnderViGEmBus(id))
                results.Add((id, "Xbox controller"));

        foreach (var (path, id) in Enumerate(DeviceInterfaceIds.HidDevice))
        {
            string? kind = DualSensePath.IsMatch(path) ? "DualSense"
                : XInputPath.IsMatch(path) ? "Xbox controller (HID)"
                : null;
            if (kind != null && seen.Add(id) && !IsUnderViGEmBus(id))
                results.Add((id, kind));
        }
        return results;
    }

    /// <summary>True if the device or any ancestor is driven by the ViGEmBus service.</summary>
    internal static bool IsUnderViGEmBus(string instanceId)
    {
        try
        {
            IPnPDevice? device = PnPDevice.GetDeviceByInstanceId(instanceId, DeviceLocationFlags.Normal);
            for (int depth = 0; device != null && depth < 8; depth++)
            {
                var service = device.GetProperty<string>(DevicePropertyKey.Device_Service);
                if (string.Equals(service, "ViGEmBus", StringComparison.OrdinalIgnoreCase))
                    return true;
                device = device.Parent;
            }
            return false;
        }
        catch
        {
            return true; // can't tell — err on the side of not hiding
        }
    }

    // ── Internals ─────────────────────────────────────────────────────────


    private static void Release(string instanceId, HashSet<string> owner)
    {
        if (!owner.Contains(instanceId)) return;
        try
        {
            var svc = new HidHideControlService();
            if (svc.IsInstalled) svc.RemoveBlockedInstanceId(instanceId);
            owner.Remove(instanceId);
            AppLogger.Log($"HidHide: un-hid {instanceId}.");
        }
        catch (Exception ex)
        {
            // Keep ownership: the entry stays in the state file, so the next start's
            // stale-entry cleanup un-hides it.
            AppLogger.Log($"HidHide: unblock {instanceId} failed: {ex.Message.Split('\n')[0].Trim()} Will retry on next start.");
        }
        SaveOwned();
    }

    /// <summary>
    /// Reads the entries a previous run hid. With <paramref name="keep"/> (Manage HidHide on)
    /// they're adopted again and stay hidden; otherwise they're un-hidden. Entries that now
    /// belong to a ViGEm virtual pad (instance IDs can be reused) and X-Mute's old feed
    /// entry are always removed.
    /// </summary>
    private static void LoadOwnedEntries(bool keep)
    {
        var owned = new List<string>();
        var stale = new List<string>();
        try
        {
            if (File.Exists(AppPaths.HidHideStateFile))
                owned.AddRange(File.ReadAllLines(AppPaths.HidHideStateFile));
            if (File.Exists(AppPaths.LegacyHidHideFeedFile))
                stale.AddRange(File.ReadAllLines(AppPaths.LegacyHidHideFeedFile));
        }
        catch { }

        foreach (var id in owned.Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (keep && !IsPresentViGEmDevice(id)) OwnedDevices.Add(id);
            else stale.Add(id);
        }
        if (OwnedDevices.Count > 0)
            AppLogger.Log($"HidHide: keeping {OwnedDevices.Count} controller(s) hidden from the previous run.");

        stale = stale.Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (stale.Count > 0)
        {
            try
            {
                var svc = new HidHideControlService();
                if (svc.IsInstalled)
                    foreach (var id in stale)
                    {
                        svc.RemoveBlockedInstanceId(id);
                        AppLogger.Log($"HidHide: removed stale entry {id}.");
                    }
            }
            catch (Exception ex)
            {
                AppLogger.Log($"HidHide: stale cleanup failed: {ex.Message}");
                return; // keep the files so we retry next start
            }
        }

        // Rewrite (never just delete) the state file, so a crash or kill from here on still
        // leaves a record of every entry we own.
        SaveOwned();
        try { if (File.Exists(AppPaths.LegacyHidHideFeedFile)) File.Delete(AppPaths.LegacyHidHideFeedFile); }
        catch { }
    }

    /// <summary>
    /// True only if the device is present and driven by ViGEmBus (i.e. the instance ID now
    /// belongs to a virtual pad). Unlike <see cref="IsUnderViGEmBus"/>, a device that isn't
    /// present (a switched-off controller) counts as false.
    /// </summary>
    private static bool IsPresentViGEmDevice(string instanceId)
    {
        try { PnPDevice.GetDeviceByInstanceId(instanceId, DeviceLocationFlags.Normal); }
        catch { return false; }
        return IsUnderViGEmBus(instanceId);
    }

    private static void SaveOwned()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllLines(AppPaths.HidHideStateFile, OwnedDevices);
        }
        catch { }
    }

    private static void EnsureAllowed(HidHideControlService svc, string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        if (svc.ApplicationPaths.Any(p => PathEquals(p, path))) return;
        svc.AddApplicationPath(path, false);
        AppLogger.Log($"HidHide: added {path} to the application list.");
    }

    private static bool IsSelfAllowed(HidHideControlService svc)
    {
        var self = Environment.ProcessPath ?? "";
        return svc.ApplicationPaths.Any(p => PathEquals(p, self));
    }

    // ── Inverted (block-list) mode conversion ─────────────────────────────

    private sealed record HidHideBackup(DateTime SavedAt, bool AppListInverted, bool Active, List<string> ApplicationPaths);

    /// <summary>
    /// Process names that were on HidHide's block list before CouchControl switched it to
    /// normal mode. The tray app merges these into Auto-Mute Processes.
    /// </summary>
    public static IReadOnlyList<string> ConvertedBlockListProcesses { get; private set; } = [];

    public static bool HasBackup => File.Exists(AppPaths.HidHideBackupFile);

    /// <summary>
    /// Backs up the current settings, then switches HidHide from block-list mode to normal
    /// (allow-list) mode with an empty list. CouchControl adds itself right after.
    /// </summary>
    private static void ConvertFromInverted(HidHideControlService svc)
    {
        var paths = svc.ApplicationPaths.ToList();
        var backup = new HidHideBackup(DateTime.Now, true, svc.IsActive, paths);
        Directory.CreateDirectory(AppPaths.DataDir);
        File.WriteAllText(AppPaths.HidHideBackupFile,
            System.Text.Json.JsonSerializer.Serialize(backup, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

        svc.ClearApplicationsList();
        svc.IsAppListInverted = false;

        var skip = new[] { "CouchControl", "X-Mute", "Joyxoff", "JoyxSvc" };
        ConvertedBlockListProcesses = paths
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .Where(n => n.Length > 0 && !skip.Contains(n, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        AppLogger.Log($"HidHide: switched from block-list mode to normal mode. Backup saved to {AppPaths.HidHideBackupFile}. " +
            $"Former block list ({paths.Count}): {string.Join(", ", paths)}");
    }

    /// <summary>
    /// Restores the HidHide settings saved before the conversion and deletes the backup.
    /// Returns false if there's no backup or the restore failed.
    /// </summary>
    public static bool RestoreBackup()
    {
        lock (Sync)
        {
            try
            {
                if (!File.Exists(AppPaths.HidHideBackupFile)) return false;
                var backup = System.Text.Json.JsonSerializer.Deserialize<HidHideBackup>(
                    File.ReadAllText(AppPaths.HidHideBackupFile));
                if (backup == null) return false;

                ReleaseAllLocked();
                var svc = new HidHideControlService();
                svc.ClearApplicationsList();
                foreach (var p in backup.ApplicationPaths)
                    svc.AddApplicationPath(p, false);
                svc.IsAppListInverted = backup.AppListInverted;
                svc.IsActive = backup.Active;
                _usable = false;

                File.Delete(AppPaths.HidHideBackupFile);
                AppLogger.Log($"HidHide: restored settings from {backup.SavedAt:g} ({backup.ApplicationPaths.Count} apps, inverted={backup.AppListInverted}).");
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"HidHide: restore failed: {ex.Message}");
                return false;
            }
        }
    }

    private static IEnumerable<(string path, string instanceId)> Enumerate(Guid interfaceGuid)
    {
        var results = new List<(string, string)>();
        try
        {
            int index = 0;
            while (Devcon.FindByInterfaceGuid(interfaceGuid, out string path, out string id, index++, true))
                results.Add((path, id));
        }
        catch { }
        return results;
    }

    private static bool PathEquals(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }
}
