using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using Nefarius.Utilities.DeviceManagement.PnP;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;

namespace CouchControl;

/// <summary>
/// Reads a Sony DualSense (USB, Bluetooth, or the virtual one exposed by a Moonlight /
/// Sunshine stream) over raw HID and mirrors it onto a dedicated, always-live ViGEm
/// Xbox 360 "feed" controller.
///
///   DualSense (HID) ──► feed X360 (XInput, never muted) ──► JoyXoff
///                                   │
///                                   └─► ControllerBridge picks it up as a "physical"
///                                       XInput pad ──► main virtual X360 (muted) ──► games
///
/// The feed is hidden from games through HidHide (see <see cref="HidHideManager"/>) so
/// games don't receive double input.
/// </summary>
public sealed class DualSenseBridge : IDisposable
{
    // Matches both USB  (\\?\hid#vid_054c&pid_0ce6#...) and
    //            BT   (\\?\hid#{00001124-...}_vid&0002054c_pid&0ce6#...) interface paths.
    private static readonly Regex DualSensePath = new(
        @"054c[_&]pid[_&](0ce6|0df2)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly ViGEmClient _vigem;
    private Thread? _thread;
    private volatile bool _running;
    private volatile SafeFileHandle? _handle;
    private readonly HashSet<string> _reportedOpenFailures = new(StringComparer.OrdinalIgnoreCase);

    public bool IsConnected { get; private set; }
    public int FeedSlot { get; private set; } = -1;
    public bool FeedHidden { get; private set; }

    private readonly Action<TouchpadGesture>? _onGesture;

    public DualSenseBridge(ViGEmClient vigem, Action<TouchpadGesture>? onGesture = null)
    {
        _vigem = vigem;
        _onGesture = onGesture;
    }

    public void Start()
    {
        _running = true;
        _thread = new Thread(Run) { IsBackground = true, Name = "DualSenseBridge" };
        _thread.Start();
    }

    private void Run()
    {
        while (_running)
        {
            try
            {
                var path = FindDualSensePath();
                if (path == null)
                {
                    Sleep(1000);
                    continue;
                }

                using var handle = OpenDevice(path);
                if (handle == null)
                {
                    Sleep(2000);
                    continue;
                }

                _reportedOpenFailures.Remove(path);
                RunSession(path, handle);
            }
            catch (Exception ex)
            {
                AppLogger.Log($"DualSense error: {ex.Message}");
                Sleep(1000);
            }
        }
    }

    /// <summary>One connected-controller session: create feed, pump reports, tear down.</summary>
    private void RunSession(string path, SafeFileHandle handle)
    {
        int reportLength = GetInputReportLength(handle);
        if (reportLength <= 0)
        {
            AppLogger.Log("DualSense: could not read HID capabilities.");
            Sleep(2000);
            return;
        }

        bool bluetooth = path.Contains("00001124", StringComparison.OrdinalIgnoreCase);
        AppLogger.Log($"DualSense connected ({(bluetooth ? "Bluetooth" : "USB/virtual")}, report {reportLength} bytes).");

        var before = GetXUsbInstanceIds();
        var feed = _vigem.CreateXbox360Controller();
        feed.AutoSubmitReport = false;
        feed.Connect();

        string? feedInstanceId = WaitForNewXUsbInstance(before);
        FeedHidden = feedInstanceId != null && HidHideManager.TryHideFeed(feedInstanceId);
        if (feedInstanceId == null)
            AppLogger.Log("DualSense: could not identify the feed controller's instance ID; it won't be hidden from games.");

        try { FeedSlot = feed.UserIndex; } catch { FeedSlot = -1; }
        AppLogger.Log($"DualSense feed controller connected on XInput slot {FeedSlot}.");
        IsConnected = true;

        _handle = handle;
        var buffer = new byte[reportLength];
        var gestures = new TouchpadGestureDetector();
        bool loggedTouch = false;
        try
        {
            while (_running)
            {
                if (!ReadFile(handle, buffer, (uint)buffer.Length, out uint read, IntPtr.Zero))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (_running)
                        AppLogger.Log($"DualSense disconnected ({new Win32Exception(err).Message}).");
                    break;
                }

                var report = buffer.AsSpan(0, (int)read);
                if (DualSenseReport.TryParse(report, bluetooth, out var state))
                {
                    ControllerBridge.WriteState(feed, state);
                    feed.SubmitReport();
                }

                if (DualSenseReport.TryParseTouch(report, bluetooth, out bool touching, out int tx, out int ty, out bool clicked))
                {
                    if (touching && !loggedTouch)
                    {
                        loggedTouch = true;
                        AppLogger.Log("DualSense touchpad input detected.");
                    }

                    var gesture = gestures.Update(touching, tx, ty, clicked, Environment.TickCount64);
                    if (gesture != TouchpadGesture.None && _onGesture != null)
                    {
                        // Run off the HID thread so actions (e.g. starting TabTip) don't stall input.
                        var g = gesture;
                        Task.Run(() => _onGesture(g));
                    }
                }
            }
        }
        finally
        {
            _handle = null;
            IsConnected = false;
            FeedSlot = -1;
            try
            {
                ControllerBridge.WriteState(feed, ControllerState.Neutral);
                feed.SubmitReport();
                feed.Disconnect();
            }
            catch { }
            if (feedInstanceId != null && FeedHidden)
                HidHideManager.UnhideFeed(feedInstanceId);
            FeedHidden = false;
            AppLogger.Log("DualSense feed controller removed.");
        }
    }

