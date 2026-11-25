using System.Text;
using HHelper.Core.Broker;

namespace HHelper.App;

/// <summary>Main window. Layout is built in code (no designer files), per the design doc.</summary>
internal sealed class MainForm : Form
{
    private readonly HardwareHost _host;
    private readonly SynchronizationContext _ui;
    private readonly Dictionary<string, Label> _values = new();
    private readonly TextBox _diagnostics = ReadOnlyText();
    private readonly TextBox _log = ReadOnlyText();
    private readonly Dictionary<string, FanEditor> _fanEditors = new();
    private bool _allowClose;

    public MainForm(HardwareHost host)
    {
        _host = host;
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        Text = "H-Helper";
        Icon = SystemIcons.Application;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(640, 520);
        Size = new Size(760, 600);
        Font = new Font("Segoe UI", 9f);

        var status = new Label { Dock = DockStyle.Top, Height = 28, Padding = new Padding(8, 6, 8, 0), Text = host.Status, AutoEllipsis = true };
        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildOverview());
        tabs.TabPages.Add(BuildFans());
        tabs.TabPages.Add(BuildDiagnostics());
        tabs.TabPages.Add(BuildSettings());
        Controls.Add(tabs);
        Controls.Add(status);

        host.TelemetryUpdated += OnTelemetry;
        host.DiagnosticsUpdated += OnDiagnostics;
        host.EventLogged += OnEvent;
        host.Message += OnMessage;
        if (host.LastTelemetry is { } t) ShowTelemetry(t);
        host.RequestDiagnostics();
        RefreshDiagnosticsText();
        RefreshFanState();
    }

    public void AllowClose() => _allowClose = true;

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Closing the window keeps the tray app running; Exit is in the tray menu.
        if (!_allowClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        _host.TelemetryUpdated -= OnTelemetry;
        _host.DiagnosticsUpdated -= OnDiagnostics;
        _host.EventLogged -= OnEvent;
        _host.Message -= OnMessage;
        base.OnFormClosing(e);
    }

    // ---- Overview -----------------------------------------------------------------------

    private TabPage BuildOverview()
    {
        var page = new TabPage("Overview") { Padding = new Padding(12) };
        var grid = Grid(2);
        void Row(string key, string label)
        {
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 6, 24, 6) });
            var value = new Label { Text = "–", AutoSize = true, Margin = new Padding(0, 6, 0, 6), Font = new Font(Font, FontStyle.Bold) };
            grid.Controls.Add(value);
            _values[key] = value;
        }
        Row("cpuTemp", "CPU temperature");
        Row("gpuTemp", "GPU / system temperature");
        Row("cpuFan", "CPU fan speed (raw, fan-table scale)");
        Row("gpuFan", "GPU fan speed (raw, fan-table scale)");
        Row("cpuTach", "CPU fan tachometer (raw)");
        Row("gpuTach", "GPU fan tachometer (raw)");
        Row("power", "Power source (Windows)");
        Row("ecAc", "AC adapter (EC flag, cross-check)");
        Row("updated", "Last update");

        var note = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(680, 0),
            Margin = new Padding(0, 16, 0, 0),
            ForeColor = SystemColors.GrayText,
            Text = "Fan values and tachometer readings are shown raw: the RPM conversion and the fan speed scale are not confirmed for this model yet.",
        };
        grid.Controls.Add(note);
        grid.SetColumnSpan(note, 2);
        page.Controls.Add(grid);
        return page;
    }

    private void OnTelemetry(Telemetry t) => _ui.Post(_ => ShowTelemetry(t), null);

    private void ShowTelemetry(Telemetry t)
    {
        if (IsDisposed) return;
        _values["cpuTemp"].Text = t.CpuTemperatureC is int c ? $"{c} °C" : "n/a";
        _values["gpuTemp"].Text = t.GpuTemperatureC is int g ? $"{g} °C" : "n/a";
        _values["cpuFan"].Text = t.CpuFanSpeedRaw?.ToString() ?? "n/a";
        _values["gpuFan"].Text = t.GpuFanSpeedRaw?.ToString() ?? "n/a";
        _values["cpuTach"].Text = t.CpuFanTach.ToString();
        _values["gpuTach"].Text = t.GpuFanTach.ToString();
        var ps = SystemInformation.PowerStatus;
        _values["power"].Text = $"{(ps.PowerLineStatus == PowerLineStatus.Online ? "AC" : ps.PowerLineStatus == PowerLineStatus.Offline ? "Battery" : "Unknown")}, battery {ps.BatteryLifePercent:P0}";
        _values["ecAc"].Text = t.OnAc is bool ac ? (ac ? "connected" : "not connected") : "n/a";
        _values["updated"].Text = t.At.ToString("HH:mm:ss");
    }

    // ---- Fans ---------------------------------------------------------------------------

    private TabPage BuildFans()
    {
        var page = new TabPage("Fan curves") { Padding = new Padding(12), AutoScroll = true };
        var stack = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };

        stack.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(700, 0),
            Text = "The fan table is read from the laptop's embedded controller. Thresholds are the temperatures (°C) where the fan steps up; " +
                   "speed points are one per step. Editing is experimental and only possible when this model's profile allows it and experimental controls are on in Settings.",
        });

        if (_host.Broker is { } broker)
        {
            foreach (var fan in broker.Fans)
            {
                var label = broker.Profile.Source.FanCurves!.Fans[fan].Label;
                var editor = new FanEditor(fan, label, Reload, Apply, Restore);
                _fanEditors[fan] = editor;
                stack.Controls.Add(editor);
            }
        }
        else
        {
            stack.Controls.Add(new Label { AutoSize = true, Text = "Hardware access is not available.", Margin = new Padding(0, 12, 0, 0) });
        }

        page.Controls.Add(stack);
        return page;
    }

    private void RefreshFanState()
    {
        if (_host.Broker is not { } broker) return;
        var canWrite = broker.FanCurveWritesEnabledByProfile && broker.ExperimentalEnabled;
        var reason = !broker.FanCurveWritesEnabledByProfile
            ? broker.FanCurveWriteBlockedReason ?? "Fan curve writes are disabled for this model."
            : !broker.ExperimentalEnabled ? "Turn on experimental controls in Settings to edit." : "Experimental: changes are verified and the factory table is saved first.";
        foreach (var (fan, editor) in _fanEditors)
        {
            editor.SetWritable(canWrite, reason, broker.HasSnapshot(fan));
            Reload(fan);
        }
    }

    private async void Reload(string fan)
    {
        if (_host.Broker is not { } broker) return;
        try
        {
            var curve = await Task.Run(() => broker.GetFanCurve(fan));
            _fanEditors[fan].Show(curve);
        }
        catch (Exception e) { OnMessage($"Reading the {fan} fan table failed: {e.Message}"); }
    }

    private async void Apply(string fan, FanCurve curve)
    {
        if (_host.Broker is not { } broker) return;
        var answer = MessageBox.Show(this,
            "Write this fan table to the embedded controller?\n\nThe factory table is saved first and restored on exit unless you chose to keep the curve. " +
            "Watch temperatures afterwards; use Restore factory if anything looks wrong.",
            "Apply experimental fan curve", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
        if (answer != DialogResult.OK) return;
        try
        {
            await Task.Run(() => broker.SetFanCurve(fan, curve));
        }
        catch (Exception e)
        {
            MessageBox.Show(this, e.Message, "Fan curve not applied", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        RefreshFanState();
    }

    private async void Restore(string fan)
    {
        if (_host.Broker is not { } broker) return;
        try
        {
            var restored = await Task.Run(() => broker.RestoreFactory(fan));
            if (!restored) OnMessage($"No saved factory table for the {fan} fan, nothing to restore.");
        }
        catch (Exception e) { OnMessage($"Restore failed: {e.Message}"); }
        RefreshFanState();
    }

    // ---- Diagnostics --------------------------------------------------------------------

    private TabPage BuildDiagnostics()
    {
        var page = new TabPage("Diagnostics") { Padding = new Padding(8) };
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 300 };
        split.Panel1.Controls.Add(_diagnostics);
        split.Panel2.Controls.Add(_log);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 36, FlowDirection = FlowDirection.LeftToRight };
        var refresh = new Button { Text = "Refresh", AutoSize = true };
        refresh.Click += (_, _) => { _host.RequestDiagnostics(); RefreshDiagnosticsText(); };
        buttons.Controls.Add(refresh);
        page.Controls.Add(split);
        page.Controls.Add(buttons);
        return page;
    }

    private void OnDiagnostics(Diagnostics d) => _ui.Post(_ => RefreshDiagnosticsText(), null);

    private async void RefreshDiagnosticsText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"H-Helper {typeof(MainForm).Assembly.GetName().Version} · GPL-3.0-or-later");
        sb.AppendLine($"Status: {_host.Status}");
        if (_host.Identity is { } id)
        {
            sb.AppendLine($"Board: {id.BaseBoardManufacturer} {id.BaseBoardProduct}");
            sb.AppendLine($"System: {id.SystemProductName} (SKU {id.SystemSku})");
            sb.AppendLine($"BIOS: {id.BiosVersion} ({id.BiosReleaseDate})");
        }

        if (_host.Broker is { } broker)
        {
            try
            {
                var (d, battery) = await Task.Run(() => (_host.LastDiagnostics ?? broker.GetDiagnostics(), broker.GetBatteryExtras()));
                sb.AppendLine($"EC firmware: {d.EcFirmware ?? "n/a"}");
                sb.AppendLine($"WMI instance base: {d.WmiInstanceBase ?? "n/a"}");
                sb.AppendLine($"Graphics: {d.GraphicsMode} (read-only)");
                sb.AppendLine($"Mode A (raw): {Hex(d.ModeA)}   Mode B (raw): {Hex(d.ModeB)}");
                sb.AppendLine($"Device flags (raw): {Hex(d.DeviceFlags)}");
                sb.AppendLine($"Brightness level (raw): {Hex(d.DisplayBrightness)}");
                sb.AppendLine($"Win/Fn swap candidate (raw): {Hex(d.WinFnSwapCandidate)}");
                sb.AppendLine();
                sb.AppendLine("Main battery, raw values (units unconfirmed):");
                foreach (var v in battery.Main) sb.AppendLine($"  {v.Block} {v.Instance,2}: {v.Value?.ToString() ?? "n/a",7}   {v.Note}");
                var secondStatus = battery.Second.FirstOrDefault()?.Value;
                if (secondStatus is int s && s != 0)
                {
                    sb.AppendLine("Second battery, raw values:");
                    foreach (var v in battery.Second) sb.AppendLine($"  {v.Block} {v.Instance,2}: {v.Value?.ToString() ?? "n/a",7}");
                }
                else
                {
                    sb.AppendLine($"Second battery: not present (status {Hex(secondStatus)}).");
                }
            }
            catch (Exception e) { sb.AppendLine($"Diagnostics read failed: {e.Message}"); }
        }
        if (!IsDisposed) _diagnostics.Text = sb.ToString();
    }

    private static string Hex(int? v) => v is int x ? $"0x{x:X2}" : "n/a";

    private void OnEvent(MsiEvent e) => OnMessage($"Event {e}");

    private void OnMessage(string message) => _ui.Post(_ =>
    {
        if (IsDisposed) return;
        _log.AppendText($"{DateTime.Now:HH:mm:ss}  {message}{Environment.NewLine}");
    }, null);

    // ---- Settings -----------------------------------------------------------------------

    private TabPage BuildSettings()
    {
        var page = new TabPage("Settings") { Padding = new Padding(12) };
        var grid = Grid(1);
        var board = _host.Identity?.BaseBoardProduct ?? "unknown";

        grid.Controls.Add(new Label { AutoSize = true, Font = new Font(Font, FontStyle.Bold), Text = "Experimental controls" });
        grid.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(680, 0),
            Text = $"Experimental controls allow writes to the embedded controller for features the model profile marks as experimental. " +
                   $"They are off by default. To turn them on, type this laptop's board ID ({board}).",
        });
        var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        var input = new TextBox { Width = 160, Text = _host.Settings.ExperimentalControlsForBoard ?? "" };
        var apply = new Button { Text = "Save", AutoSize = true };
        var state = new Label { AutoSize = true, Margin = new Padding(8, 6, 0, 0) };
        void ShowState() => state.Text = _host.Settings.ExperimentalEnabledFor(_host.Identity?.BaseBoardProduct) ? "On for this laptop" : "Off";
        apply.Click += (_, _) =>
        {
            _host.Settings.ExperimentalControlsForBoard = string.IsNullOrWhiteSpace(input.Text) ? null : input.Text.Trim();
            _host.SaveSettings();
            ShowState();
            RefreshFanState();
        };
        ShowState();
        row.Controls.AddRange([input, apply, state]);
        grid.Controls.Add(row);

        var keep = new CheckBox { AutoSize = true, Text = "Keep a custom fan curve when H-Helper exits (otherwise the factory table is restored)", Checked = _host.Settings.KeepFanCurveOnExit, Margin = new Padding(0, 12, 0, 0) };
        keep.CheckedChanged += (_, _) => { _host.Settings.KeepFanCurveOnExit = keep.Checked; _host.SaveSettings(); };
        grid.Controls.Add(keep);

        grid.Controls.Add(new Label { AutoSize = true, Font = new Font(Font, FontStyle.Bold), Text = "Startup", Margin = new Padding(0, 20, 0, 0) });
        var autostart = new CheckBox { AutoSize = true, Text = "Start H-Helper when I sign in (scheduled task, runs elevated)", Checked = Autostart.IsEnabled() };
        autostart.CheckedChanged += (_, _) =>
        {
            try { Autostart.Set(autostart.Checked); }
            catch (Exception e) { OnMessage($"Changing autostart failed: {e.Message}"); }
        };
        grid.Controls.Add(autostart);

        page.Controls.Add(grid);
        return page;
    }

    // ---- Helpers ------------------------------------------------------------------------

    private static TableLayoutPanel Grid(int columns)
    {
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = columns, AutoSize = true, AutoScroll = true };
        for (var i = 0; i < columns; i++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        return grid;
    }

    private static TextBox ReadOnlyText() => new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Both,
        WordWrap = false,
        Font = new Font("Consolas", 9f),
    };
}

