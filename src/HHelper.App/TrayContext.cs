using HHelper.Core.Broker;

namespace HHelper.App;

internal sealed class TrayContext : ApplicationContext
{
    private readonly HardwareHost _host;
    private readonly NotifyIcon _tray;
    private readonly SynchronizationContext _ui;
    private MainForm? _form;

    public TrayContext(HardwareHost host)
    {
        _host = host;
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open H-Helper", null, (_, _) => ShowForm());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "H-Helper",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowForm(); };

        _host.TelemetryUpdated += t => _ui.Post(_ => UpdateTray(t), null);
        if (_host.Broker is null) ShowForm();
    }

    private void UpdateTray(Telemetry t)
    {
        var text = $"H-Helper\nCPU {Fmt(t.CpuTemperatureC)} · GPU/sys {Fmt(t.GpuTemperatureC)}";
        _tray.Text = text.Length > 127 ? text[..127] : text;
    }

    private static string Fmt(int? c) => c is int v ? $"{v} °C" : "n/a";

    private void ShowForm()
    {
        if (_form is null || _form.IsDisposed)
        {
            _form = new MainForm(_host);
            _form.VisibleChanged += (_, _) => _host.SetUiAttached(_form.Visible);
        }
        _form.Show();
        _form.WindowState = FormWindowState.Normal;
        _form.Activate();
    }

    protected override void ExitThreadCore()
    {
        _tray.Visible = false;
        _form?.AllowClose();
        _form?.Close();
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tray.Dispose();
        base.Dispose(disposing);
    }
}
