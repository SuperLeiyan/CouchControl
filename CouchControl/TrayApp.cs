using System.Windows.Forms;
using CouchControl;

namespace CouchControl;

/// <summary>
/// System tray application host for CouchControl.
/// Manages the NotifyIcon, context menu, and the controller bridge lifecycle.
/// </summary>
public sealed class TrayApp : IDisposable
{
    private readonly NotifyIcon _trayIcon;
    private readonly ControllerBridge _bridge;
    private readonly GuideButtonWatcher _guideWatcher;
    private readonly CancellationTokenSource _cts = new();
    private AppConfig _config;
    private volatile bool _guideActionsEnabled;
    private volatile bool _touchpadGesturesEnabled;
    private int _gestureBusy; // 1 while an action runs, so overlapping gestures are dropped

    // Menu items that need updating
    private ToolStripMenuItem _muteStatusItem = null!;
    private ToolStripMenuItem _slotInfoItem = null!;
    private ToolStripMenuItem _dualSenseItem = null!;
    private ToolStripMenuItem _desktopModeItem = null!;
    private ToolStripMenuItem _hidHideItem = null!;
    private ToolStripMenuItem _restoreHidHideItem = null!;
    private ToolStripMenuItem _startWithWindowsItem = null!;
    private ToolStripMenuItem _autoMuteProcessesMenu = null!;

    public TrayApp()
    {
        _config = AppConfig.Load();
        _config.Save(); // writes any new settings (e.g. DesktopMode) so they can be edited
        _bridge = new ControllerBridge(_config.ManageHidHide, _config.DesktopMode)
        {
            MuteOnWindowsUi = _config.MuteOnWindowsUi
        };
        _bridge.DesktopModeChanged += OnDesktopModeChanged;
        _guideActionsEnabled = _config.GuideButtonActions;
        _guideWatcher = new GuideButtonWatcher(() => _guideActionsEnabled);
        _touchpadGesturesEnabled = _config.TouchpadGestures;
        _bridge.TouchpadGestureDetected += OnTouchpadGesture;

        _trayIcon = new NotifyIcon
        {
            Text = "CouchControl",
            Icon = LoadIcon(),
            Visible = true,
            ContextMenuStrip = BuildContextMenu()
        };

        _trayIcon.DoubleClick += (_, _) => ShowStatus();
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
    }

    private readonly SynchronizationContext _ui;

    private void OnDesktopModeChanged(bool active)
    {
        // Raised on the poll thread; never block it.
        _ui.Post(_ =>
        {
            _desktopModeItem.Checked = active;
            if (_config.DesktopMode.PlaySoundOnSwitch)
                (active ? System.Media.SystemSounds.Asterisk : System.Media.SystemSounds.Hand).Play();
            _trayIcon.ShowBalloonTip(1000, "CouchControl",
                active ? "Desktop mode ON — controller drives the mouse" : "Game mode — controller goes to games",
                ToolTipIcon.None);
        }, null);
    }