    private static string? FindDualSensePath()
    {
        int index = 0;
        while (Devcon.FindByInterfaceGuid(DeviceInterfaceIds.HidDevice,
                   out string path, out _, index++, true))
        {
            if (DualSensePath.IsMatch(path)) return path;
        }
        return null;
    }

    private SafeFileHandle? OpenDevice(string path)
    {
        var h = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            h.Dispose();
            // Retry read-only; some virtual devices reject write access.
            h = CreateFile(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid)
            {
                h.Dispose();
                if (_reportedOpenFailures.Add(path))
                    AppLogger.Log($"DualSense found but could not be opened ({new Win32Exception(err).Message}). " +
                        "If it is hidden in HidHide, add CouchControl.exe to HidHide's application list " +
                        "(or turn on 'Manage HidHide' in the tray menu).");
                return null;
            }
        }
        return h;
    }

    private static int GetInputReportLength(SafeFileHandle handle)
    {
        if (!HidD_GetPreparsedData(handle, out IntPtr preparsed)) return -1;
        try
        {
            return HidP_GetCaps(preparsed, out HIDP_CAPS caps) == HIDP_STATUS_SUCCESS
                ? caps.InputReportByteLength
                : -1;
        }
        finally { HidD_FreePreparsedData(preparsed); }
    }

    private static HashSet<string> GetXUsbInstanceIds()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            int index = 0;
            while (Devcon.FindByInterfaceGuid(DeviceInterfaceIds.XUsbDevice,
                       out _, out string instanceId, index++, true))
                ids.Add(instanceId);
        }
        catch { }
        return ids;
    }

    private string? WaitForNewXUsbInstance(HashSet<string> before)
    {
        for (int i = 0; i < 30 && _running; i++)
        {
            var added = GetXUsbInstanceIds().Except(before, StringComparer.OrdinalIgnoreCase).ToList();
            if (added.Count == 1) return added[0];
            if (added.Count > 1)
            {
                AppLogger.Log("DualSense: multiple new Xbox devices appeared at once; not hiding feed.");
                return null;
            }
            Thread.Sleep(100);
        }
        return null;
    }

    private void Sleep(int ms)
    {
        for (int waited = 0; waited < ms && _running; waited += 100)
            Thread.Sleep(100);
    }

    public void Dispose()
    {
        _running = false;
        var h = _handle;
        if (h != null && !h.IsClosed)
            CancelIoEx(h, IntPtr.Zero);   // unblock the pending ReadFile
        _thread?.Join(2000);
    }

    // ── Win32 / HID interop ────────────────────────────────────────────────

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x1;
    private const uint FILE_SHARE_WRITE = 0x2;
    private const uint OPEN_EXISTING = 3;
    private const int HIDP_STATUS_SUCCESS = 0x00110000;

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(SafeFileHandle hFile, [Out] byte[] lpBuffer, uint nNumberOfBytesToRead,
        out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(SafeFileHandle hFile, IntPtr lpOverlapped);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle hidDeviceObject, out IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern int HidP_GetCaps(IntPtr preparsedData, out HIDP_CAPS capabilities);
}