/// <summary>One fan's table: N thresholds and N+1 speed points as numeric inputs.</summary>
internal sealed class FanEditor : GroupBox
{
    private readonly string _fan;
    private readonly List<NumericUpDown> _thresholds = new();
    private readonly List<NumericUpDown> _speeds = new();
    private readonly TableLayoutPanel _grid = new() { AutoSize = true, Dock = DockStyle.Top };
    private readonly Button _apply = new() { Text = "Apply (experimental)", AutoSize = true, Enabled = false };
    private readonly Button _restore = new() { Text = "Restore factory", AutoSize = true, Enabled = false };
    private readonly Label _state = new() { AutoSize = true, MaximumSize = new Size(660, 0), ForeColor = SystemColors.GrayText };

    public FanEditor(string fan, string label, Action<string> reload, Action<string, FanCurve> apply, Action<string> restore)
    {
        _fan = fan;
        Text = label;
        Width = 700;
        AutoSize = true;
        Padding = new Padding(8);

        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Bottom, FlowDirection = FlowDirection.LeftToRight };
        var reloadButton = new Button { Text = "Reload", AutoSize = true };
        reloadButton.Click += (_, _) => reload(_fan);
        _apply.Click += (_, _) => apply(_fan, Current());
        _restore.Click += (_, _) => restore(_fan);
        buttons.Controls.AddRange([reloadButton, _apply, _restore]);