    /// <summary>
    /// After HidHide is switched out of block-list mode, the apps that were blocked become
    /// auto-mute processes — the substitute for the old block list.
    /// </summary>
    private void MergeConvertedBlockList()
    {
        var added = HidHideManager.ConvertedBlockListProcesses
            .Where(p => !_config.AutoMuteProcesses.Contains(p, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (added.Count == 0) return;

        _config.AutoMuteProcesses.AddRange(added);
        _config.Save();
        RebuildAutoMuteMenu();
        AppLogger.Log($"Added former HidHide block list to Auto-Mute Processes: {string.Join(", ", added)}");
        _trayIcon.ShowBalloonTip(4000, "CouchControl",
            $"HidHide switched to normal mode. Your blocked apps are now auto-muted: {string.Join(", ", added)}",
            ToolTipIcon.Info);
    }

    private void RestoreHidHide()
    {
        if (MessageBox.Show(
                "Restore the HidHide settings from before CouchControl switched it to normal mode?\n\n" +
                "This also turns off 'Manage HidHide Automatically', so games may see your physical controllers again.",
                "CouchControl", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        _bridge.ManageHidHide = false;
        _config.ManageHidHide = false;
        _config.Save();
        _hidHideItem.Checked = false;

        bool ok = HidHideManager.RestoreBackup();
        MessageBox.Show(ok ? "HidHide settings restored." : "Restore failed. See the log for details.",
            "CouchControl", MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Error);
    }

    private void OpenSettingsFile()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe", $"\"{AppPaths.ConfigFile}\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppLogger.Log($"Could not open settings: {ex.Message}");
        }
    }

    private void ReloadSettings()
    {
        _config = AppConfig.Load();
        _bridge.ApplyDesktopSettings(_config.DesktopMode);
        _guideActionsEnabled = _config.GuideButtonActions;
        _touchpadGesturesEnabled = _config.TouchpadGestures;
        _bridge.MuteOnWindowsUi = _config.MuteOnWindowsUi;
        _desktopModeItem.Text = DesktopModeLabel();
        RebuildAutoMuteMenu();
        AppLogger.Log("Settings reloaded.");
        _trayIcon.ShowBalloonTip(1000, "CouchControl", "Settings reloaded", ToolTipIcon.None);
    }

    private string DesktopModeLabel() => $"Desktop Mode ({_config.DesktopMode.ToggleChord})";

    public void Run()
    {
        if (!_bridge.Initialize())
        {
            MessageBox.Show(
                "CouchControl failed to initialize.\n\nMake sure ViGEmBus is installed.",
                "CouchControl Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        MergeConvertedBlockList();

        // Run the bridge loop on a background task
        _ = Task.Run(() => _bridge.RunAsync(_cts.Token));

        // Guide/PS button → touch keyboard / show desktop (formerly GuideTray)
        _guideWatcher.Start();

        // Update tray icon tooltip based on mute state
        _ = Task.Run(async () =>
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                UpdateTrayIcon();
                await Task.Delay(200, _cts.Token).ConfigureAwait(false);
            }
        }, _cts.Token);

        Application.Run();
    }

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip();

        // Status indicator (non-clickable)
        _muteStatusItem = new ToolStripMenuItem("● UNMUTED") { Enabled = false };
        _muteStatusItem.Font = new System.Drawing.Font(_muteStatusItem.Font, System.Drawing.FontStyle.Bold);
        menu.Items.Add(_muteStatusItem);

        // Slot info (non-clickable)
        _slotInfoItem = new ToolStripMenuItem("Virtual: slot ? | Physical: slot ?") { Enabled = false };
        menu.Items.Add(_slotInfoItem);

        // DualSense feed info (non-clickable)
        _dualSenseItem = new ToolStripMenuItem("DualSense: not detected") { Enabled = false };
        menu.Items.Add(_dualSenseItem);

        menu.Items.Add(new ToolStripSeparator());

        // Desktop mode (built-in mouse/keyboard control)
        _desktopModeItem = new ToolStripMenuItem(DesktopModeLabel())
        {
            ToolTipText = "Controller drives the mouse and keyboard; games get no input."
        };
        _desktopModeItem.Click += (_, _) => _bridge.SetDesktopMode(!_bridge.DesktopModeActive);
        menu.Items.Add(_desktopModeItem);

        // Auto-mute processes submenu
        _autoMuteProcessesMenu = new ToolStripMenuItem("Auto-Mute Processes");
        RebuildAutoMuteMenu();
        menu.Items.Add(_autoMuteProcessesMenu);

        // Guide button actions toggle
        var guideItem = new ToolStripMenuItem("Guide/PS Button: Touch Keyboard")
        {
            Checked = _config.GuideButtonActions,
            CheckOnClick = true,
            ToolTipText = "Tap: toggle touch keyboard. Double-tap: show desktop."
        };
        guideItem.CheckedChanged += (_, _) =>
        {
            _guideActionsEnabled = guideItem.Checked;
            _config.GuideButtonActions = guideItem.Checked;
            _config.Save();
        };
        menu.Items.Add(guideItem);

        // DualSense touchpad gestures toggle
        var touchpadItem = new ToolStripMenuItem("DualSense Touchpad Gestures")
        {
            Checked = _config.TouchpadGestures,
            CheckOnClick = true,
            ToolTipText = "Long touch: toggle touch keyboard. Double-tap: show desktop."
        };
        touchpadItem.CheckedChanged += (_, _) =>
        {
            _touchpadGesturesEnabled = touchpadItem.Checked;
            _config.TouchpadGestures = touchpadItem.Checked;
            _config.Save();
        };
        menu.Items.Add(touchpadItem);

        // HidHide automation toggle
        var hidHideItem = _hidHideItem = new ToolStripMenuItem("Manage HidHide Automatically")
        {
            Checked = _config.ManageHidHide,
            CheckOnClick = true,
            ToolTipText = "Allow-list CouchControl, and hide physical controllers from games while CouchControl runs."
        };
        hidHideItem.CheckedChanged += (_, _) =>
        {
            _config.ManageHidHide = hidHideItem.Checked;
            _config.Save();
            bool on = hidHideItem.Checked;
            Task.Run(() => _bridge.ManageHidHide = on); // HidHide calls can block briefly
        };
        menu.Items.Add(hidHideItem);

        _restoreHidHideItem = new ToolStripMenuItem("Restore Previous HidHide Settings...")
        {
            Visible = HidHideManager.HasBackup
        };
        _restoreHidHideItem.Click += (_, _) => RestoreHidHide();
        menu.Items.Add(_restoreHidHideItem);

        var editSettingsItem = new ToolStripMenuItem("Edit Settings File...");
        editSettingsItem.Click += (_, _) => OpenSettingsFile();
        menu.Items.Add(editSettingsItem);

        var reloadSettingsItem = new ToolStripMenuItem("Reload Settings");
        reloadSettingsItem.Click += (_, _) => ReloadSettings();
        menu.Items.Add(reloadSettingsItem);

        menu.Items.Add(new ToolStripSeparator());

        // Start with Windows toggle
        _startWithWindowsItem = new ToolStripMenuItem("Start with Windows")
        {
            Checked = StartupManager.IsEnabled(),
            CheckOnClick = true
        };
        _startWithWindowsItem.CheckedChanged += (_, _) =>
        {
            if (_startWithWindowsItem.Checked)
                StartupManager.Enable();
            else
                StartupManager.Disable();
        };
        menu.Items.Add(_startWithWindowsItem);

        menu.Items.Add(new ToolStripSeparator());

        // Quit
        var quitItem = new ToolStripMenuItem("Quit CouchControl");
        quitItem.Click += (_, _) => Quit();
        menu.Items.Add(quitItem);

        // Update status when menu opens
        menu.Opening += (_, _) =>
        {
            bool muted = _bridge.IsMuted;
            _muteStatusItem.Text = muted ? "● MUTED" : "● UNMUTED";
            _muteStatusItem.ForeColor = muted
                ? System.Drawing.Color.Red
                : System.Drawing.Color.Green;

            int vs = _bridge.VirtualSlot;
            int ps = _bridge.PhysicalSlot;
            string vsText = vs >= 0 ? $"slot {vs}" : "unknown";
            string psText = ps >= 0 ? $"slot {ps}" : "not connected";
            _slotInfoItem.Text = $"Virtual: {vsText}  |  Physical: {psText}";
            _slotInfoItem.ForeColor = vs == 0
                ? System.Drawing.Color.DarkGreen
                : System.Drawing.Color.DarkOrange;

            if (_bridge.DualSenseConnected)
            {
                int fs = _bridge.DualSenseFeedSlot;
                string hidden = _bridge.DualSenseFeedHidden ? "hidden from games" : "NOT hidden";
                _dualSenseItem.Text = $"DualSense: feed on slot {(fs >= 0 ? fs.ToString() : "?")} ({hidden})";
                _dualSenseItem.ForeColor = _bridge.DualSenseFeedHidden
                    ? System.Drawing.Color.DarkGreen
                    : System.Drawing.Color.DarkOrange;
            }
            else
            {
                _dualSenseItem.Text = "DualSense: not detected";
                _dualSenseItem.ForeColor = System.Drawing.SystemColors.GrayText;
            }

            _startWithWindowsItem.Checked = StartupManager.IsEnabled();
            _desktopModeItem.Checked = _bridge.DesktopModeActive;
            _desktopModeItem.Enabled = _config.DesktopMode.Enabled;
            _restoreHidHideItem.Visible = HidHideManager.HasBackup;
        };

        return menu;
    }

    private void RebuildAutoMuteMenu()
    {
        _autoMuteProcessesMenu.DropDownItems.Clear();

        // Built-in: Windows shell surfaces
        var windowsUiItem = new ToolStripMenuItem("Windows UI (Start, Search, Tray, Explorer)")
        {
            Checked = _config.MuteOnWindowsUi,
            CheckOnClick = true,
            ToolTipText = "Stops the Start menu and tray reacting to the controller in game mode."
        };
        windowsUiItem.CheckedChanged += (_, _) =>
        {
            _config.MuteOnWindowsUi = windowsUiItem.Checked;
            _bridge.MuteOnWindowsUi = windowsUiItem.Checked;
            _config.Save();
        };
        _autoMuteProcessesMenu.DropDownItems.Add(windowsUiItem);
        _autoMuteProcessesMenu.DropDownItems.Add(new ToolStripSeparator());

        // Show current processes with remove option
        foreach (var proc in _config.AutoMuteProcesses.ToList())
        {
            var item = new ToolStripMenuItem($"✓ {proc}");
            var captured = proc;
            item.Click += (_, _) =>
            {
                _config.AutoMuteProcesses.Remove(captured);
                _config.Save();
                RebuildAutoMuteMenu();
            };
            _autoMuteProcessesMenu.DropDownItems.Add(item);
        }

        if (_config.AutoMuteProcesses.Count > 0)
            _autoMuteProcessesMenu.DropDownItems.Add(new ToolStripSeparator());

        // Add by browsing to executable
        var addByBrowseItem = new ToolStripMenuItem("Add by browsing...");
        addByBrowseItem.Click += (_, _) => AddProcessByBrowse();
        _autoMuteProcessesMenu.DropDownItems.Add(addByBrowseItem);

        // Add by name
        var addByNameItem = new ToolStripMenuItem("Add by name...");
        addByNameItem.Click += (_, _) => AddProcessByName();
        _autoMuteProcessesMenu.DropDownItems.Add(addByNameItem);
    }

    private void AddProcessByBrowse()
    {
        // Run OpenFileDialog on a dedicated STA thread to avoid blocking the UI message loop
        var thread = new Thread(() =>
        {
            using var dialog = new OpenFileDialog
            {
                Title = "Select executable to auto-mute",
                Filter = "Executables (*.exe)|*.exe",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
            };

            if (dialog.ShowDialog() != DialogResult.OK) return;

            var name = Path.GetFileNameWithoutExtension(dialog.FileName);

            if (_config.AutoMuteProcesses.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                MessageBox.Show($"'{name}' is already in the auto-mute list.",
                    "CouchControl", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            _config.AutoMuteProcesses.Add(name);
            _config.Save();

            // Rebuild menu on UI thread
            _trayIcon.ContextMenuStrip?.Invoke(RebuildAutoMuteMenu);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
    }

    private void AddProcessByName()
    {
        using var form = new Form
        {
            Text = "CouchControl — Add Process",
            Size = new System.Drawing.Size(380, 140),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            MaximizeBox = false, MinimizeBox = false
        };
        var label = new Label { Text = "Process name (e.g. Battle.net, Discord):", Left = 12, Top = 12, Width = 340 };
        var textBox = new TextBox { Left = 12, Top = 34, Width = 340 };
        var ok = new Button { Text = "Add", Left = 196, Top = 64, Width = 75, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Left = 277, Top = 64, Width = 75, DialogResult = DialogResult.Cancel };
        form.Controls.AddRange([label, textBox, ok, cancel]);
        form.AcceptButton = ok;
        form.CancelButton = cancel;

        if (form.ShowDialog() != DialogResult.OK) return;

        var name = textBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;

        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];

        if (_config.AutoMuteProcesses.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            MessageBox.Show($"'{name}' is already in the auto-mute list.",
                "CouchControl", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _config.AutoMuteProcesses.Add(name);
        _config.Save();
        RebuildAutoMuteMenu();
    }

    private void OnTouchpadGesture(TouchpadGesture gesture)
    {
        if (!_touchpadGesturesEnabled) return;
        if (Interlocked.Exchange(ref _gestureBusy, 1) == 1) return;
        try
        {
            switch (gesture)
            {
                case TouchpadGesture.LongTouch:
                    AppLogger.Log("Touchpad: long touch → touch keyboard");
                    TouchKeyboard.Toggle();
                    break;
                case TouchpadGesture.DoubleTap:
                    AppLogger.Log("Touchpad: double tap → show desktop");
                    GuideButtonWatcher.ShowDesktop();
                    break;
            }
        }
        finally { Interlocked.Exchange(ref _gestureBusy, 0); }
    }

    private void UpdateTrayIcon()
    {
        bool muted = _bridge.IsMuted;
        string tooltip = _bridge.DesktopModeActive ? "CouchControl — Desktop mode"
            : muted ? "CouchControl — Games muted" : "CouchControl — Game mode";

        // Update on UI thread
        if (_trayIcon.ContextMenuStrip?.InvokeRequired == true)
            _trayIcon.ContextMenuStrip.Invoke(() => _trayIcon.Text = tooltip);
        else
            _trayIcon.Text = tooltip;
    }

    private void ShowStatus()
    {
        _trayIcon.ShowBalloonTip(
            2000,
            "CouchControl",
            _bridge.IsMuted ? "Controller is MUTED" : "Controller is active",
            _bridge.IsMuted ? ToolTipIcon.Warning : ToolTipIcon.Info);
    }

    private void Quit()
    {
        _cts.Cancel();
        _trayIcon.Visible = false;
        Application.Exit();
    }

    private static System.Drawing.Icon LoadIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "icon.ico");
        if (File.Exists(iconPath))
            return new System.Drawing.Icon(iconPath);
        return SystemIcons.Application;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    public void Dispose()
    {
        _cts.Cancel();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _guideWatcher.Dispose();
        _bridge.Dispose();
        _cts.Dispose();
    }
}
