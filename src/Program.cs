using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace NSBMP.Pacer
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool consoleMode = false;
            bool resetOnly = false;
            bool silentMode = false;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                if (a == "--console" || a == "-c") consoleMode = true;
                if (a == "--reset" || a == "-r") resetOnly = true;
                if (a == "--silent" || a == "-s") silentMode = true;
            }

            if (resetOnly)
            {
                RunResetOnly();
                return;
            }

            if (consoleMode)
            {
                RunConsoleMode();
                return;
            }

            SteamConfigManager.CleanHoi4LaunchOptions();
            Application.Run(new MainForm(silentMode));
        }

        static void InitConsoleOutput()
        {
            if (Win32.AttachConsole(-1))
            {
                var stdOut = new System.IO.StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
                Console.SetOut(stdOut);
                var stdErr = new System.IO.StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
                Console.SetError(stdErr);
            }
        }

        static void RunResetOnly()
        {
            InitConsoleOutput();
            Console.WriteLine("[NSBMP] Connecting to hoi4.exe to restore Speed 5 to 0.0s (Unlocked)...");
            using (var engine = new PacerEngine())
            {
                if (engine.TryAttach())
                {
                    engine.ResetSpeed5Unlocked();
                    Console.WriteLine("[NSBMP] Successfully reset Speed 5 to 0.0s and cleared lag recovery counters.");
                }
                else
                {
                    Console.WriteLine("[NSBMP] hoi4.exe is not currently running.");
                }
            }
        }

        static void RunConsoleMode()
        {
            InitConsoleOutput();
            Console.WriteLine("==================================================================");
            Console.WriteLine("  NSBMP Adaptive Speed 5 Controller v4.0 (Native C# Release)      ");
            Console.WriteLine("  Zero-Dependency | Universal Ping Floor | Auto-RVA Scanner       ");
            Console.WriteLine("==================================================================");
            Console.WriteLine("Waiting for hoi4.exe to launch (Press Ctrl+C to stop)...\n");

            using (var engine = new PacerEngine())
            {
                bool wasAttached = false;
                while (true)
                {
                    if (!engine.IsAttached)
                    {
                        if (wasAttached)
                        {
                            Console.WriteLine("\n[INFO] hoi4.exe closed. Waiting for restart...");
                            wasAttached = false;
                        }
                        if (engine.TryAttach())
                        {
                            Console.WriteLine(string.Format("[OK] Attached to hoi4.exe (PID: {0}) at base 0x{1:X}", engine.ProcessId, engine.BaseAddress));
                            wasAttached = true;
                        }
                        else
                        {
                            Thread.Sleep(1000);
                            continue;
                        }
                    }

                    engine.Update();
                    Thread.Sleep(16);
                }
            }
        }
    }

    public class MainForm : Form
    {
        private readonly PacerEngine _engine;
        private readonly System.Windows.Forms.Timer _timer;
        private readonly NotifyIcon _trayIcon;
        private readonly ContextMenuStrip _trayMenu;

        private Label _lblStatusBadge;
        private Label _lblStatusDesc;
        private Label _lblSpeedVal;
        private Label _lblSyncVal;
        private Label _lblModeVal;
        private Button _btnToggle;
        private Button _btnReset;
        private Button _btnAutoRun;
        private CheckBox _chkAutoClose;
        private CheckBox _chkMinimizeToTray;
        private CheckBox _chkAutoStartWithWindows;

        private bool _isPacingActive = true;
        private bool _hasAttachedOnce = false;
        private double _lastTrayUpdateTime = 0;

        public MainForm(bool startMinimized = false)
        {
            _engine = new PacerEngine();
            _engine.StatusUpdated += OnStatusUpdated;

            Text = "NSBMP - Hearts of Iron IV Speed 5 Pacer";
            Size = new Size(580, 520);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(30, 30, 36);
            ForeColor = Color.FromArgb(248, 249, 250);
            Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);

            // Tray Menu
            _trayMenu = new ContextMenuStrip();
            _trayMenu.Items.Add("Show Dashboard", null, (s, e) => ShowFromTray());
            _trayMenu.Items.Add("Reset Speed 5 (Unlocked)", null, (s, e) => _engine.ResetSpeed5Unlocked());
            _trayMenu.Items.Add("-");
            _trayMenu.Items.Add("Exit", null, (s, e) => Close());

            _trayIcon = new NotifyIcon
            {
                Text = "NSBMP Adaptive Speed 5 Controller",
                Icon = SystemIcons.Application,
                ContextMenuStrip = _trayMenu,
                Visible = true
            };
            _trayIcon.DoubleClick += (s, e) => ShowFromTray();

            try
            {
                Icon appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (appIcon != null)
                {
                    Icon = appIcon;
                    _trayIcon.Icon = appIcon;
                }
            }
            catch { }

            InitializeUi();

            _timer = new System.Windows.Forms.Timer { Interval = 1000 };
            _timer.Tick += (s, e) => TickLoop();
            _timer.Start();

            FormClosing += (s, e) =>
            {
                _timer.Stop();
                _engine.ResetSpeed5Unlocked();
                _engine.Dispose();
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
            };

            Resize += (s, e) =>
            {
                if (WindowState == FormWindowState.Minimized && _chkMinimizeToTray.Checked)
                {
                    Hide();
                    _trayIcon.ShowBalloonTip(1000, "NSBMP Running in Background", "Pacing is active. Double-click tray icon to open.", ToolTipIcon.Info);
                }
            };

            if (startMinimized)
            {
                WindowState = FormWindowState.Minimized;
                Hide();
                if (IsAutoStartEnabled())
                {
                    _chkAutoClose.Checked = false;
                }
            }
        }

        private bool IsAutoStartEnabled()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false))
                {
                    if (key != null)
                    {
                        var val = key.GetValue("NSBMP_Pacer");
                        return val != null;
                    }
                }
            }
            catch { }
            return false;
        }

        private void SetAutoStart(bool enable)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key != null)
                    {
                        if (enable)
                        {
                            key.SetValue("NSBMP_Pacer", "\"" + Application.ExecutablePath + "\" --silent");
                        }
                        else
                        {
                            key.DeleteValue("NSBMP_Pacer", false);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not update Windows startup registry: " + ex.Message, "NSBMP", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void InitializeUi()
        {
            // Header Panel
            var pnlHeader = new Panel
            {
                Location = new Point(0, 0),
                Size = new Size(580, 75),
                BackColor = Color.FromArgb(24, 24, 30)
            };

            var lblTitle = new Label
            {
                Text = "NSBMP Speed 5 Host Controller",
                Font = new Font("Segoe UI", 14f, FontStyle.Bold),
                ForeColor = Color.FromArgb(58, 134, 255),
                Location = new Point(20, 12),
                AutoSize = true
            };

            var lblSub = new Label
            {
                Text = "Real-Time Adaptive Multiplayer Synchronization | Hearts of Iron IV v1.19.3.0+",
                Font = new Font("Segoe UI", 8.5f, FontStyle.Regular),
                ForeColor = Color.FromArgb(173, 181, 189),
                Location = new Point(22, 42),
                AutoSize = true
            };

            pnlHeader.Controls.Add(lblTitle);
            pnlHeader.Controls.Add(lblSub);
            Controls.Add(pnlHeader);

            // Status Card
            var pnlStatus = new Panel
            {
                Location = new Point(20, 90),
                Size = new Size(525, 95),
                BackColor = Color.FromArgb(43, 43, 54)
            };

            _lblStatusBadge = new Label
            {
                Text = "WAITING FOR HOI4",
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                ForeColor = Color.FromArgb(255, 209, 102),
                Location = new Point(15, 12),
                AutoSize = true
            };

            _lblStatusDesc = new Label
            {
                Text = "Launch Hearts of Iron IV. The pacer will automatically attach and monitor multiplayer clients.",
                Font = new Font("Segoe UI", 9f),
                ForeColor = Color.FromArgb(220, 220, 230),
                Location = new Point(15, 38),
                Size = new Size(495, 45)
            };

            pnlStatus.Controls.Add(_lblStatusBadge);
            pnlStatus.Controls.Add(_lblStatusDesc);
            Controls.Add(pnlStatus);

            // Telemetry Card
            var pnlTelemetry = new Panel
            {
                Location = new Point(20, 200),
                Size = new Size(525, 130),
                BackColor = Color.FromArgb(43, 43, 54)
            };

            var lblSpeedHdr = new Label { Text = "SPEED 5 TARGET:", Font = new Font("Segoe UI", 8.5f, FontStyle.Bold), ForeColor = Color.FromArgb(173, 181, 189), Location = new Point(15, 12), AutoSize = true };
            _lblSpeedVal = new Label { Text = "0.000 s/hr (100% Unlocked)", Font = new Font("Segoe UI", 11f, FontStyle.Bold), ForeColor = Color.FromArgb(76, 201, 240), Location = new Point(15, 30), AutoSize = true };

            var lblSyncHdr = new Label { Text = "CLIENT SYNCHRONIZATION:", Font = new Font("Segoe UI", 8.5f, FontStyle.Bold), ForeColor = Color.FromArgb(173, 181, 189), Location = new Point(15, 55), AutoSize = true };
            _lblSyncVal = new Label { Text = "0 Clients | Lag: 0h (Learned Ping Floor: 2.0h)", Font = new Font("Segoe UI", 10f, FontStyle.Regular), ForeColor = Color.FromArgb(248, 249, 250), Location = new Point(15, 73), AutoSize = true };

            var lblModeHdr = new Label { Text = "ADAPTATION STATE:", Font = new Font("Segoe UI", 8.5f, FontStyle.Bold), ForeColor = Color.FromArgb(173, 181, 189), Location = new Point(15, 98), AutoSize = true };
            _lblModeVal = new Label { Text = "LOCKSTEP (Flat Daily Equilibrium)", Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), ForeColor = Color.FromArgb(6, 214, 160), Location = new Point(145, 97), AutoSize = true };

            pnlTelemetry.Controls.Add(lblSpeedHdr);
            pnlTelemetry.Controls.Add(_lblSpeedVal);
            pnlTelemetry.Controls.Add(lblSyncHdr);
            pnlTelemetry.Controls.Add(_lblSyncVal);
            pnlTelemetry.Controls.Add(lblModeHdr);
            pnlTelemetry.Controls.Add(_lblModeVal);
            Controls.Add(pnlTelemetry);

            // Controls
            _btnToggle = new Button
            {
                Text = "Disable Auto-Pacing",
                Location = new Point(20, 345),
                Size = new Size(160, 38),
                BackColor = Color.FromArgb(6, 214, 160),
                ForeColor = Color.FromArgb(20, 20, 20),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            _btnToggle.FlatAppearance.BorderSize = 0;
            _btnToggle.Click += (s, e) =>
            {
                _isPacingActive = !_isPacingActive;
                if (!_isPacingActive)
                {
                    _engine.ResetSpeed5Unlocked();
                    _btnToggle.Text = "Enable Auto-Pacing";
                    _btnToggle.BackColor = Color.FromArgb(108, 117, 125);
                }
                else
                {
                    _btnToggle.Text = "Disable Auto-Pacing";
                    _btnToggle.BackColor = Color.FromArgb(6, 214, 160);
                }
            };
            Controls.Add(_btnToggle);

            _btnReset = new Button
            {
                Text = "Reset Speed 5 (0.0s)",
                Location = new Point(190, 345),
                Size = new Size(160, 38),
                BackColor = Color.FromArgb(58, 134, 255),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            _btnReset.FlatAppearance.BorderSize = 0;
            _btnReset.Click += (s, e) =>
            {
                _engine.ResetSpeed5Unlocked();
                MessageBox.Show("Speed 5 restored to 0.0s (100% Unlocked) and lag recovery counters zeroed.", "NSBMP", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            Controls.Add(_btnReset);

            bool isAutoRun = IsAutoStartEnabled();
            _btnAutoRun = new Button
            {
                Text = isAutoRun ? "Auto-Run: ACTIVE" : "Enable Auto-Run",
                Location = new Point(360, 345),
                Size = new Size(185, 38),
                BackColor = isAutoRun ? Color.FromArgb(76, 201, 240) : Color.FromArgb(140, 100, 220),
                ForeColor = isAutoRun ? Color.FromArgb(20, 20, 20) : Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            _btnAutoRun.FlatAppearance.BorderSize = 0;
            _btnAutoRun.Click += (s, e) =>
            {
                bool newState = !IsAutoStartEnabled();
                SetAutoStart(newState);
                _chkAutoStartWithWindows.Checked = newState;
                if (newState)
                {
                    _chkAutoClose.Checked = false;
                    _btnAutoRun.Text = "Auto-Run: ACTIVE";
                    _btnAutoRun.BackColor = Color.FromArgb(76, 201, 240);
                    _btnAutoRun.ForeColor = Color.FromArgb(20, 20, 20);
                    MessageBox.Show(
                        "Auto-Run is now ACTIVE!\n\n" +
                        "* Starts quietly with Windows in your system tray (0% CPU, ~12 MB RAM).\n" +
                        "* Automatically attaches whenever Hearts of Iron IV starts (from Steam, Paradox Launcher, or shortcut)!\n" +
                        "* Cleanly resets and returns to standby when HOI4 exits.\n\n" +
                        "You never have to manually launch NSBMP again!",
                        "NSBMP Auto-Run Enabled",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
                else
                {
                    _btnAutoRun.Text = "Enable Auto-Run";
                    _btnAutoRun.BackColor = Color.FromArgb(140, 100, 220);
                    _btnAutoRun.ForeColor = Color.White;
                    MessageBox.Show("Auto-Run has been disabled.", "NSBMP", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            };
            Controls.Add(_btnAutoRun);

            _chkMinimizeToTray = new CheckBox
            {
                Text = "Minimize to System Tray",
                Checked = true,
                Location = new Point(22, 398),
                AutoSize = true,
                ForeColor = Color.FromArgb(173, 181, 189)
            };
            Controls.Add(_chkMinimizeToTray);

            _chkAutoClose = new CheckBox
            {
                Text = "Auto-close when HOI4 exits",
                Checked = !IsAutoStartEnabled(),
                Location = new Point(230, 398),
                AutoSize = true,
                ForeColor = Color.FromArgb(173, 181, 189)
            };
            Controls.Add(_chkAutoClose);

            _chkAutoStartWithWindows = new CheckBox
            {
                Text = "Auto-start with Windows (Tray Monitor Mode - always ready)",
                Checked = IsAutoStartEnabled(),
                Location = new Point(22, 428),
                AutoSize = true,
                ForeColor = Color.FromArgb(173, 181, 189)
            };
            _chkAutoStartWithWindows.CheckedChanged += (s, e) =>
            {
                bool enabled = _chkAutoStartWithWindows.Checked;
                SetAutoStart(enabled);
                if (enabled)
                {
                    _chkAutoClose.Checked = false;
                    _btnAutoRun.Text = "Auto-Run: ACTIVE";
                    _btnAutoRun.BackColor = Color.FromArgb(76, 201, 240);
                    _btnAutoRun.ForeColor = Color.FromArgb(20, 20, 20);
                }
                else
                {
                    _btnAutoRun.Text = "Enable Auto-Run";
                    _btnAutoRun.BackColor = Color.FromArgb(140, 100, 220);
                    _btnAutoRun.ForeColor = Color.White;
                }
            };
            Controls.Add(_chkAutoStartWithWindows);
        }

        private static void SetText(Label lbl, string text)
        {
            if (lbl.Text != text) lbl.Text = text;
        }

        private static void SetColor(Label lbl, Color color)
        {
            if (lbl.ForeColor != color) lbl.ForeColor = color;
        }

        private void TickLoop()
        {
            if (!_engine.IsAttached)
            {
                // Standby mode: 1000ms polling to use ~0.0% CPU when HOI4 is not running
                if (_timer.Interval != 1000) _timer.Interval = 1000;

                if (_hasAttachedOnce)
                {
                    if (_chkAutoClose.Checked)
                    {
                        Close();
                        return;
                    }
                    else
                    {
                        // HOI4 was closed in Tray Monitor / persistent mode
                        _engine.Close();
                        _hasAttachedOnce = false;
                        SetText(_lblStatusBadge, "WAITING FOR HOI4");
                        SetColor(_lblStatusBadge, Color.FromArgb(255, 209, 102));
                        SetText(_lblStatusDesc, "HOI4 closed. Standing by for next game session...");
                        SetText(_lblSpeedVal, "0.000 s/hr (Standby)");
                        SetText(_lblSyncVal, "No running session detected");
                        SetText(_lblModeVal, "STANDBY");
                        SetColor(_lblModeVal, Color.FromArgb(173, 181, 189));
                        _trayIcon.Text = "NSBMP: Standing by for HOI4";
                    }
                }

                if (_engine.TryAttach())
                {
                    _hasAttachedOnce = true;
                    _timer.Interval = 60; // 16 Hz active polling once attached (smooth with minimal CPU)
                    SetText(_lblStatusBadge, "ATTACHED TO HOI4");
                    SetColor(_lblStatusBadge, Color.FromArgb(6, 214, 160));
                    SetText(_lblStatusDesc, string.Format("Process ID: {0} | Base Address: 0x{1:X}\nSpeed 5 target resolved dynamically via PE scan.", _engine.ProcessId, _engine.BaseAddress));
                }
                else
                {
                    SetText(_lblStatusBadge, "WAITING FOR HOI4");
                    SetColor(_lblStatusBadge, Color.FromArgb(255, 209, 102));
                    SetText(_lblStatusDesc, "Launch Hearts of Iron IV. The controller will attach automatically when the game starts.");
                    SetText(_lblSpeedVal, "0.000 s/hr (Standby)");
                    SetText(_lblSyncVal, "No running session detected");
                    SetText(_lblModeVal, "STANDBY");
                    SetColor(_lblModeVal, Color.FromArgb(173, 181, 189));
                }
                return;
            }

            // Attached: Dynamically adapt timer rate (throttle to 120ms when minimized)
            int targetInterval = (WindowState == FormWindowState.Minimized || !Visible) ? 120 : 60;
            if (_timer.Interval != targetInterval) _timer.Interval = targetInterval;

            if (_isPacingActive)
            {
                _engine.Update();
            }
        }

        private void OnStatusUpdated(object sender, PacerStatusEventArgs e)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => OnStatusUpdated(sender, e)));
                return;
            }

            if (!e.IsAttached) return;

            // When minimized to tray, skip redrawing UI labels completely to save CPU
            bool isVisible = (WindowState != FormWindowState.Minimized && Visible);

            // Loading / Initializing Screen Safety State
            if (e.State == SessionState.LoadingOrMenu)
            {
                if (isVisible)
                {
                    SetText(_lblStatusBadge, "MENU / LOADING (SAFE)");
                    SetColor(_lblStatusBadge, Color.FromArgb(76, 201, 240));
                    SetText(_lblStatusDesc, !string.IsNullOrEmpty(e.StateMessage) ? e.StateMessage : "Game initializing | 100% Read-Only Safety Shield Active");
                    SetText(_lblSpeedVal, "0.000 s/hr (Safe Read-Only)");
                    SetText(_lblSyncVal, "Waiting for campaign session to load...");
                    SetText(_lblModeVal, "SAFE READ-ONLY");
                    SetColor(_lblModeVal, Color.FromArgb(76, 201, 240));
                }
                UpdateTrayThrottled("NSBMP: Standby (Loading / Read-Only)");
                return;
            }

            if (e.State == SessionState.AttachedInitializing)
            {
                if (isVisible)
                {
                    SetText(_lblStatusBadge, "ATTACHED (INITIALIZING)");
                    SetColor(_lblStatusBadge, Color.FromArgb(255, 209, 102));
                    SetText(_lblStatusDesc, "Game process attached | Waiting for gamestate initialization");
                    SetText(_lblSpeedVal, "0.000 s/hr (Safe Read-Only)");
                    SetText(_lblSyncVal, "Standby (Safe Read-Only)");
                    SetText(_lblModeVal, "STANDBY");
                    SetColor(_lblModeVal, Color.FromArgb(173, 181, 189));
                }
                return;
            }

            if (isVisible)
            {
                // UI & Status Logic
                if (e.CurrentSpeed < 5)
                {
                    // Speed 1, 2, 3, or 4
                    string spdText = e.IsPaused ? string.Format("SPEED {0} (PAUSED)", e.CurrentSpeed) : string.Format("SPEED {0}", e.CurrentSpeed);
                    SetText(_lblStatusBadge, spdText);
                    SetColor(_lblStatusBadge, Color.FromArgb(76, 201, 240));
                    SetText(_lblStatusDesc, string.Format("Standby | MP Primed at {0:F1}ms", e.MpBaseDelayMs));
                }
                else // Speed 5
                {
                    if (e.ConnectedClients <= 0)
                    {
                        // Singleplayer / Solo
                        string sp5Text = e.IsPaused ? "SPEED 5 (UNLOCKED 0.0s) (PAUSED)" : "SPEED 5 (UNLOCKED 0.0s)";
                        SetText(_lblStatusBadge, sp5Text);
                        SetColor(_lblStatusBadge, Color.FromArgb(6, 214, 160));
                        SetText(_lblStatusDesc, "Singleplayer / Solo | Unthrottled hardware throughput");
                    }
                    else
                    {
                        // Hosting multiplayer with clients
                        if (e.IsPaused)
                        {
                            SetText(_lblStatusBadge, "SPEED 5 (PAUSED)");
                            SetColor(_lblStatusBadge, Color.FromArgb(255, 209, 102));
                            SetText(_lblStatusDesc, string.Format("Primed at {0:F1}ms delay ({1:F1}s/month) for immediate smooth unpause",
                                e.MpBaseDelayMs, e.MpBaseDelayMs * 24f * 30f));
                        }
                        else
                        {
                            SetText(_lblStatusBadge, "ACTIVE: SPEED 5 HOSTING");
                            Color modeColor = (e.Mode == "ANTI-DESYNC-BRAKE")
                                ? Color.FromArgb(239, 71, 111) // Red
                                : (e.Mode == "CUSHION" ? Color.FromArgb(255, 209, 102) : Color.FromArgb(6, 214, 160)); // Green
                            SetColor(_lblStatusBadge, modeColor);
                            SetText(_lblStatusDesc, e.StateMessage);
                        }
                    }
                }

                if (e.CurrentSpeed == 5 && e.ConnectedClients > 0)
                {
                    SetText(_lblSpeedVal, string.Format("{0:F3} s/hr ({1:F1} ms) = {2:F2} s/day ({3:F1} s/month)",
                        e.CurrentDelayMs / 1000f, e.CurrentDelayMs, e.DaySeconds, e.DaySeconds * 30f));
                }
                else
                {
                    SetText(_lblSpeedVal, "0.000 s/hr (100% Unlocked)");
                }

                SetText(_lblSyncVal, string.Format("{0} Clients Connected | Max Lag: {1}h (Ping Floor: {2:F1}h)",
                    e.ConnectedClients, e.MaxLagHours, e.PingFloorHours));

                SetText(_lblModeVal, e.Mode);
                if (e.Mode == "ANTI-DESYNC-BRAKE")
                {
                    SetColor(_lblModeVal, Color.FromArgb(239, 71, 111));
                }
                else if (e.Mode == "CUSHION")
                {
                    SetColor(_lblModeVal, Color.FromArgb(255, 209, 102));
                }
                else
                {
                    SetColor(_lblModeVal, Color.FromArgb(6, 214, 160));
                }
            }

            // Update tray tooltip at most once every 1.5 seconds (avoids Windows Shell IPC churn)
            string trayTxt = string.Format("NSBMP: {0} ({1:F1}ms) | Lag: {2}h", e.Mode, e.CurrentDelayMs, e.MaxLagHours);
            UpdateTrayThrottled(trayTxt);
        }

        private void UpdateTrayThrottled(string text)
        {
            double now = (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
            if (now - _lastTrayUpdateTime >= 1.5)
            {
                if (text.Length > 63) text = text.Substring(0, 63);
                if (_trayIcon.Text != text) _trayIcon.Text = text;
                _lastTrayUpdateTime = now;
            }
        }

        private void ShowFromTray()
        {
            Show();
            WindowState = FormWindowState.Normal;
            BringToFront();
        }
    }
}
