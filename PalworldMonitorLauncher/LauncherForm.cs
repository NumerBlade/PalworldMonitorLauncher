namespace PalworldMonitorLauncher;

internal sealed class LauncherForm : Form
{
    static readonly Color Bg = Color.FromArgb(0x0D, 0x0B, 0x10);
    static readonly Color Panel = Color.FromArgb(0x16, 0x12, 0x1C);
    static readonly Color Purple = Color.FromArgb(0x7B, 0x3F, 0xA0);
    static readonly Color Gold = Color.FromArgb(0xD4, 0xAF, 0x37);
    static readonly Color Muted = Color.FromArgb(0xA8, 0x9B, 0xB8);
    static readonly Color Ink = Color.FromArgb(0xF5, 0xE6, 0xC8);
    static readonly Color Warn = Color.FromArgb(0xE8, 0xA8, 0x4A);

    readonly string[] _args;
    readonly string _dll;
    readonly ListBox _monitors = new();
    readonly Label _status = new();
    readonly Label _hint = new();
    readonly Button _launch = new();
    readonly CheckBox _dontHide = new();
    readonly System.Windows.Forms.Timer _closeTimer = new() { Interval = 20_000 };
    List<MonitorInfo> _mons = [];
    bool _launchStarted;
    bool _ready;

    void Ui(Action action)
    {
        if (Program.Exiting || IsDisposed || !IsHandleCreated) return;
        try
        {
            if (InvokeRequired) BeginInvoke(action);
            else action();
        }
        catch (ObjectDisposedException) { /* shutting down */ }
        catch (InvalidOperationException) { /* handle gone */ }
    }

