using HHelper.Core.Broker;
using HHelper.Core.Platform;
using HHelper.Core.Profiles;
using HHelper.Core.Settings;
using HHelper.Core.Wmi;

namespace HHelper.App;

/// <summary>
/// Owns the broker and the single telemetry poll thread (spec 5.2: never poll from more than one
/// thread; 2 s while a window is open, 30 s otherwise).
/// </summary>
internal sealed class HardwareHost : IDisposable
{
    private static readonly TimeSpan FastInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SlowInterval = TimeSpan.FromSeconds(30);

    private readonly SettingsStore _settingsStore;
    private readonly AutoResetEvent _wake = new(false);
    private Thread? _thread;
    private volatile bool _stop;
    private volatile bool _uiAttached;
    private volatile bool _diagnosticsPending = true;

    public HardwareHost(SettingsStore settingsStore)
    {
        _settingsStore = settingsStore;
        Settings = settingsStore.Load();
    }

    public AppSettings Settings { get; }
    public SystemIdentity? Identity { get; private set; }
    public MsiBroker? Broker { get; private set; }

    /// <summary>One line describing why hardware access is or is not active.</summary>
    public string Status { get; private set; } = "Not started";

    public Telemetry? LastTelemetry { get; private set; }
    public Diagnostics? LastDiagnostics { get; private set; }

    public event Action<Telemetry>? TelemetryUpdated;
    public event Action<Diagnostics>? DiagnosticsUpdated;
    public event Action<MsiEvent>? EventLogged;
    public event Action<string>? Message;

    public void SaveSettings() => _settingsStore.Save(Settings);

    /// <summary>Model gate, broker start, and the poll thread. Never throws; failures end up in <see cref="Status"/>.</summary>
    public void Start(bool startPolling = true)
    {
        Identity = SystemInfo.Read();
        var profile = ProfileLoader.FindMatch(ProfileLoader.LoadBuiltIn(), Identity.BaseBoardProduct);
        if (profile is null)
        {
            Status = $"Unsupported model (board '{Identity.BaseBoardProduct ?? "unknown"}'). Nothing is read or written.";
            return;
        }

        var transport = new Win32WmiTransport(Settings.WmiInstanceBaseOverride);
        var events = new Win32WmiEventSource();
        var broker = new MsiBroker(
            profile,
            Identity.BaseBoardProduct!,
            transport,
            events,
            () => Settings,
            new FileSnapshotStore(FileSnapshotStore.DefaultDirectory),
            () => profile.Source.Graphics is { } g ? FirmwareVariables.TryRead(g.UefiVariable, Guid.Parse(g.UefiGuid)) : null);
        broker.Log += m => Message?.Invoke(m);
        broker.EventReceived += e =>
        {
            EventLogged?.Invoke(e);
            if (e.Group is not null) { _diagnosticsPending = true; _wake.Set(); }
        };

        try
        {
            broker.Start();
        }
        catch (WmiException e) when (e.IsAccessDenied)
        {
            broker.Dispose();
            Status = "Access denied by the MSI WMI blocks. Run H-Helper as administrator; if that is not enough, SYSTEM may be needed (spec Q2).";
            return;
        }
        catch (Exception e)
        {
            broker.Dispose();
            Status = $"Could not start hardware access: {e.Message}";
            return;
        }

        Broker = broker;
        Status = $"{profile.DisplayName} ({Identity.BaseBoardProduct}), profile {profile.Id}";

        if (startPolling)
        {
            _thread = new Thread(PollLoop) { IsBackground = true, Name = "H-Helper telemetry" };
            _thread.Start();
        }
    }

    public void SetUiAttached(bool attached)
    {
        _uiAttached = attached;
        _wake.Set();
    }

    public void RequestDiagnostics()
    {
        _diagnosticsPending = true;
        _wake.Set();
    }

    private void PollLoop()
    {
        while (!_stop)
        {
            try
            {
                if (_diagnosticsPending)
                {
                    _diagnosticsPending = false;
                    LastDiagnostics = Broker!.GetDiagnostics();
                    DiagnosticsUpdated?.Invoke(LastDiagnostics);
                }
                LastTelemetry = Broker!.GetTelemetry();
                TelemetryUpdated?.Invoke(LastTelemetry);
            }
            catch (Exception e)
            {
                Message?.Invoke($"Telemetry read failed: {e.Message}");
            }
            _wake.WaitOne(_uiAttached ? FastInterval : SlowInterval);
        }
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        _thread?.Join(TimeSpan.FromSeconds(5));
        if (Broker is { } b)
        {
            try { b.OnExit(); } catch (Exception) { /* best effort; the snapshot stays on disk for next start */ }
            b.Dispose();
        }
        _wake.Dispose();
    }
}
