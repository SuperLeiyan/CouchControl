using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using Nefarius.Utilities.DeviceManagement.PnP;

namespace CouchControl;

/// <summary>
/// Reads a Sony DualSense (USB, Bluetooth, or the virtual one exposed by a Moonlight /
/// Sunshine stream) over raw HID and keeps its latest state for <see cref="ControllerBridge"/>,
/// which copies it to the main virtual Xbox 360 controller:
///
///   DualSense (HID) ──► DualSenseBridge ──► ControllerBridge ──► main virtual X360 ──► games
///         ▲                                        │
///         └──────────── rumble ◄───────────────────┘
///
/// The physical DualSense is hidden from games through HidHide (see <see cref="HidHideManager"/>).
/// </summary>
public sealed class DualSenseBridge : IDisposable
{
    // Matches both USB  (\\?\hid#vid_054c&pid_0ce6#...) and
    //            BT   (\\?\hid#{00001124-...}_vid&0002054c_pid&0ce6#...) interface paths.
    private static readonly Regex DualSensePath = new(
        @"054c[_&]pid[_&](0ce6|0df2)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private Thread? _thread;
    private volatile bool _running;
    private volatile SafeFileHandle? _handle;
    private volatile RumbleWriter? _rumble;
    private readonly HashSet<string> _reportedOpenFailures = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _stateLock = new();
    private ControllerState _state;
    private bool _hasState;
    private volatile bool _guideDown;

    public bool IsConnected { get; private set; }
    public bool IsBluetooth { get; private set; }

    /// <summary>True while the PS button is held.</summary>
    public bool GuideDown => _guideDown;

    private readonly Action<TouchpadGesture>? _onGesture;

    public DualSenseBridge(Action<TouchpadGesture>? onGesture = null)
    {
        _onGesture = onGesture;
    }

    public void Start()
    {
        _running = true;
        _thread = new Thread(Run) { IsBackground = true, Name = "DualSenseBridge" };
        _thread.Start();
    }

    /// <summary>Latest raw state (no deadzone). False if no DualSense is connected.</summary>
    internal bool TryGetState(out ControllerState state)
    {
        lock (_stateLock)
        {
            state = _state;
            return _hasState;
        }
    }

    /// <summary>Sets the rumble motors (0–255). Ignored if no DualSense is connected.</summary>
    public void SetRumble(byte largeMotor, byte smallMotor) => _rumble?.Set(largeMotor, smallMotor);

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
                if (!RunSession(path, handle))
                    Sleep(2000); // device present but silent (e.g. a Bluetooth pad that's switched off)
            }
            catch (Exception ex)
            {
                AppLogger.Log($"DualSense error: {ex.Message}");
                Sleep(1000);
            }
        }
    }

    /// <summary>
    /// A Bluetooth DualSense that's switched off can stay in Device Manager with reads that
    /// never complete. Once it has sent nothing for this long, the session ends. (Full
    /// Bluetooth reports arrive hundreds of times a second.)
    /// </summary>
    private const int BluetoothSilenceTimeoutMs = 2000;

    /// <summary>
    /// One controller session: pump reports until it disconnects or goes silent.
    /// Returns false if the controller never sent a report.
    /// </summary>
    private bool RunSession(string path, SafeFileHandle handle)
    {
        if (!TryGetCaps(handle, out var caps) || caps.InputReportByteLength <= 0)
        {
            AppLogger.Log("DualSense: could not read HID capabilities.");
            return false;
        }
        int reportLength = caps.InputReportByteLength;
        bool bluetooth = path.Contains("00001124", StringComparison.OrdinalIgnoreCase);

        using var rumble = RumbleWriter.TryOpen(path, bluetooth, caps.OutputReportByteLength,
            UsesVibrationV2(path, handle, caps.FeatureReportByteLength));

        IsBluetooth = bluetooth;
        _handle = handle;
        long lastReport = Environment.TickCount64;
        bool fullReports = false; // set once full (continuous) Bluetooth reports arrive
        bool silent = false;

        // Bluetooth watchdog. A controller that was streaming full reports can go quiet for
        // two reasons: it was switched off, or something (e.g. Windows when the touch keyboard
        // opens) put it back into "simple" mode, which only reports on change. Re-request full
        // reports: a switched-off controller rejects the write, so only then end the session.
        int refreshesWithoutData = 0;
        using var watchdog = bluetooth
            ? new System.Threading.Timer(_ =>
            {
                if (!Volatile.Read(ref fullReports) ||
                    Environment.TickCount64 - Interlocked.Read(ref lastReport) <= BluetoothSilenceTimeoutMs)
                    return;

                if (rumble != null && Volatile.Read(ref refreshesWithoutData) < 3 && rumble.Refresh())
                {
                    Interlocked.Increment(ref refreshesWithoutData); // reset only by a real report
                    Interlocked.Exchange(ref lastReport, Environment.TickCount64); // give it another window
                    AppLogger.Log("DualSense went quiet; re-requested full reports.");
                    return;
                }

                Volatile.Write(ref silent, true);
                CancelIoEx(handle, IntPtr.Zero);
            }, null, 500, 500)
            : null;
        long lastSimpleRefresh = 0;

        var buffer = new byte[reportLength];
        var gestures = new TouchpadGestureDetector();
        bool loggedTouch = false;
        bool gotReport = false;
        try
        {
            while (_running)
            {
                if (!ReadFile(handle, buffer, (uint)buffer.Length, out uint read, IntPtr.Zero))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (_running && gotReport)
                        AppLogger.Log(Volatile.Read(ref silent)
                            ? "DualSense disconnected (no data for 2 seconds)."
                            : $"DualSense disconnected ({new Win32Exception(err).Message}).");
                    break;
                }
                Interlocked.Exchange(ref lastReport, Environment.TickCount64);
                Volatile.Write(ref refreshesWithoutData, 0);
                if (bluetooth && read > 0)
                {
                    if (buffer[0] == 0x31)
                    {
                        Volatile.Write(ref fullReports, true);
                    }
                    else if (buffer[0] == 0x01 && Volatile.Read(ref fullReports) && rumble != null &&
                             Environment.TickCount64 - lastSimpleRefresh > 1000)
                    {
                        // Something switched it back to simple reports (no touchpad data).
                        lastSimpleRefresh = Environment.TickCount64;
                        AppLogger.Log("DualSense switched back to simple reports; re-requesting full reports.");
                        rumble.Refresh();
                    }
                }

                if (!gotReport)
                {
                    // Only now do we know the controller is really on.
                    gotReport = true;
                    AppLogger.Log($"DualSense connected ({(bluetooth ? "Bluetooth" : "USB/virtual")}, report {reportLength} bytes).");
                    // An output report also switches a Bluetooth DualSense from its "simple"
                    // input report to the full one, which carries the touchpad data.
                    rumble?.Set(0, 0);
                    _rumble = rumble;
                    IsConnected = true;
                }

                var report = buffer.AsSpan(0, (int)read);
                if (DualSenseReport.TryParse(report, bluetooth, out var state))
                {
                    lock (_stateLock)
                    {
                        _state = state;
                        _hasState = true;
                    }
                    _guideDown = state.Guide;
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
            _rumble = null;
            rumble?.Stop();
            lock (_stateLock)
            {
                _state = default;
                _hasState = false;
            }
            _guideDown = false;
            IsConnected = false;
        }
        return gotReport;
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

    private static bool TryGetCaps(SafeFileHandle handle, out HIDP_CAPS caps)
    {
        caps = default;
        if (!HidD_GetPreparsedData(handle, out IntPtr preparsed)) return false;
        try { return HidP_GetCaps(preparsed, out caps) == HIDP_STATUS_SUCCESS; }
        finally { HidD_FreePreparsedData(preparsed); }
    }

    /// <summary>
    /// Newer DualSense firmware (feature version ≥ 2.21) and every DualSense Edge use a
    /// different rumble flag. Falls back to the new method if the version can't be read
    /// (e.g. a virtual DualSense from a stream), since nearly all controllers are updated.
    /// </summary>
    private static bool UsesVibrationV2(string path, SafeFileHandle handle, int featureLength)
    {
        if (path.Contains("0df2", StringComparison.OrdinalIgnoreCase)) return true; // Edge
        if (featureLength < 46) return true;

        var buf = new byte[featureLength];
        buf[0] = 0x20; // firmware info
        if (!HidD_GetFeature(handle, buf, buf.Length)) return true;

        var version = DualSenseReport.ParseUpdateVersion(buf);
        bool v2 = version is null || version >= DualSenseReport.VibrationV2MinVersion;
        AppLogger.Log($"DualSense feature version 0x{version ?? 0:X4}; rumble method {(v2 ? "v2" : "v1")}.");
        return v2;
    }

    /// <summary>Writes rumble output reports to a DualSense on its own write handle.</summary>
    private sealed class RumbleWriter : IDisposable
    {
        private readonly SafeFileHandle _handle;
        private readonly bool _bluetooth, _vibrationV2;
        private readonly int _length;
        private readonly object _lock = new();
        private byte _seq;
        private int _last = -1; // last (large << 8 | small) sent
        private bool _loggedFailure, _disposed;

        private RumbleWriter(SafeFileHandle handle, bool bluetooth, bool vibrationV2, int length)
        {
            _handle = handle; _bluetooth = bluetooth; _vibrationV2 = vibrationV2; _length = length;
        }

        public static RumbleWriter? TryOpen(string path, bool bluetooth, int outputLength, bool vibrationV2)
        {
            int needed = bluetooth ? DualSenseReport.BtOutputReportSize : DualSenseReport.UsbOutputReportSize;
            if (outputLength < needed)
            {
                AppLogger.Log($"DualSense: output report too short ({outputLength} bytes); rumble disabled.");
                return null;
            }

            var h = CreateFile(path, GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid)
            {
                int err = Marshal.GetLastWin32Error();
                h.Dispose();
                AppLogger.Log($"DualSense: can't open for writing ({new Win32Exception(err).Message}); rumble disabled.");
                return null;
            }
            return new RumbleWriter(h, bluetooth, vibrationV2, outputLength);
        }

        public void Set(byte large, byte small)
        {
            lock (_lock)
            {
                if (_disposed) return;
                int key = large << 8 | small;
                if (key == _last) return; // games resend the same values constantly
                var report = DualSenseReport.BuildRumbleReport(_bluetooth, _vibrationV2, large, small, _seq, _length);
                _seq = (byte)((_seq + 1) & 0x0F);
                if (WriteFile(_handle, report, (uint)report.Length, out _, IntPtr.Zero))
                {
                    _last = key;
                    _loggedFailure = false;
                }
                else if (!_loggedFailure)
                {
                    _loggedFailure = true;
                    AppLogger.Log($"DualSense rumble write failed ({new Win32Exception(Marshal.GetLastWin32Error()).Message}).");
                }
            }
        }

        public void Stop() => Set(0, 0);

        /// <summary>
        /// Re-sends the current rumble state. Any output report also asks a Bluetooth
        /// DualSense for full input reports. Returns false if the write failed
        /// (e.g. the controller is switched off).
        /// </summary>
        public bool Refresh()
        {
            lock (_lock)
            {
                if (_disposed) return false;
                byte large = (byte)(Math.Max(_last, 0) >> 8), small = (byte)(Math.Max(_last, 0) & 0xFF);
                var report = DualSenseReport.BuildRumbleReport(_bluetooth, _vibrationV2, large, small, _seq, _length);
                _seq = (byte)((_seq + 1) & 0x0F);
                return WriteFile(_handle, report, (uint)report.Length, out _, IntPtr.Zero);
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                _handle.Dispose();
            }
        }
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
    private static extern bool WriteFile(SafeFileHandle hFile, byte[] lpBuffer, uint nNumberOfBytesToWrite,
        out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetFeature(SafeFileHandle hidDeviceObject, byte[] reportBuffer, int reportBufferLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(SafeFileHandle hFile, IntPtr lpOverlapped);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle hidDeviceObject, out IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern int HidP_GetCaps(IntPtr preparsedData, out HIDP_CAPS capabilities);
}