    public LauncherForm(string[] args, string dll)
    {
        _args = args;
        _dll = dll;

        Text = "Palworld Monitor";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(620, 340);
        BackColor = Bg;
        ForeColor = Ink;
        Font = new Font("Segoe UI", 9.75f, FontStyle.Regular, GraphicsUnit.Point);
        DoubleBuffered = true;

        var title = new Label
        {
            Text = "PALWORLD MONITOR",
            Font = new Font("Georgia", 18f, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = Gold,
            AutoSize = true,
            Location = new Point(28, 18),
        };

        var sub = new Label
        {
            Text = "Pick a display · game sees it as primary (Windows primary unchanged)",
            ForeColor = Muted,
            AutoSize = true,
            Location = new Point(30, 52),
        };

        var card = new Panel
        {
            BackColor = Panel,
            Location = new Point(24, 84),
            Size = new Size(572, 168),
        };
        card.Controls.Add(new Panel { BackColor = Purple, Dock = DockStyle.Left, Width = 4 });

        var pickLabel = new Label
        {
            Text = "TARGET DISPLAY",
            ForeColor = Gold,
            Font = new Font("Segoe UI", 8f, FontStyle.Bold),
            Location = new Point(18, 12),
            AutoSize = true,
        };

        // ListBox (not ComboBox): Flat DropDownList often fails to open / feels broken.
        _monitors.BorderStyle = BorderStyle.FixedSingle;
        _monitors.BackColor = Bg;
        _monitors.ForeColor = Ink;
        _monitors.Location = new Point(18, 34);
        _monitors.Size = new Size(536, 88);
        _monitors.Font = new Font("Segoe UI", 9f);
        _monitors.IntegralHeight = false;
        _monitors.HorizontalScrollbar = true;
        _monitors.SelectedIndexChanged += (_, _) =>
        {
            if (_monitors.SelectedItem is MonitorInfo)
            {
                SaveSelection();
                UpdateHint();
            }
        };

        _hint.ForeColor = Muted;
        _hint.Location = new Point(18, 128);
        _hint.Size = new Size(536, 32);

        card.Controls.Add(pickLabel);
        card.Controls.Add(_monitors);
        card.Controls.Add(_hint);

        _launch.Text = "LAUNCH";
        _launch.FlatStyle = FlatStyle.Flat;
        _launch.FlatAppearance.BorderColor = Gold;
        _launch.FlatAppearance.BorderSize = 1;
        _launch.BackColor = Purple;
        _launch.ForeColor = Ink;
        _launch.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
        _launch.Size = new Size(120, 36);
        _launch.Location = new Point(24, 268);
        _launch.Click += (_, _) => StartLaunch();

        _dontHide.Text = "Don't hide";
        _dontHide.ForeColor = Gold;
        _dontHide.BackColor = Bg;
        _dontHide.AutoSize = true;
        _dontHide.Location = new Point(160, 276);
        _dontHide.FlatStyle = FlatStyle.Flat;
        _dontHide.CheckedChanged += (_, _) =>
        {
            if (_dontHide.Checked)
            {
                _closeTimer.Stop();
                if (_ready)
                {
                    _status.Text = "Running - staying open";
                    _hint.ForeColor = Muted;
                    _hint.Text = "Don't hide is on. Close the window anytime; process stays until quit.";
                }
            }
            else if (_ready)
            {
                _status.Text = "Running - closing in 20s";
                _hint.ForeColor = Muted;
                _hint.Text = "Game is up. Window hides soon; process stays until quit.";
                _closeTimer.Stop();
                _closeTimer.Start();
            }
        };

        _status.ForeColor = Muted;
        _status.Location = new Point(320, 276);
        _status.Size = new Size(276, 24);
        _status.TextAlign = ContentAlignment.MiddleRight;

        _closeTimer.Tick += (_, _) =>
        {
            _closeTimer.Stop();
            if (_ready && !_dontHide.Checked) Hide();
        };

        Controls.Add(title);
        Controls.Add(sub);
        Controls.Add(card);
        Controls.Add(_launch);
        Controls.Add(_dontHide);
        Controls.Add(_status);

        Load += OnLoad;
        FormClosing += (_, e) =>
        {
            if (_launchStarted && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
        };
    }

    void OnLoad(object? sender, EventArgs e)
    {
        _mons = Monitors.List();
        if (_mons.Count == 0)
        {
            MessageBox.Show(this, "No monitors found.", "Palworld Monitor", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
            return;
        }

        _monitors.Items.Clear();
        foreach (var m in _mons) _monitors.Items.Add(m);

        var cfg = AppConfig.Load();
        var idx = 0;
        if (!string.IsNullOrWhiteSpace(cfg.TargetDevice))
        {
            for (int i = 0; i < _mons.Count; i++)
                if (string.Equals(_mons[i].Device, cfg.TargetDevice, StringComparison.OrdinalIgnoreCase))
                { idx = i; break; }
        }
        else
        {
            for (int i = 0; i < _mons.Count; i++)
                if (!_mons[i].Primary) { idx = i; break; }
        }
        _monitors.SelectedIndex = idx;
        UpdateHint();

        if (!cfg.Configured)
        {
            if (string.IsNullOrEmpty(GpuTopology.HintFor(_mons[idx], _mons)))
                _hint.Text = $"First run - {_mons.Count} display(s). Select one, then LAUNCH.";
            _status.Text = "Waiting for selection…";
            return;
        }

        if (string.IsNullOrEmpty(GpuTopology.HintFor(_mons[idx], _mons)))
            _hint.Text = $"{_mons.Count} displays · launching saved pick. Click list to change.";
        _launch.Visible = false;
        // Keep list enabled so both displays stay usable without a broken ComboBox / CHANGE gate.
        BeginInvoke(StartLaunch);
    }

    void UpdateHint()
    {
        if (_ready || _launchStarted) return;
        if (_monitors.SelectedItem is not MonitorInfo m) return;

        var warn = GpuTopology.HintFor(m, _mons);
        if (!string.IsNullOrEmpty(warn))
        {
            _hint.ForeColor = Warn;
            _hint.Text = warn;
            return;
        }

        _hint.ForeColor = Muted;
        _hint.Text = _mons.Count == 1
            ? "1 display found."
            : $"{_mons.Count} displays found - click one to select.";
    }

    void SaveSelection()
    {
        if (_monitors.SelectedItem is not MonitorInfo m) return;
        var cfg = AppConfig.Load();
        cfg.FakePrimary = true;
        cfg.TargetDevice = m.Device;
        cfg.Configured = true;
        cfg.Save();
        _status.Text = $"Saved {m.Device}";
    }

    void StartLaunch()
    {
        if (_launchStarted) return;
        if (_monitors.SelectedItem is not MonitorInfo)
        {
            _status.Text = "Select a display.";
            return;
        }

        SaveSelection();
        _launchStarted = true;
        _launch.Enabled = false;
        _status.Text = "Starting…";
        _status.ForeColor = Gold;

        var expected = (MonitorInfo)_monitors.SelectedItem;

        _ = Task.Run(() =>
        {
            try
            {
                var code = Program.RunLaunch(_args, _dll,
                    msg => Ui(() => { _status.Text = msg; }),
                    pid => Ui(() => OnGameReady(pid, expected)));

                // Game process ended (or launch failed). Tear down without racing UI dialogs.
                Program.Exiting = true;
                if (code != 0 && !_ready)
                {
                    Program.Exiting = false;
                    Ui(() =>
                    {
                        _status.ForeColor = Color.Salmon;
                        _status.Text = $"Failed ({code})";
                        _launch.Enabled = true;
                        _launch.Visible = true;
                        _launchStarted = false;
                    });
                }
                else
                {
                    Environment.Exit(0);
                }
            }
            catch (Exception ex)
            {
                Program.CrashLog.Write("RunLaunch", ex);
                Program.Exiting = false;
                Ui(() =>
                {
                    MessageBox.Show(this, ex.Message, "Palworld Monitor", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    _launchStarted = false;
                    _launch.Enabled = true;
                    _launch.Visible = true;
                });
            }
        });
    }

    void OnGameReady(int pid, MonitorInfo expected)
    {
        if (Program.Exiting) return;
        _ready = true;
        _closeTimer.Stop();
        _status.Text = "Waiting for shipping GPU…";
        _hint.ForeColor = Muted;
        _hint.Text = "5s settle, then check Palworld-Win64-Shipping.exe…";

        _ = Task.Run(() =>
        {
            var (hit, shippingPid) = RunGpuCheckWithWatchdog(pid);
            Ui(() => FinishGpuCheck(hit, expected, shippingPid));
        });
    }

    /// <summary>
    /// PDH can hang rarely. After the 5s settle, wait 5s more; if still not done,
    /// start another attempt. First completed attempt wins. Does not touch disk/config.
    /// </summary>
    static (GpuProcessHit? hit, int shippingPid) RunGpuCheckWithWatchdog(int pid)
    {
        GpuProcessHit? hit = null;
        var shippingPid = pid;
        var done = new ManualResetEventSlim(false);

        void Attempt()
        {
            try
            {
                var h = GpuProcessProbe.WaitForActiveGpu(
                    pid,
                    settleDelay: TimeSpan.FromSeconds(5),
                    timeoutAfterSettle: TimeSpan.FromSeconds(25));
                var sp = GpuProcessProbe.ResolveShippingPid(pid, TimeSpan.Zero);
                if (done.IsSet) return;
                hit = h;
                shippingPid = sp;
                done.Set();
            }
            catch (Exception ex)
            {
                Program.CrashLog.Write("GpuProbe", ex);
                // Leave unset so the watchdog can retry a hung/failed attempt.
            }
        }

        _ = Task.Run(Attempt);

        // settle (5s) + grace (5s). If still running, kick another full attempt, repeat.
        while (!Program.Exiting && !done.Wait(TimeSpan.FromSeconds(10)))
            _ = Task.Run(Attempt);

        return (hit, shippingPid);
    }

    void FinishGpuCheck(GpuProcessHit? hit, MonitorInfo expected, int shippingPid)
    {
        if (Program.Exiting || IsDisposed) return;

        var expectedName = string.IsNullOrEmpty(expected.AdapterName)
            ? "the chosen display's GPU"
            : expected.AdapterName;

        if (hit is null)
        {
            _status.ForeColor = Warn;
            _status.Text = _dontHide.Checked ? "Running - GPU unknown" : "Running - GPU unknown · closing in 20s";
            _hint.ForeColor = Muted;
            _hint.Text = $"Could not read GPU for Palworld-Win64-Shipping (pid {shippingPid}). Display GPU: {expectedName}";
            if (!_dontHide.Checked) _closeTimer.Start();
            return;
        }

        var actualName = GpuProcessProbe.ResolveName(hit.Value.AdapterLuid, _mons);
        var mismatch = !string.IsNullOrEmpty(expected.AdapterLuid) &&
                       !GpuProcessProbe.LuidsEqual(hit.Value.AdapterLuid, expected.AdapterLuid);
        var suppress = AppConfig.Load().SuppressGpuMismatchWarn;

        // Always show which GPU the shipping process is on (even if dialogs are suppressed).
        void ApplyGpuStatus(bool keepOpen)
        {
            if (mismatch)
            {
                _status.ForeColor = Warn;
                _status.Text = keepOpen
                    ? $"GPU: {actualName} (≠ display) - staying open"
                    : $"GPU: {actualName} (≠ display) - closing in 20s";
                _hint.ForeColor = Warn;
                _hint.Text = $"Process GPU: {actualName}  ·  Display GPU: {expectedName}";
            }
            else
            {
                _status.ForeColor = Gold;
                _status.Text = keepOpen
                    ? $"GPU: {actualName} - staying open"
                    : $"GPU: {actualName} - closing in 20s";
                _hint.ForeColor = Muted;
                _hint.Text = $"Process GPU: {actualName}  ·  Display GPU: {expectedName}";
            }
        }

        if (mismatch && !suppress)
        {
            ApplyGpuStatus(keepOpen: _dontHide.Checked);
            try
            {
                using var dlg = new GpuMismatchDialog(actualName, expectedName);
                dlg.ShowDialog(this);
                if (dlg.ResultChoice == GpuMismatchDialog.Choice.DontBother)
                {
                    var cfg = AppConfig.Load();
                    cfg.SuppressGpuMismatchWarn = true;
                    cfg.Save();
                }
                else if (dlg.ResultChoice == GpuMismatchDialog.Choice.Cancel)
                {
                    _dontHide.Checked = true;
                    _closeTimer.Stop();
                    ApplyGpuStatus(keepOpen: true);
                    return;
                }
            }
            catch (Exception ex)
            {
                Program.CrashLog.Write("GpuMismatchDialog", ex);
            }
        }
        else
        {
            ApplyGpuStatus(keepOpen: _dontHide.Checked);
        }

        if (!_dontHide.Checked)
            _closeTimer.Start();
    }
}