        var stack = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        stack.Controls.Add(_grid);
        stack.Controls.Add(_state);
        stack.Controls.Add(buttons);
        Controls.Add(stack);
    }

    public void Show(FanCurve curve)
    {
        if (_thresholds.Count != curve.Thresholds.Count || _speeds.Count != curve.Speeds.Count) Build(curve.Thresholds.Count, curve.Speeds.Count);
        for (var i = 0; i < curve.Thresholds.Count; i++) _thresholds[i].Value = curve.Thresholds[i];
        for (var i = 0; i < curve.Speeds.Count; i++) _speeds[i].Value = curve.Speeds[i];
    }

    public void SetWritable(bool writable, string reason, bool hasSnapshot)
    {
        foreach (var n in _thresholds.Concat(_speeds)) n.Enabled = writable;
        _apply.Enabled = writable;
        _restore.Enabled = hasSnapshot;
        _state.Text = hasSnapshot ? $"{reason} A factory table is saved for this fan." : reason;
    }

    private FanCurve Current() => new(
        _thresholds.Select(n => (byte)n.Value).ToArray(),
        _speeds.Select(n => (byte)n.Value).ToArray());

    private void Build(int thresholds, int speeds)
    {
        _grid.Controls.Clear();
        _thresholds.Clear();
        _speeds.Clear();
        _grid.ColumnCount = speeds + 1;
        _grid.Controls.Add(new Label { Text = "Up-threshold °C", AutoSize = true, Margin = new Padding(0, 6, 8, 0) }, 0, 0);
        _grid.Controls.Add(new Label { Text = "Speed point", AutoSize = true, Margin = new Padding(0, 6, 8, 0) }, 0, 1);
        for (var i = 0; i < speeds; i++)
        {
            if (i < thresholds)
            {
                var t = Number();
                _thresholds.Add(t);
                _grid.Controls.Add(t, i + 1, 0);
            }
            var s = Number();
            _speeds.Add(s);
            _grid.Controls.Add(s, i + 1, 1);
        }
    }

    private static NumericUpDown Number() => new() { Minimum = 0, Maximum = 255, Width = 56, ReadOnly = true, Increment = 1 };
}
